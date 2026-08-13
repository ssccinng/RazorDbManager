using RazorDbManager.MySql.Models;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlRowIdentityTests
{
    [Fact]
    public void Select_PrefersCompositePrimaryKey()
    {
        var table = Table(
            [Column("tenant_id", false), Column("id", false), Column("slug", false)],
            [
                Index("slug_unique", true, false, "slug"),
                Index("PRIMARY", true, true, "tenant_id", "id"),
            ]);

        var identity = MySqlRowIdentity.Select(table);

        Assert.NotNull(identity);
        Assert.True(identity.IsPrimary);
        Assert.Equal(["tenant_id", "id"], identity.Columns);
    }

    [Fact]
    public void Select_UsesSmallestNonNullableUniqueIndex()
    {
        var table = Table(
            [Column("email", false), Column("tenant", false), Column("slug", false)],
            [
                Index("tenant_slug", true, false, "tenant", "slug"),
                Index("email", true, false, "email"),
            ]);

        var identity = MySqlRowIdentity.Select(table);

        Assert.NotNull(identity);
        Assert.False(identity.IsPrimary);
        Assert.Equal(["email"], identity.Columns);
    }

    [Fact]
    public void Select_RejectsNullableUniqueIndex()
    {
        var table = Table([Column("email", true)], [Index("email", true, false, "email")]);

        Assert.Null(MySqlRowIdentity.Select(table));
    }

    [Fact]
    public void Select_RejectsPrefixUniqueIndex()
    {
        var column = Column("slug", false);
        var index = new MySqlIndexMetadata("slug_prefix", true, false, "BTREE",
            [new MySqlIndexColumnMetadata("slug", 1, 16, false)]);

        Assert.Null(MySqlRowIdentity.Select(Table([column], [index])));
    }

    private static MySqlColumnMetadata Column(string name, bool nullable) =>
        new(name, 1, "varchar", "varchar(255)", nullable, null, null, null, null, 255, null, null, false, null);

    private static MySqlIndexMetadata Index(string name, bool unique, bool primary, params string[] columns) =>
        new(name, unique, primary, "BTREE", columns.Select((column, index) =>
            new MySqlIndexColumnMetadata(column, index + 1, null, false)).ToArray());

    private static MySqlTableMetadata Table(
        IReadOnlyList<MySqlColumnMetadata> columns,
        IReadOnlyList<MySqlIndexMetadata> indexes) =>
        new("app", "users", "BASE TABLE", "InnoDB", null, null, columns, indexes, []);
}
