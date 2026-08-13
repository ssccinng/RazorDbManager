using System.Security.Cryptography;
using System.Text;
using MySqlConnector;
using RazorDbManager.Core;
using RazorDbManager.MySql.Configuration;
using RazorDbManager.MySql.Infrastructure;
using RazorDbManager.MySql.Metadata;

namespace RazorDbManager.MySql.Schema;

internal sealed class MySqlSchemaService(
    MySqlCredentialSource credentials,
    MySqlDatabaseGuard guard,
    MySqlMetadataService metadata,
    MySqlDdlGenerator generator)
{
    // Generated literals escape backslashes. Pin execution to that parser mode so values are
    // both injection-safe and lossless regardless of the server's configured sql_mode.
    internal const string NormalizeSqlModeStatement =
        "SET SESSION sql_mode = TRIM(BOTH ',' FROM REPLACE(CONCAT(',', @@SESSION.sql_mode, ','), ',NO_BACKSLASH_ESCAPES,', ','))";

    public async Task<DdlPreview> PreviewAsync(SchemaChangeRequest request, CancellationToken cancellationToken)
    {
        var converted = MySqlCoreDdlAdapter.Convert(request.Change);
        var target = MySqlCoreDdlAdapter.Target(request.Change);
        guard.EnsureAllowed(target.Schema);
        EnsureForeignKeyTargetsAllowed(request.Change);
        var currentTable = await CurrentTableAsync(request.Change, target, cancellationToken).ConfigureAwait(false);
        if (currentTable is not null)
            MySqlStructuredDdlValidator.ValidateAgainstTable(request.Change, currentTable);
        await ValidateReferencedColumnsAsync(request.Change, target, currentTable, cancellationToken).ConfigureAwait(false);
        var fingerprint = currentTable?.SchemaFingerprint ?? CreateFingerprint(target);
        var generated = generator.Generate(converted);
        string[] statements = [NormalizeSqlModeStatement, generated.Sql];
        string sqlHash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join('\n', statements))));
        return new DdlPreview(
            statements,
            generated.IsDestructive,
            generated.IsDestructive ? RazorDbCapability.ModifySchema | RazorDbCapability.DestructiveSchema : RazorDbCapability.ModifySchema,
            fingerprint,
            sqlHash,
            ["MySQL and MariaDB DDL may implicitly commit and cannot be promised to roll back."]);
    }

    private void EnsureForeignKeyTargetsAllowed(SchemaChange change)
    {
        switch (change)
        {
            case CreateTableChange create:
                foreach (var foreignKey in create.Table.ForeignKeys)
                {
                    guard.EnsureAllowed(foreignKey.ReferencedTable.Schema);
                }
                break;
            case AddForeignKeyChange add:
                guard.EnsureAllowed(add.ForeignKey.ReferencedTable.Schema);
                break;
        }
    }

    public async Task<DdlExecutionResult> ExecuteAsync(DdlExecutionRequest request, CancellationToken cancellationToken)
    {
        var preview = await PreviewAsync(new SchemaChangeRequest(request.DatabaseId, request.Change), cancellationToken).ConfigureAwait(false);
        if (!string.Equals(preview.SchemaFingerprint, request.ExpectedSchemaFingerprint, StringComparison.Ordinal)
            || !string.Equals(preview.SqlHash, request.ExpectedSqlHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The schema or generated SQL changed after preview. Preview again before execution.");
        }

        // Actor binding, expiry and single-use validation of ConfirmationToken is owned by the hosting authorization layer.
        if (string.IsNullOrWhiteSpace(request.ConfirmationToken))
        {
            throw new UnauthorizedAccessException("A valid operation confirmation token is required.");
        }

        await using var dataSource = await credentials.CreateDataSourceAsync(MySqlCredentialSlot.Schema, cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var statement in preview.Statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var target = MySqlCoreDdlAdapter.Target(request.Change);
        metadata.Invalidate(target);
        var postFingerprint = request.Change is DropTableChange
            ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"dropped:{target}:{DateTimeOffset.UtcNow:O}")))
            : (await metadata.GetTableAsync(
                request.Change is RenameTableChange rename ? new DbObjectName(rename.Table.Schema, rename.NewName) : target,
                true,
                cancellationToken).ConfigureAwait(false)).SchemaFingerprint;
        return new DdlExecutionResult(preview.Statements.Count, postFingerprint, DateTimeOffset.UtcNow);
    }

    private async Task<DbTableMetadata?> CurrentTableAsync(
        SchemaChange change,
        DbObjectName target,
        CancellationToken cancellationToken)
    {
        if (change is CreateTableChange)
        {
            try
            {
                _ = await metadata.GetTableAsync(target, true, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"Table '{target}' already exists.");
            }
            catch (KeyNotFoundException)
            {
                return null;
            }
        }

        return await metadata.GetTableAsync(target, true, cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateReferencedColumnsAsync(
        SchemaChange change,
        DbObjectName target,
        DbTableMetadata? currentTable,
        CancellationToken cancellationToken)
    {
        IEnumerable<ForeignKeyDefinition> foreignKeys = change switch
        {
            CreateTableChange create => create.Table.ForeignKeys,
            AddForeignKeyChange add => [add.ForeignKey],
            _ => [],
        };
        foreach (var foreignKey in foreignKeys)
        {
            IReadOnlyCollection<string> referencedColumns;
            if (MySqlStructuredDdlValidator.SameObject(foreignKey.ReferencedTable, target))
            {
                referencedColumns = change is CreateTableChange create
                    ? create.Table.Columns.Select(column => column.Name).ToArray()
                    : currentTable!.Columns.Select(column => column.Name).ToArray();
            }
            else
            {
                var referenced = await metadata.GetTableAsync(
                    foreignKey.ReferencedTable,
                    true,
                    cancellationToken).ConfigureAwait(false);
                referencedColumns = referenced.Columns.Select(column => column.Name).ToArray();
            }

            MySqlStructuredDdlValidator.ValidateReferencedColumns(foreignKey, referencedColumns);
        }
    }

    private static string CreateFingerprint(DbObjectName target) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"create:{target}")));
}
