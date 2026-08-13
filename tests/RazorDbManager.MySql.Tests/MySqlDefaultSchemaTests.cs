using RazorDbManager.Core;
using RazorDbManager.MySql.Metadata;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlDefaultSchemaTests
{
    [Fact]
    public void ResolveDefaultSchemaPrefersAllowedConnectionDatabase()
    {
        DbSchemaMetadata[] schemas = [new("archive", []), new("main", [])];

        Assert.Equal("main", MySqlMetadataService.ResolveDefaultSchema("MAIN", schemas));
    }

    [Fact]
    public void ResolveDefaultSchemaFallsBackToOnlyAllowedSchema()
    {
        DbSchemaMetadata[] schemas = [new("main", [])];

        Assert.Equal("main", MySqlMetadataService.ResolveDefaultSchema("outside", schemas));
    }

    [Fact]
    public void ResolveDefaultSchemaRejectsAmbiguousOrDisallowedDatabase()
    {
        DbSchemaMetadata[] schemas = [new("main", []), new("archive", [])];

        Assert.Null(MySqlMetadataService.ResolveDefaultSchema("outside", schemas));
        Assert.Null(MySqlMetadataService.ResolveDefaultSchema(null, schemas));
    }
}
