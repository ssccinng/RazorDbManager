using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RazorDbManager.Core;
using RazorDbManager.MySql.Configuration;
using RazorDbManager.MySql.Infrastructure;
using RazorDbManager.MySql.Metadata;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlProviderOptionsValidatorTests
{
    [Fact]
    public void CredentialValidation_RequiresVerifyFullOutsideDevelopment()
    {
        var options = Options();
        var validator = CredentialValidator(options, Environments.Production);

        var exception = Assert.Throws<InvalidOperationException>(() => validator.Validate(
            "Server=localhost;Database=app;User ID=user;Password=x;SslMode=Required;PersistSecurityInfo=false;AllowLoadLocalInfile=false",
            MySqlCredentialSlot.Reader));

        Assert.Contains("SslMode must be VerifyFull", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialValidation_AllowsExplicitDevelopmentRelaxation()
    {
        var options = Options();
        options.AllowInsecureDevelopmentConnection = true;
        var validator = CredentialValidator(options, Environments.Development);

        validator.Validate(
            "Server=localhost;Database=app;User ID=user;Password=x;SslMode=None;PersistSecurityInfo=false;AllowLoadLocalInfile=false",
            MySqlCredentialSlot.Reader);
    }

    [Theory]
    [InlineData("PersistSecurityInfo=true", "PersistSecurityInfo must be false")]
    [InlineData("AllowLoadLocalInfile=true", "AllowLoadLocalInfile must be false")]
    public void CredentialValidation_RejectsUnsafeConnectionOptions(string unsafeOption, string expectedMessage)
    {
        var validator = CredentialValidator(Options(), Environments.Production);

        var exception = Assert.Throws<InvalidOperationException>(() => validator.Validate(
            $"Server=localhost;Database=app;User ID=user;Password=x;SslMode=VerifyFull;{unsafeOption}",
            MySqlCredentialSlot.Reader));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialValidation_UsesReaderAllowlistForWriterCredential()
    {
        var options = Options();
        options.AllowedSchemas.Add("app");
        var validator = CredentialValidator(options, Environments.Production);

        var exception = Assert.Throws<InvalidOperationException>(() => validator.Validate(
            SecureConnection("other"),
            MySqlCredentialSlot.Writer,
            ["app"]));

        Assert.Contains("Database must be included in AllowedSchemas", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialValidation_RequiresSqlConsoleDefaultDatabase()
    {
        var validator = CredentialValidator(Options(), Environments.Production);

        var exception = Assert.Throws<InvalidOperationException>(() => validator.Validate(
            SecureConnection(database: null),
            MySqlCredentialSlot.SqlConsole,
            ["app"]));

        Assert.Contains("must select an allowed Database", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsSystemSchema()
    {
        var options = Options();
        options.AllowedSchemas.Add("mysql");

        Assert.Throws<OptionsValidationException>(() => new MySqlProviderOptionsValidator().Validate("Main", options));
    }

    [Fact]
    public void Validate_SqlRestoreRequiresCapabilities()
    {
        var options = Options();
        options.EnableSqlRestore = true;

        Assert.Throws<OptionsValidationException>(() => new MySqlProviderOptionsValidator().Validate("Main", options));
    }

    [Fact]
    public void Validate_SqlRestoreRequiresDedicatedSqlConsoleCredential()
    {
        var options = Options();
        options.EnabledCapabilities |= RazorDbCapability.Import | RazorDbCapability.ExecuteSql;
        options.EnableSqlRestore = true;
        options.AllowSharedHighRiskCredential = true;

        var exception = Assert.Throws<OptionsValidationException>(() =>
            new MySqlProviderOptionsValidator().Validate("Main", options));

        Assert.Contains(exception.Failures, failure => failure.Contains("explicit SqlConsoleConnectionStringName", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AllowsSqlRestoreWithDedicatedCredentialSlot()
    {
        var options = Options();
        options.EnabledCapabilities |= RazorDbCapability.Import | RazorDbCapability.ExecuteSql;
        options.EnableSqlRestore = true;
        options.SqlConsoleConnectionStringName = "RestoreDatabase";

        new MySqlProviderOptionsValidator().Validate("Main", options);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_001)]
    public void Validate_RejectsSqlStatementLimitOutsideRange(int value)
    {
        var options = Options();
        options.MaximumSqlStatements = value;

        var exception = Assert.Throws<OptionsValidationException>(() =>
            new MySqlProviderOptionsValidator().Validate("Main", options));

        Assert.Contains(exception.Failures, failure => failure.Contains("MaximumSqlStatements", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsRecordOrCellLimitsAboveTheirContainingTransferLimits()
    {
        var options = Options();
        options.MaximumUploadBytes = 1024;
        options.MaximumCsvRecordBytes = 2048;
        options.MaximumExportBytes = 1024;
        options.MaximumExportCellBytes = 2048;

        var exception = Assert.Throws<OptionsValidationException>(() =>
            new MySqlProviderOptionsValidator().Validate("Main", options));

        Assert.Contains(exception.Failures, failure => failure.Contains("MaximumCsvRecordBytes cannot exceed", StringComparison.Ordinal));
        Assert.Contains(exception.Failures, failure => failure.Contains("MaximumExportCellBytes cannot exceed", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsBinaryDownloadLimitAboveOneGiB()
    {
        var options = Options();
        options.MaximumBinaryDownloadBytes = 1024L * 1024 * 1024 + 1;

        var exception = Assert.Throws<OptionsValidationException>(() =>
            new MySqlProviderOptionsValidator().Validate("Main", options));

        Assert.Contains(exception.Failures, failure =>
            failure.Contains("MaximumBinaryDownloadBytes", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfiguredCapabilities_RemoveHighRiskOperationsWithoutCredentialSlots()
    {
        var options = Options();
        options.EnabledCapabilities = RazorDbCapabilitySets.All;
        var registration = Registration(options);

        var capabilities = MySqlMetadataService.GetConfiguredCapabilities(registration, options);

        Assert.True(capabilities.Includes(RazorDbCapabilitySets.DataEditor));
        Assert.False(capabilities.Includes(RazorDbCapability.ModifySchema));
        Assert.False(capabilities.Includes(RazorDbCapability.DestructiveSchema));
        Assert.False(capabilities.Includes(RazorDbCapability.ExecuteSql));
    }

    private static MySqlCredentialValidator CredentialValidator(MySqlProviderOptions options, string environment) =>
        new(Registration(options), options, new FakeEnvironment(environment));

    private static MySqlProviderOptions Options() => new()
    {
        ConnectionStringName = "MainDatabase",
    };

    private static DatabaseRegistration Registration(MySqlProviderOptions options) => new()
    {
        Id = "Main",
        ProviderName = "mysql",
        ConnectionStringName = options.ConnectionStringName,
        WriterConnectionStringName = options.WriterConnectionStringName,
        SchemaConnectionStringName = options.SchemaConnectionStringName,
        SqlConsoleConnectionStringName = options.SqlConsoleConnectionStringName,
        EnabledCapabilities = options.EnabledCapabilities,
        AllowedSchemas = options.AllowedSchemas.ToArray(),
        AllowSharedHighRiskCredential = options.AllowSharedHighRiskCredential,
    };

    internal static string SecureConnection(string? database) =>
        $"Server=localhost;{(database is null ? string.Empty : $"Database={database};")}User ID=user;Password=x;SslMode=VerifyFull;PersistSecurityInfo=false;AllowLoadLocalInfile=false";

    private sealed class FakeEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
