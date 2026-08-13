using RazorDbManager.Core;
using RazorDbManager.MySql.Health;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlGrantAnalyzerTests
{
    [Fact]
    public void Analyze_MapsWholeSchemaPrivilegesToDiagnosticCapabilities()
    {
        MySqlGrantAnalysis result = MySqlGrantAnalyzer.Analyze(
        [
            "GRANT USAGE ON *.* TO `manager`@`%`",
            "GRANT SELECT, INSERT, UPDATE, DELETE ON `app`.* TO `manager`@`%`",
        ],
        ["app"]);

        Assert.True(result.Capabilities.Includes(RazorDbCapabilitySets.DataEditor));
        Assert.True(result.Capabilities.Includes(RazorDbCapability.Import));
        Assert.True(result.Capabilities.Includes(RazorDbCapability.Export));
        Assert.True(result.Capabilities.Includes(RazorDbCapability.DownloadBinary));
        Assert.False(result.Capabilities.Includes(RazorDbCapability.ModifySchema));
        Assert.False(result.Capabilities.Includes(RazorDbCapability.ExecuteSql));
        Assert.Empty(result.DiagnosticCodes);
    }

    [Fact]
    public void Analyze_RequiresCapabilitiesAcrossEveryAllowedSchema()
    {
        MySqlGrantAnalysis result = MySqlGrantAnalyzer.Analyze(
        [
            "GRANT SELECT ON `app`.* TO 'manager'@'localhost'",
            "GRANT INSERT ON `app`.* TO 'manager'@'localhost'",
            "GRANT SELECT ON `archive`.* TO 'manager'@'localhost'",
        ],
        ["app", "archive"]);

        Assert.True(result.Capabilities.Includes(RazorDbCapability.ReadRows));
        Assert.True(result.Capabilities.Includes(RazorDbCapability.Export));
        Assert.False(result.Capabilities.Includes(RazorDbCapability.InsertRows));
        Assert.False(result.Capabilities.Includes(RazorDbCapability.Import));
    }

    [Fact]
    public void Analyze_MapsGlobalAllPrivilegesConservatively()
    {
        MySqlGrantAnalysis result = MySqlGrantAnalyzer.Analyze(
            ["GRANT ALL PRIVILEGES ON *.* TO `manager`@`localhost` WITH GRANT OPTION"],
            ["app", "archive"]);

        Assert.Equal(RazorDbCapabilitySets.All, result.Capabilities);
        Assert.Empty(result.DiagnosticCodes);
    }

    [Fact]
    public void Analyze_AllPrivilegesTriggerExcessiveReaderDiagnostic()
    {
        MySqlGrantAnalysis result = MySqlGrantAnalyzer.Analyze(
            ["GRANT ALL PRIVILEGES, PROCESS ON *.* TO `manager`@`localhost`"],
            ["app"]);

        Assert.True(MySqlHealthProbe.HasExcessiveReaderCapabilities(result.Capabilities));
        Assert.False(MySqlHealthProbe.HasExcessiveReaderCapabilities(
            RazorDbCapability.BrowseMetadata
            | RazorDbCapability.ReadRows
            | RazorDbCapability.Export
            | RazorDbCapability.DownloadBinary));
    }

    [Fact]
    public void Analyze_RoleAndPartialScopesWarnAndDoNotWidenCapabilities()
    {
        MySqlGrantAnalysis result = MySqlGrantAnalyzer.Analyze(
        [
            "GRANT `data_editor`@`%` TO `manager`@`%`",
            "GRANT SELECT ON `app`.`orders` TO `manager`@`%`",
            "GRANT UPDATE (`status`) ON `app`.* TO `manager`@`%`",
        ],
        ["app"]);

        Assert.Equal(RazorDbCapability.None, result.Capabilities);
        Assert.Contains("grants-role-unresolved", result.DiagnosticCodes);
        Assert.Contains("grants-partial-scope-ignored", result.DiagnosticCodes);
        Assert.Contains("grants-column-scope-ignored", result.DiagnosticCodes);
    }

    [Fact]
    public void Analyze_ParsesQuotedSchemaContainingDotOrEscapedQuote()
    {
        MySqlGrantAnalysis result = MySqlGrantAnalyzer.Analyze(
        [
            "GRANT SELECT ON `tenant.one`.* TO `manager`@`%`",
            "GRANT SELECT ON `odd``name`.* TO `manager`@`%`",
        ],
        ["tenant.one", "odd`name"]);

        Assert.True(result.Capabilities.Includes(RazorDbCapability.ReadRows));
        Assert.Empty(result.DiagnosticCodes);
    }
}
