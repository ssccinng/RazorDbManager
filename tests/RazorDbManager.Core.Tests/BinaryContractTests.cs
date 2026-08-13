using RazorDbManager.Core;

namespace RazorDbManager.Core.Tests;

public sealed class BinaryContractTests
{
    [Fact]
    public void Request_RejectsNullIdentityValue()
    {
        BinaryCellRequest request = new(
            "Main",
            new DbObjectName("app", "files"),
            "payload",
            new RowIdentity("PRIMARY", new Dictionary<string, DbValue> { ["id"] = DbValue.Null }));

        Assert.Throws<ArgumentException>(request.Validate);
    }

    [Fact]
    public void Row_UsesSeparateBinaryIdentityWhenEditableSnapshotIsUnavailable()
    {
        RowIdentity identity = new("PRIMARY", new Dictionary<string, DbValue>
        {
            ["id"] = DbValue.FromSignedInteger(7),
        });
        DbRow row = new([DbValue.FromBinary([1, 2, 3])], null) { BinaryIdentity = identity };

        Assert.Null(row.Identity);
        Assert.Same(identity, row.EffectiveBinaryIdentity);
    }
}
