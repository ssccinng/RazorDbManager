using RazorDbManager.Core;
using RazorDbManager.MySql.Metadata;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlMetadataFingerprintTests
{
    [Fact]
    public void Fingerprint_ChangesForEveryDdlRelevantTableColumnIndexAndForeignKeyFacet()
    {
        var original = Model();
        var baseline = Fingerprint(original);
        var variants = new[]
        {
            original with { Engine = "MyISAM" },
            original with { TableCollation = "utf8mb4_bin" },
            original with { TableComment = "changed" },
            original with { Column = original.Column with { CharacterSet = "latin1" } },
            original with { Column = original.Column with { Collation = "utf8mb4_bin" } },
            original with { Column = original.Column with { Comment = "changed" } },
            original with { Index = original.Index with { IsPrimary = true } },
            original with { Index = original.Index with { Method = "HASH" } },
            original with { Index = original.Index with { Columns = [new DbIndexColumn("id", true, 4)] } },
            original with { ForeignKey = original.ForeignKey with { ReferencedColumns = ["other_id"] } },
            original with { ForeignKey = original.ForeignKey with { OnDelete = DbReferentialAction.Cascade } },
            original with { ForeignKey = original.ForeignKey with { OnUpdate = DbReferentialAction.NoAction } },
        };

        Assert.All(variants, variant => Assert.NotEqual(baseline, Fingerprint(variant)));
    }

    private static string Fingerprint(FingerprintModel value) => MySqlMetadataService.Fingerprint(
        new DbObjectName("app", "items"),
        DbObjectKind.Table,
        value.Engine,
        value.TableCollation,
        value.TableComment,
        [value.Column],
        [value.Index],
        [value.ForeignKey]);

    private static FingerprintModel Model() => new(
        "InnoDB",
        "utf8mb4_general_ci",
        "table-comment",
        new DbColumnMetadata(
            "id",
            0,
            new DbTypeDescriptor("varchar(20)", DbDataKind.Text, Length: 20),
            false,
            CharacterSet: "utf8mb4",
            Collation: "utf8mb4_general_ci",
            Comment: "column-comment"),
        new DbIndexMetadata("ix_id", [new DbIndexColumn("id")], true, Method: "BTREE"),
        new DbForeignKeyMetadata(
            "fk_parent",
            ["id"],
            new DbObjectName("app", "parents"),
            ["id"],
            DbReferentialAction.Restrict,
            DbReferentialAction.Cascade));

    private sealed record FingerprintModel(
        string Engine,
        string TableCollation,
        string TableComment,
        DbColumnMetadata Column,
        DbIndexMetadata Index,
        DbForeignKeyMetadata ForeignKey);
}
