using RazorDbManager.Core;
using RazorDbManager.MySql.Metadata;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlTypeMapperTests
{
    [Theory]
    [InlineData("bigint", "bigint unsigned", DbDataKind.UnsignedInteger)]
    [InlineData("decimal", "decimal(65,30)", DbDataKind.Decimal)]
    [InlineData("tinyint", "tinyint(1)", DbDataKind.Boolean)]
    [InlineData("json", "json", DbDataKind.Json)]
    [InlineData("geometry", "geometry", DbDataKind.Geometry)]
    [InlineData("blob", "blob", DbDataKind.Binary)]
    public void Map_PreservesSemanticType(string dataType, string columnType, DbDataKind expected)
    {
        var result = MySqlTypeMapper.Map(dataType, columnType, null, null, null);

        Assert.Equal(expected, result.Kind);
    }

    [Fact]
    public void Map_ParsesEnumEscapes()
    {
        var result = MySqlTypeMapper.Map("enum", "enum('new','paid\\'s','a''b')", 8, null, null);

        Assert.Equal(["new", "paid's", "a'b"], result.AllowedValues);
    }
}
