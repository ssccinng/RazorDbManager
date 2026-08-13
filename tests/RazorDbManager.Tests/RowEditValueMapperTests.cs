using RazorDbManager.Components;
using RazorDbManager.Core;

namespace RazorDbManager.Tests;

public sealed class RowEditValueMapperTests
{
    [Theory]
    [InlineData(DbDataKind.Binary)]
    [InlineData(DbDataKind.Geometry)]
    public void Build_BinaryColumnsAreAlwaysOmitted(DbDataKind kind)
    {
        DbColumnMetadata column = Column(kind);
        DbValue original = DbValue.FromBinary([1, 2, 3], kind == DbDataKind.Binary ? DbValueKind.Binary : DbValueKind.Geometry);

        EditValue result = RowEditValueMapper.Build(column, false, original, string.Empty, false, false);

        Assert.Equal(EditValueKind.Omitted, result.Kind);
    }

    [Fact]
    public void Build_UnchangedScalarIsOmitted()
    {
        DbColumnMetadata column = Column(DbDataKind.Decimal);
        DbValue original = DbValue.FromDecimal("12345678901234567890.123456789012345678901234567890");

        EditValue result = RowEditValueMapper.Build(column, false, original, original.Text!, false, false);

        Assert.Equal(EditValueKind.Omitted, result.Kind);
    }

    [Fact]
    public void Build_ChangedScalarPreservesExactText()
    {
        DbColumnMetadata column = Column(DbDataKind.Decimal);

        EditValue result = RowEditValueMapper.Build(column, false, DbValue.FromDecimal("1.0"), "1.000000000000000000000000000001", false, false);

        Assert.Equal(EditValueKind.Value, result.Kind);
        Assert.Equal("1.000000000000000000000000000001", result.Value!.Text);
    }

    [Fact]
    public void Build_UnchangedNullIsOmitted()
    {
        EditValue result = RowEditValueMapper.Build(Column(DbDataKind.Text), false, DbValue.Null, string.Empty, true, false);

        Assert.Equal(EditValueKind.Omitted, result.Kind);
    }

    private static DbColumnMetadata Column(DbDataKind kind) =>
        new("value", 0, new DbTypeDescriptor(kind.ToString(), kind), true);
}
