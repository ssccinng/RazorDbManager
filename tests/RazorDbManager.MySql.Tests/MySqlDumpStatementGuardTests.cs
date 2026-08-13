using RazorDbManager.MySql.Transfer;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlDumpStatementGuardTests
{
    [Fact]
    public void EnsureAllowed_AcceptsAllowedQualifiedTable() =>
        MySqlDumpStatementGuard.EnsureAllowed("INSERT INTO `app`.`users` VALUES (1)", ["app"]);

    [Theory]
    [InlineData("USE app")]
    [InlineData("CREATE DATABASE app")]
    [InlineData("DROP SCHEMA app")]
    [InlineData("INSERT INTO `other`.`users` VALUES (1)")]
    [InlineData("INSERT INTO other.users VALUES (1)")]
    [InlineData("INSERT INTO other /* boundary */ . users VALUES (1)")]
    [InlineData("INSERT INTO \"other\".\"users\" VALUES (1)")]
    [InlineData("INSERT INTO MYSQL.user VALUES (1)")]
    [InlineData("/*!50000 INSERT INTO forbidden.users VALUES (1) */")]
    [InlineData("/*M!100100 INSERT INTO forbidden.users VALUES (1) */")]
    [InlineData("CREATE /* comment */ DATABASE app")]
    public void EnsureAllowed_RejectsDatabaseScopeOrOtherSchema(string sql) =>
        Assert.Throws<UnauthorizedAccessException>(() => MySqlDumpStatementGuard.EnsureAllowed(sql, ["app"]));

    [Fact]
    public void EnsureAllowed_DoesNotTreatStringsOrCommentsAsQualifiedNames() =>
        MySqlDumpStatementGuard.EnsureAllowed(
            "INSERT INTO app.users VALUES ('other.users'); -- forbidden.users\n# denied.users\n/* secret.users */",
            ["app"]);

    [Theory]
    [InlineData("INSERT INTO APP.Users VALUES (1)", "app")]
    [InlineData("INSERT INTO `tenant``one`.`users` VALUES (1)", "tenant`one")]
    [InlineData("INSERT INTO `数据库`.`表` VALUES (1)", "数据库")]
    [InlineData("SELECT app.users.id FROM app.users", "APP")]
    public void EnsureAllowed_MatchesCaseUnicodeAndEscapedIdentifiers(string sql, string allowedSchema) =>
        MySqlDumpStatementGuard.EnsureAllowed(sql, [allowedSchema]);

    [Fact]
    public void EnsureAllowed_RejectsForbiddenUnicodeSchema() =>
        Assert.Throws<UnauthorizedAccessException>(() =>
            MySqlDumpStatementGuard.EnsureAllowed("INSERT INTO 禁止.表 VALUES (1)", ["允许"]));
}
