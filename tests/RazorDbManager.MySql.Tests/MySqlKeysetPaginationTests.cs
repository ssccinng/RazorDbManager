using RazorDbManager.Core;
using RazorDbManager.MySql.Data;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlKeysetPaginationTests
{
    [Fact]
    public void DuplicateRowsWithoutSafeKeyAlwaysUseOffsetFallback()
    {
        DbTableMetadata table = Table(rowIdentityKey: null);
        DbRow duplicate = new(
            [DbValue.FromString("same"), DbValue.FromSignedInteger(1)],
            Identity: null);

        RowCursor? cursor = MySqlKeysetPagination.CreateNextCursor(
            table,
            [new DbSort("label"), new DbSort("value")],
            [duplicate, duplicate],
            lastRowSortValuesComplete: true);

        Assert.Null(cursor);
    }

    [Fact]
    public void TruncatedSortValueUsesOffsetFallbackEvenWithSafeKey()
    {
        DbKeyMetadata key = new("PRIMARY", DbKeyKind.Primary, ["value"], true);
        DbTableMetadata table = Table(key);
        DbRow row = new(
            [DbValue.FromString("truncated-preview"), DbValue.FromSignedInteger(1)],
            new RowIdentity("PRIMARY", new Dictionary<string, DbValue>
            {
                ["value"] = DbValue.FromSignedInteger(1),
            }));

        RowCursor? cursor = MySqlKeysetPagination.CreateNextCursor(
            table,
            [new DbSort("label"), new DbSort("value")],
            [row],
            lastRowSortValuesComplete: false);

        Assert.Null(cursor);
    }

    [Fact]
    public void CompleteUniqueSortProducesCursor()
    {
        DbKeyMetadata key = new("PRIMARY", DbKeyKind.Primary, ["value"], true);
        DbTableMetadata table = Table(key);
        DbRow row = new(
            [DbValue.FromString("complete"), DbValue.FromSignedInteger(1)],
            new RowIdentity("PRIMARY", new Dictionary<string, DbValue>
            {
                ["value"] = DbValue.FromSignedInteger(1),
            }));

        RowCursor? cursor = MySqlKeysetPagination.CreateNextCursor(
            table,
            [new DbSort("label"), new DbSort("value")],
            [row],
            lastRowSortValuesComplete: true);

        Assert.NotNull(cursor);
        Assert.Equal(["complete", "1"], cursor.Values.Select(value => value.Text));
    }

    private static DbTableMetadata Table(DbKeyMetadata? rowIdentityKey) => new(
        new DbObjectName("app", "items"),
        DbObjectKind.Table,
        [
            new DbColumnMetadata("label", 0, new DbTypeDescriptor("varchar(200)", DbDataKind.Text), true),
            new DbColumnMetadata("value", 1, new DbTypeDescriptor("bigint", DbDataKind.SignedInteger), false),
        ],
        rowIdentityKey is null ? [] : [rowIdentityKey],
        [],
        [],
        rowIdentityKey,
        "fingerprint");
}
