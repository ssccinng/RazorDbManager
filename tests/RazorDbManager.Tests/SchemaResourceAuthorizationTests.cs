using RazorDbManager.Core;

namespace RazorDbManager.Tests;

public sealed class SchemaResourceAuthorizationTests
{
    [Fact]
    public void RenameTable_BindsSourceAndDestination()
    {
        IReadOnlyList<RazorDbResource> resources = DatabaseWorkspace.SchemaResources(
            new RenameTableChange(new DbObjectName("app", "old_name"), "new_name"));

        Assert.Equal(
            [new RazorDbResource("app", "old_name"), new RazorDbResource("app", "new_name")],
            resources);
    }

    [Fact]
    public void AlterColumn_BindsExistingAndReplacementNames()
    {
        IReadOnlyList<RazorDbResource> resources = DatabaseWorkspace.SchemaResources(
            new AlterColumnChange(
                new DbObjectName("app", "orders"),
                "old_total",
                new ColumnDefinition
                {
                    Name = "total",
                    Type = new ColumnTypeDefinition { Type = SchemaDataType.Decimal, Precision = 12, Scale = 2 },
                }));

        Assert.Equal(
            [new RazorDbResource("app", "orders", "old_total"), new RazorDbResource("app", "orders", "total")],
            resources);
    }

    [Fact]
    public void AddForeignKey_BindsLocalAndReferencedResources()
    {
        ForeignKeyDefinition foreignKey = new()
        {
            Name = "fk_orders_customers",
            Columns = ["customer_id", "tenant_id"],
            ReferencedTable = new DbObjectName("crm", "customers"),
            ReferencedColumns = ["id", "tenant_id"],
        };

        IReadOnlyList<RazorDbResource> resources = DatabaseWorkspace.SchemaResources(
            new AddForeignKeyChange(new DbObjectName("app", "orders"), foreignKey));

        Assert.Equal(
            [
                new RazorDbResource("app", "orders", "fk_orders_customers"),
                new RazorDbResource("app", "orders", "customer_id"),
                new RazorDbResource("app", "orders", "tenant_id"),
                new RazorDbResource("crm", "customers"),
                new RazorDbResource("crm", "customers", "id"),
                new RazorDbResource("crm", "customers", "tenant_id"),
            ],
            resources);
    }

    [Fact]
    public void ConfirmationHash_BindsEveryAuthorizedTarget()
    {
        const string sqlHash = "0123456789abcdef";
        RazorDbResource source = new("app", "old_name");

        string first = DatabaseWorkspace.SchemaConfirmationHash(
            sqlHash,
            [source, new RazorDbResource("app", "first_name")]);
        string second = DatabaseWorkspace.SchemaConfirmationHash(
            sqlHash,
            [source, new RazorDbResource("app", "second_name")]);

        Assert.NotEqual(first, second);
        Assert.Equal(first, DatabaseWorkspace.SchemaConfirmationHash(
            sqlHash,
            [source, new RazorDbResource("app", "first_name")]));
    }

    [Theory]
    [InlineData("/* comment */ SELECT ';' AS value; -- next\n UPDATE app.users SET name='x'", "SELECT,UPDATE")]
    [InlineData("# comment\nINSERT INTO app.users VALUES (1)", "INSERT")]
    [InlineData("customer_secret", "OTHER")]
    public void SqlClassification_ContainsOnlyStatementKinds(string sql, string expected)
    {
        string classification = DatabaseWorkspace.ClassifySql(sql);

        Assert.Equal(expected, classification);
        Assert.DoesNotContain("secret", classification, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("users", classification, StringComparison.OrdinalIgnoreCase);
    }
}
