using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MySqlConnector;
using RazorDbManager.Core;
using RazorDbManager.MySql.Configuration;
using RazorDbManager.MySql.Infrastructure;

namespace RazorDbManager.MySql.Metadata;

internal sealed class MySqlMetadataService(
    string databaseId,
    DatabaseRegistration registration,
    MySqlProviderOptions options,
    MySqlCredentialSource credentials,
    MySqlDatabaseGuard guard)
{
    private readonly ConcurrentDictionary<string, CacheEntry<DbTableMetadata>> _tables =
        new(StringComparer.OrdinalIgnoreCase);
    private CacheEntry<DatabaseMetadata>? _database;

    public async Task<DatabaseMetadata> GetDatabaseAsync(bool refresh, CancellationToken cancellationToken)
    {
        var cached = _database;
        if (!refresh && cached is not null && !cached.IsExpired)
        {
            return cached.Value;
        }

        await using var dataSource = await credentials.CreateDataSourceAsync(MySqlCredentialSlot.Reader, cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var (product, version) = await ReadVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        var schemas = await ReadSchemasAsync(connection, cancellationToken).ConfigureAwait(false);
        string? defaultSchema = ResolveDefaultSchema(connection.Database, schemas);
        var metadata = new DatabaseMetadata(
            databaseId,
            product,
            version,
            defaultSchema,
            schemas,
            DateTimeOffset.UtcNow,
            GetConfiguredCapabilities(registration, options));
        _database = new CacheEntry<DatabaseMetadata>(metadata, CacheExpiry());
        return metadata;
    }

    internal static string? ResolveDefaultSchema(
        string? connectionDatabase,
        IReadOnlyList<DbSchemaMetadata> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        if (!string.IsNullOrWhiteSpace(connectionDatabase))
        {
            string? allowed = schemas.Select(schema => schema.Name).FirstOrDefault(schema =>
                string.Equals(schema, connectionDatabase, StringComparison.OrdinalIgnoreCase));
            if (allowed is not null) return allowed;
        }

        return schemas.Count == 1 ? schemas[0].Name : null;
    }

    public async Task<DbTableMetadata> GetTableAsync(DbObjectName table, bool refresh, CancellationToken cancellationToken)
    {
        guard.EnsureAllowed(table.Schema);
        var cacheKey = table.ToString();
        if (!refresh && _tables.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
        {
            return cached.Value;
        }

        await using var dataSource = await credentials.CreateDataSourceAsync(MySqlCredentialSlot.Reader, cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var metadata = await ReadTableAsync(connection, table, cancellationToken).ConfigureAwait(false);
        _tables[cacheKey] = new CacheEntry<DbTableMetadata>(metadata, CacheExpiry());
        return metadata;
    }

    public void Invalidate(DbObjectName table)
    {
        _tables.TryRemove(table.ToString(), out _);
        _database = null;
    }

    private async Task<IReadOnlyList<DbSchemaMetadata>> ReadSchemasAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE, TABLE_ROWS, TABLE_COMMENT
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA IN ({0})
            ORDER BY TABLE_SCHEMA, TABLE_NAME
            """;
        var allowed = MySqlDatabaseGuard.ResolveAllowedSchemas(options,
            await credentials.GetConnectionStringAsync(MySqlCredentialSlot.Reader, cancellationToken).ConfigureAwait(false));
        await using var command = connection.CreateCommand();
        var names = new List<string>();
        var index = 0;
        foreach (var schema in allowed)
        {
            var parameterName = $"@schema{index++}";
            names.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, schema);
        }

        command.CommandText = string.Format(System.Globalization.CultureInfo.InvariantCulture, sql, string.Join(",", names));
        var objectsBySchema = allowed.ToDictionary(schema => schema, _ => new List<DbObjectSummary>(), StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            if (!objectsBySchema.TryGetValue(schema, out var objects)) continue;
            objects.Add(new DbObjectSummary(
                new DbObjectName(schema, reader.GetString(1)),
                reader.GetString(2).Equals("VIEW", StringComparison.OrdinalIgnoreCase) ? DbObjectKind.View : DbObjectKind.Table,
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return objectsBySchema
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new DbSchemaMetadata(pair.Key, pair.Value))
            .ToArray();
    }

    private static async Task<(string Product, string Version)> ReadVersionAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT VERSION()";
        var version = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        return (version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) ? "MariaDB" : "MySQL", version);
    }

    internal static RazorDbCapability GetConfiguredCapabilities(
        DatabaseRegistration registration,
        MySqlProviderOptions options)
    {
        var capabilities = registration.EnabledCapabilities;
        var writerAvailable = !string.IsNullOrWhiteSpace(options.WriterConnectionStringName)
            || !string.IsNullOrWhiteSpace(options.ConnectionStringName);
        if (!writerAvailable)
        {
            capabilities &= ~(RazorDbCapability.InsertRows
                | RazorDbCapability.UpdateRows
                | RazorDbCapability.DeleteRows
                | RazorDbCapability.Import);
        }

        var schemaAvailable = !string.IsNullOrWhiteSpace(options.SchemaConnectionStringName)
            || options.AllowSharedHighRiskCredential && writerAvailable;
        if (!schemaAvailable)
        {
            capabilities &= ~(RazorDbCapability.ModifySchema | RazorDbCapability.DestructiveSchema);
        }

        var sqlAvailable = !string.IsNullOrWhiteSpace(options.SqlConsoleConnectionStringName)
            || options.AllowSharedHighRiskCredential && writerAvailable;
        if (!sqlAvailable)
        {
            capabilities &= ~RazorDbCapability.ExecuteSql;
        }

        return capabilities;
    }

    private static async Task<DbTableMetadata> ReadTableAsync(
        MySqlConnection connection,
        DbObjectName table,
        CancellationToken cancellationToken)
    {
        var tableInfo = await ReadTableInfoAsync(connection, table, cancellationToken).ConfigureAwait(false);
        var columns = await ReadColumnsAsync(connection, table, cancellationToken).ConfigureAwait(false);
        var indexes = await ReadIndexesAsync(connection, table, cancellationToken).ConfigureAwait(false);
        var foreignKeys = await ReadForeignKeysAsync(connection, table, cancellationToken).ConfigureAwait(false);
        var columnMap = columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var keys = indexes
            .Where(index => index.IsPrimary || index.IsUnique)
            .Select(index => new DbKeyMetadata(
                index.Name,
                index.IsPrimary ? DbKeyKind.Primary : DbKeyKind.Unique,
                index.Columns.Select(column => column.Name).ToArray(),
                index.Columns.All(column => column.PrefixLength is null
                    && columnMap.TryGetValue(column.Name, out var metadata)
                    && !metadata.IsNullable)))
            .ToArray();
        var identity = keys.FirstOrDefault(key => key.Kind == DbKeyKind.Primary)
            ?? keys.Where(key => key.IsUsableForRowIdentity)
                .OrderBy(key => key.Columns.Count)
                .ThenBy(key => key.Name, StringComparer.Ordinal)
                .FirstOrDefault();
        var fingerprint = Fingerprint(
            table,
            tableInfo.Kind,
            tableInfo.Engine,
            tableInfo.Collation,
            tableInfo.Comment,
            columns,
            indexes,
            foreignKeys);
        return new DbTableMetadata(
            table,
            tableInfo.Kind,
            columns,
            keys,
            indexes,
            foreignKeys,
            identity,
            fingerprint,
            tableInfo.Engine,
            tableInfo.Collation,
            tableInfo.Comment);
    }

    private static async Task<(DbObjectKind Kind, string? Engine, string? Collation, string? Comment)> ReadTableInfoAsync(
        MySqlConnection connection,
        DbObjectName table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TABLE_TYPE, ENGINE, TABLE_COLLATION, TABLE_COMMENT
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            """;
        command.Parameters.AddWithValue("@schema", table.Schema);
        command.Parameters.AddWithValue("@table", table.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"Table or view '{table}' was not found.");
        }

        return (
            reader.GetString(0).Equals("VIEW", StringComparison.OrdinalIgnoreCase) ? DbObjectKind.View : DbObjectKind.Table,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static async Task<IReadOnlyList<DbColumnMetadata>> ReadColumnsAsync(
        MySqlConnection connection,
        DbObjectName table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COLUMN_NAME, ORDINAL_POSITION, DATA_TYPE, COLUMN_TYPE, IS_NULLABLE,
                   COLUMN_DEFAULT, EXTRA, COLUMN_COMMENT, CHARACTER_SET_NAME, COLLATION_NAME,
                   CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, GENERATION_EXPRESSION
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION
            """;
        command.Parameters.AddWithValue("@schema", table.Schema);
        command.Parameters.AddWithValue("@table", table.Name);
        var columns = new List<DbColumnMetadata>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dataType = reader.GetString(2);
            var columnType = reader.GetString(3);
            long? length = reader.IsDBNull(10) ? null : reader.GetInt64(10);
            int? precision = reader.IsDBNull(11) ? null : reader.GetInt32(11);
            int? scale = reader.IsDBNull(12) ? null : reader.GetInt32(12);
            var extra = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
            columns.Add(new DbColumnMetadata(
                reader.GetString(0),
                reader.GetInt32(1) - 1,
                MySqlTypeMapper.Map(dataType, columnType, length, precision, scale),
                reader.GetString(4).Equals("YES", StringComparison.OrdinalIgnoreCase),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase) || !reader.IsDBNull(13) && reader.GetString(13).Length > 0,
                extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        if (columns.Count == 0)
        {
            throw new KeyNotFoundException($"Table or view '{table}' has no visible columns or does not exist.");
        }

        return columns;
    }

    private static async Task<IReadOnlyList<DbIndexMetadata>> ReadIndexesAsync(
        MySqlConnection connection,
        DbObjectName table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT INDEX_NAME, NON_UNIQUE, INDEX_TYPE, SEQ_IN_INDEX, COLUMN_NAME,
                   SUB_PART, COLLATION
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            ORDER BY INDEX_NAME, SEQ_IN_INDEX
            """;
        command.Parameters.AddWithValue("@schema", table.Schema);
        command.Parameters.AddWithValue("@table", table.Name);
        var rows = new List<(string Name, bool Unique, string Method, int Sequence, string Column, int? Prefix, bool Descending)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(4)) continue; // Functional indexes are display-only until the Core model can represent expressions.
            rows.Add((
                reader.GetString(0),
                reader.GetInt32(1) == 0,
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                !reader.IsDBNull(6) && reader.GetString(6).Equals("D", StringComparison.OrdinalIgnoreCase)));
        }

        return rows.GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DbIndexMetadata(
                group.Key,
                group.OrderBy(row => row.Sequence)
                    .Select(row => new DbIndexColumn(row.Column, row.Descending, row.Prefix))
                    .ToArray(),
                group.First().Unique,
                group.Key.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase),
                group.First().Method))
            .ToArray();
    }

    private static async Task<IReadOnlyList<DbForeignKeyMetadata>> ReadForeignKeysAsync(
        MySqlConnection connection,
        DbObjectName table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT k.CONSTRAINT_NAME, k.COLUMN_NAME, k.ORDINAL_POSITION,
                   k.REFERENCED_TABLE_SCHEMA, k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME,
                   r.UPDATE_RULE, r.DELETE_RULE
            FROM information_schema.KEY_COLUMN_USAGE AS k
            JOIN information_schema.REFERENTIAL_CONSTRAINTS AS r
              ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA
             AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME
             AND r.TABLE_NAME = k.TABLE_NAME
            WHERE k.TABLE_SCHEMA = @schema AND k.TABLE_NAME = @table
              AND k.REFERENCED_TABLE_NAME IS NOT NULL
            ORDER BY k.CONSTRAINT_NAME, k.ORDINAL_POSITION
            """;
        command.Parameters.AddWithValue("@schema", table.Schema);
        command.Parameters.AddWithValue("@table", table.Name);
        var rows = new List<(string Name, string Column, int Sequence, string RefSchema, string RefTable, string RefColumn, string Update, string Delete)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        }

        return rows.GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new DbForeignKeyMetadata(
                    group.Key,
                    group.OrderBy(row => row.Sequence).Select(row => row.Column).ToArray(),
                    new DbObjectName(first.RefSchema, first.RefTable),
                    group.OrderBy(row => row.Sequence).Select(row => row.RefColumn).ToArray(),
                    Action(first.Delete),
                    Action(first.Update));
            })
            .ToArray();
    }

    private static DbReferentialAction Action(string value) => value.ToUpperInvariant() switch
    {
        "RESTRICT" => DbReferentialAction.Restrict,
        "CASCADE" => DbReferentialAction.Cascade,
        "SET NULL" => DbReferentialAction.SetNull,
        "SET DEFAULT" => DbReferentialAction.SetDefault,
        _ => DbReferentialAction.NoAction,
    };

    internal static string Fingerprint(
        DbObjectName table,
        DbObjectKind kind,
        string? engine,
        string? tableCollation,
        string? tableComment,
        IReadOnlyList<DbColumnMetadata> columns,
        IReadOnlyList<DbIndexMetadata> indexes,
        IReadOnlyList<DbForeignKeyMetadata> foreignKeys)
    {
        var canonical = new StringBuilder();
        Append(table.Schema);
        Append(table.Name);
        Append(kind.ToString());
        Append(engine);
        Append(tableCollation);
        Append(tableComment);
        foreach (var column in columns)
        {
            Append("column");
            Append(column.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(column.Name);
            Append(column.Type.ProviderTypeName);
            Append(column.Type.Kind.ToString());
            Append(column.Type.IsUnsigned.ToString());
            Append(column.Type.Length?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(column.Type.Precision?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(column.Type.Scale?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var allowedValue in column.Type.AllowedValues ?? []) Append(allowedValue);
            Append(column.IsNullable.ToString());
            Append(column.DefaultSql);
            Append(column.IsGenerated.ToString());
            Append(column.IsAutoIncrement.ToString());
            Append(column.CharacterSet);
            Append(column.Collation);
            Append(column.Comment);
        }

        foreach (var index in indexes.OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            Append("index");
            Append(index.Name);
            Append(index.IsUnique.ToString());
            Append(index.IsPrimary.ToString());
            Append(index.Method);
            foreach (var column in index.Columns)
            {
                Append(column.Name);
                Append(column.Descending.ToString());
                Append(column.PrefixLength?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        foreach (var foreignKey in foreignKeys.OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            Append("foreign-key");
            Append(foreignKey.Name);
            foreach (var column in foreignKey.Columns) Append(column);
            Append(foreignKey.ReferencedTable.Schema);
            Append(foreignKey.ReferencedTable.Name);
            foreach (var column in foreignKey.ReferencedColumns) Append(column);
            Append(foreignKey.OnDelete.ToString());
            Append(foreignKey.OnUpdate.ToString());
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));

        void Append(string? value)
        {
            canonical.Append(value?.Length ?? -1).Append(':');
            if (value is not null) canonical.Append(value);
            canonical.Append(';');
        }
    }

    private DateTimeOffset CacheExpiry() => DateTimeOffset.UtcNow.AddSeconds(options.MetadataCacheSeconds);

    private sealed record CacheEntry<T>(T Value, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }
}
