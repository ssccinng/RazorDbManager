using RazorDbManager.Components;
using RazorDbManager.Core;

namespace RazorDbManager.Tests;

public sealed class SqlResultCsvExporterTests
{
    [Fact]
    public void Build_PreservesCsvShapeAndBinaryWhileProtectingFormulas()
    {
        SqlStatementResult result = new(
            SqlStatementResultKind.ResultSet,
            [Column("name", DbDataKind.Text), Column("payload", DbDataKind.Binary), Column("nullable", DbDataKind.Text)],
            [
                [DbValue.FromString("Ada, \"Admin\""), DbValue.FromBinary([0, 1, 255]), DbValue.Null],
                [DbValue.FromString("=HYPERLINK(\"https://invalid\")"), DbValue.FromBinary([]), DbValue.FromString("line1\r\nline2")],
            ]);

        string csv = SqlResultCsvExporter.Build(result);

        Assert.Equal(
            "name,payload,nullable\r\n\"Ada, \"\"Admin\"\"\",AAH/,\\N\r\n\"'=HYPERLINK(\"\"https://invalid\"\")\",,\"line1\r\nline2\"\r\n",
            csv);
    }

    [Fact]
    public void Build_RejectsNonResultStatements()
    {
        SqlStatementResult result = new(SqlStatementResultKind.AffectedRows, [], [], 1);

        Assert.Throws<ArgumentException>(() => SqlResultCsvExporter.Build(result));
    }

    [Fact]
    public void Build_ProtectsUntrustedColumnNamesAndTextButPreservesNumericValues()
    {
        SqlStatementResult result = new(
            SqlStatementResultKind.ResultSet,
            [
                Column("=HYPERLINK(\"https://invalid\")", DbDataKind.Text),
                Column("amount", DbDataKind.Decimal),
                Column("text", DbDataKind.Text),
            ],
            [[
                DbValue.FromString("\n=1+1"),
                DbValue.FromDecimal("-123.45"),
                DbValue.FromString("-1+cmd|' /C calc'!A0"),
            ]]);

        string csv = SqlResultCsvExporter.Build(result);

        Assert.Equal(
            "\"'=HYPERLINK(\"\"https://invalid\"\")\",amount,text\r\n\"'\n=1+1\",-123.45,'-1+cmd|' /C calc'!A0\r\n",
            csv);
    }

    [Fact]
    public void Build_RejectsRowsThatDoNotMatchTheResultColumns()
    {
        SqlStatementResult result = new(
            SqlStatementResultKind.ResultSet,
            [Column("first", DbDataKind.Text), Column("second", DbDataKind.Text)],
            [[DbValue.FromString("only one")]]);

        Assert.Throws<ArgumentException>(() => SqlResultCsvExporter.Build(result));
    }

    [Fact]
    public void Build_KeepsNullAndLiteralNullTokenDistinctAndProtectsIndentedFormula()
    {
        SqlStatementResult result = new(
            SqlStatementResultKind.ResultSet,
            [Column("\\N", DbDataKind.Text), Column("value", DbDataKind.Text)],
            [[DbValue.FromString("\\N"), DbValue.FromString("  =1+1")], [DbValue.Null, DbValue.FromString("'literal")]]);

        string csv = SqlResultCsvExporter.Build(result);

        Assert.Equal("\\\\N,value\r\n\\\\N,'  =1+1\r\n\\N,''literal\r\n", csv);
    }

    private static DbColumnMetadata Column(string name, DbDataKind kind) =>
        new(name, 0, new DbTypeDescriptor(kind.ToString(), kind), true);
}
