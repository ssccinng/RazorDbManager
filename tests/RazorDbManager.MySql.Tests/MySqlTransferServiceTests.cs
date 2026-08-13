using RazorDbManager.Core;
using RazorDbManager.MySql.Data;
using RazorDbManager.MySql.Transfer;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlTransferServiceTests
{
    private static readonly DbColumnMetadata BinaryColumn = new(
        "payload",
        0,
        new DbTypeDescriptor("varbinary(32)", DbDataKind.Binary),
        true);

    [Fact]
    public void ParseCsvField_DecodesBinaryBase64AndKeepsNullDistinct()
    {
        Assert.Equal(new byte[] { 0, 1, 255 },
            Assert.IsType<byte[]>(MySqlTransferService.ParseCsvField(BinaryColumn, "AAH/", "\\N")));
        Assert.Empty(Assert.IsType<byte[]>(MySqlTransferService.ParseCsvField(BinaryColumn, string.Empty, "\\N")));
        Assert.Equal(DBNull.Value, MySqlTransferService.ParseCsvField(BinaryColumn, "\\N", "\\N"));
    }

    [Fact]
    public void ParseCsvField_RejectsInvalidBinaryWithoutEchoingValue()
    {
        const string invalid = "not-base64-secret";

        RazorDbException exception = Assert.Throws<RazorDbException>(() =>
            MySqlTransferService.ParseCsvField(BinaryColumn, invalid, "\\N"));

        Assert.Equal(RazorDbErrorCode.Validation, exception.Code);
        Assert.Contains("payload", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(invalid, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCsvField_DoesNotDecodeTextColumns()
    {
        DbColumnMetadata textColumn = new(
            "content",
            0,
            new DbTypeDescriptor("varchar(100)", DbDataKind.Text),
            true);

        Assert.Equal("AAH/", MySqlTransferService.ParseCsvField(textColumn, "AAH/", "\\N"));
    }

    [Theory]
    [InlineData("\\N")]
    [InlineData("\\\\N")]
    [InlineData("\\\\\\N")]
    [InlineData("=SUM(A1:A2)")]
    [InlineData("+1")]
    [InlineData("@command")]
    [InlineData("  =SUM(A1:A2)")]
    [InlineData("'=literal")]
    [InlineData("''already-quoted")]
    public void CsvTextEncoding_IsSpreadsheetSafeAndRoundTrips(string original)
    {
        DbColumnMetadata textColumn = new(
            "content",
            0,
            new DbTypeDescriptor("varchar(100)", DbDataKind.Text),
            true);
        DbValue value = DbValue.FromString(original);

        string encoded = MySqlTransferService.EncodeCsvField(value, "\\N");
        object decoded = MySqlTransferService.ParseCsvField(textColumn, encoded, "\\N", decodeProtectedValues: true);

        Assert.Equal(original, Assert.IsType<string>(decoded));
        if (original[0] is '=' or '+' or '@') Assert.StartsWith("'", encoded, StringComparison.Ordinal);
    }

    [Fact]
    public void CsvEncoding_KeepsNullEmptyAndLiteralNullTokenDistinct()
    {
        DbColumnMetadata textColumn = new(
            "content",
            0,
            new DbTypeDescriptor("varchar(100)", DbDataKind.Text),
            true);

        string nullValue = MySqlTransferService.EncodeCsvField(DbValue.Null, "\\N");
        string emptyValue = MySqlTransferService.EncodeCsvField(DbValue.FromString(string.Empty), "\\N");
        string literalToken = MySqlTransferService.EncodeCsvField(DbValue.FromString("\\N"), "\\N");

        Assert.Equal(DBNull.Value, MySqlTransferService.ParseCsvField(textColumn, nullValue, "\\N"));
        Assert.Equal(string.Empty, MySqlTransferService.ParseCsvField(textColumn, emptyValue, "\\N"));
        Assert.Equal("\\N", MySqlTransferService.ParseCsvField(textColumn, literalToken, "\\N", decodeProtectedValues: true));
        Assert.NotEqual(nullValue, literalToken);
    }

    [Theory]
    [InlineData("=calculated")]
    [InlineData("@external")]
    [InlineData("\\N")]
    [InlineData("'literal")]
    public void CsvHeaderEncoding_IsSafeAndUsesTheSameReversibleConvention(string columnName)
    {
        string encoded = MySqlTransferService.EncodeCsvField(DbValue.FromString(columnName), "\\N");
        DbColumnMetadata column = new(
            "header",
            0,
            new DbTypeDescriptor("varchar(100)", DbDataKind.Text),
            false);

        Assert.Equal(columnName, MySqlTransferService.ParseCsvField(column, encoded, "\\N", decodeProtectedValues: true));
        if (columnName[0] is '=' or '@') Assert.StartsWith("'", encoded, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\\\\server\\share")]
    [InlineData("''already-quoted")]
    [InlineData("'=SUM(A1:A2)")]
    public void ParseCsvField_RawImportPreservesExternalText(string original)
    {
        DbColumnMetadata textColumn = new(
            "content",
            0,
            new DbTypeDescriptor("varchar(100)", DbDataKind.Text),
            true);

        Assert.Equal(original, MySqlTransferService.ParseCsvField(
            textColumn,
            original,
            "\\N",
            decodeProtectedValues: false));
    }

    [Fact]
    public void CsvExportSelection_UsesRequestedColumnsFilterParametersAndStableSort()
    {
        DbTableMetadata table = Table();
        DbColumnMetadata[] columns = MySqlTransferService.SelectExportColumns(table, ["name", "id"]);
        CompiledSql filter = MySqlCoreFilterCompiler.Compile(
            new ComparisonFilter("name", DbComparisonOperator.Contains, DbValue.FromString("A%_")),
            table.Columns.Select(column => column.Name).ToArray());
        IReadOnlyList<DbSort> sorts = MySqlTransferService.BuildExportSorts(
            [new DbSort("name", DbSortDirection.Descending)], table);

        string sql = MySqlTransferService.BuildCsvSelectSql(table.Name, columns, filter.Text, sorts);

        Assert.Equal(["name", "id"], columns.Select(column => column.Name));
        Assert.Equal("%A\\%\\_%", filter.Parameters[0].Value);
        Assert.Contains("SELECT `name`, `id` FROM `app`.`items`", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE `name` LIKE @p0 ESCAPE '\\\\'", sql, StringComparison.Ordinal);
        Assert.EndsWith("ORDER BY `name` DESC, `id` ASC", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CsvExportSelection_RejectsUnknownDuplicateAndGeneratedColumns()
    {
        DbTableMetadata table = Table();

        Assert.Equal(RazorDbErrorCode.Validation, Assert.Throws<RazorDbException>(
            () => MySqlTransferService.SelectExportColumns(table, ["missing"])).Code);
        Assert.Equal(RazorDbErrorCode.Validation, Assert.Throws<RazorDbException>(
            () => MySqlTransferService.SelectExportColumns(table, ["id", "ID"])).Code);
        Assert.Equal(RazorDbErrorCode.Validation, Assert.Throws<RazorDbException>(
            () => MySqlTransferService.SelectExportColumns(table, ["computed"])).Code);
    }

    private static DbTableMetadata Table()
    {
        DbKeyMetadata key = new("PRIMARY", DbKeyKind.Primary, ["id"], true);
        return new DbTableMetadata(
            new DbObjectName("app", "items"),
            DbObjectKind.Table,
            [
                new DbColumnMetadata("id", 0, new DbTypeDescriptor("bigint", DbDataKind.SignedInteger), false),
                new DbColumnMetadata("name", 1, new DbTypeDescriptor("varchar(100)", DbDataKind.Text), true),
                new DbColumnMetadata("computed", 2, new DbTypeDescriptor("int", DbDataKind.SignedInteger), false, IsGenerated: true),
            ],
            [key], [], [], key, "fingerprint");
    }
}
