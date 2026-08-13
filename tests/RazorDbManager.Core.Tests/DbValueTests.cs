using RazorDbManager.Core;

namespace RazorDbManager.Core.Tests;

public sealed class DbValueTests
{
    [Fact]
    public void Decimal_PreservesProviderPrecisionAsText()
    {
        const string value = "99999999999999999999999999999999999.123456789012345678901234567890";

        DbValue dbValue = DbValue.FromDecimal(value);

        Assert.Equal(DbValueKind.Decimal, dbValue.Kind);
        Assert.Equal(value, dbValue.Text);
    }

    [Fact]
    public void ProviderPreservingDateTime_CanRepresentZeroDate()
    {
        DbValue dbValue = DbValue.FromText(DbValueKind.DateTime, "0000-00-00 00:00:00");

        Assert.Equal("0000-00-00 00:00:00", dbValue.Text);
    }

    [Fact]
    public void Binary_DefensivelyCopiesInput()
    {
        byte[] source = [1, 2, 3];
        DbValue dbValue = DbValue.FromBinary(source);

        source[0] = 99;

        Assert.Equal(new byte[] { 1, 2, 3 }, dbValue.Binary.ToArray());
    }

    [Fact]
    public void BinaryEquality_UsesContentAndKind()
    {
        DbValue first = DbValue.FromBinary([1, 2, 3]);
        DbValue same = DbValue.FromBinary([1, 2, 3]);
        DbValue geometry = DbValue.FromBinary([1, 2, 3], DbValueKind.Geometry);

        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, geometry);
        Assert.NotEqual(first.ComputeHash(), geometry.ComputeHash());
    }

    [Fact]
    public void NullAndEmptyString_RemainDistinct()
    {
        DbValue empty = DbValue.FromString(string.Empty);

        Assert.True(DbValue.Null.IsNull);
        Assert.False(empty.IsNull);
        Assert.Equal(string.Empty, empty.Text);
        Assert.NotEqual(DbValue.Null, empty);
    }

    [Fact]
    public void EditValue_ModelsOmittedDefaultNullAndExplicitValue()
    {
        EditValue explicitValue = EditValue.FromValue(DbValue.FromString("value"));

        Assert.Equal(EditValueKind.Omitted, EditValue.Omitted.Kind);
        Assert.Equal(EditValueKind.Default, EditValue.Default.Kind);
        Assert.Equal(EditValueKind.Null, EditValue.Null.Kind);
        Assert.Equal(EditValueKind.Value, explicitValue.Kind);
        Assert.Equal("value", explicitValue.Value!.Text);
        Assert.Throws<ArgumentException>(() => EditValue.FromValue(DbValue.Null));
    }

    [Fact]
    public void FromText_RejectsBinaryAndNullKinds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DbValue.FromText(DbValueKind.Binary, "01"));
        Assert.Throws<ArgumentOutOfRangeException>(() => DbValue.FromText(DbValueKind.Null, string.Empty));
    }
}
