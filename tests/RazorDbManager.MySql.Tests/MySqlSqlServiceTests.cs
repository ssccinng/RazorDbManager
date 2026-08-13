using System.Diagnostics;
using MySqlConnector;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RazorDbManager.Core;
using RazorDbManager.MySql.Configuration;
using RazorDbManager.MySql.Infrastructure;
using RazorDbManager.MySql.Sql;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlSqlServiceTests
{
    [Fact]
    public void QueryCommandDiagnostic_UsesExecutedCommandTextAndBoundsValues()
    {
        using var command = new MySqlCommand("SELECT * FROM `app`.`users` WHERE `name` = @p0 AND `payload` = @p1 LIMIT @limit");
        command.Parameters.AddWithValue("@p0", new string('x', 300));
        command.Parameters.AddWithValue("@p1", new byte[12]);
        command.Parameters.AddWithValue("@limit", 101);

        DbCommandDiagnostic diagnostic = RazorDbManager.MySql.Data.MySqlDataService.CreateCommandDiagnostic(
            command,
            TimeSpan.FromMilliseconds(12));

        Assert.Equal(command.CommandText, diagnostic.CommandText);
        Assert.Equal(3, diagnostic.Parameters.Count);
        Assert.Contains("300 chars", diagnostic.Parameters[0].ValuePreview, StringComparison.Ordinal);
        Assert.Equal("<binary 12 bytes>", diagnostic.Parameters[1].ValuePreview);
        Assert.Equal("101", diagnostic.Parameters[2].ValuePreview);
        Assert.All(diagnostic.Parameters, parameter => Assert.False(string.IsNullOrWhiteSpace(parameter.DatabaseType)));
    }

    [Fact]
    public async Task ExecuteAsync_AppliesBatchDeadlineDuringCredentialResolution()
    {
        var options = Options();
        var sql = Service(options, new DelayedCredentialProvider());
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sql.ExecuteAsync(
            new SqlExecutionRequest("Main", "SELECT 1", Timeout: TimeSpan.FromMilliseconds(30)),
            CancellationToken.None));

        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExecuteAsync_EnforcesConfiguredStatementCapBeforeResolvingCredential()
    {
        var options = Options();
        options.MaximumSqlStatements = 1;
        var credentials = new CountingCredentialProvider();
        var sql = Service(options, credentials);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sql.ExecuteAsync(
            new SqlExecutionRequest("Main", "SELECT 1; SELECT 2;"),
            CancellationToken.None));

        Assert.Equal(0, credentials.CallCount);
    }

    [Fact]
    public async Task BatchTimeoutSource_AppliesOneDeadlineToTheWholeBatch()
    {
        var stopwatch = Stopwatch.StartNew();
        using var source = MySqlSqlService.CreateBatchTimeoutSource(
            TimeSpan.FromMilliseconds(30),
            CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Delay(TimeSpan.FromSeconds(10), source.Token));

        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BatchTimeoutSource_LinksCallerCancellation()
    {
        using var caller = new CancellationTokenSource();
        using var source = MySqlSqlService.CreateBatchTimeoutSource(TimeSpan.FromSeconds(10), caller.Token);

        await caller.CancelAsync();

        Assert.True(source.IsCancellationRequested);
    }

    private static MySqlSqlService Service(
        MySqlProviderOptions options,
        IRazorDbCredentialProvider credentialProvider)
    {
        var registration = new DatabaseRegistration
        {
            Id = "Main",
            ProviderName = "mysql",
            ConnectionStringName = options.ConnectionStringName,
            SqlConsoleConnectionStringName = options.SqlConsoleConnectionStringName,
            EnabledCapabilities = options.EnabledCapabilities,
            AllowedSchemas = ["app"],
        };
        var validator = new MySqlCredentialValidator(
            registration,
            options,
            new TestEnvironment(Environments.Production));
        return new MySqlSqlService(options, new MySqlCredentialSource(registration, credentialProvider, validator));
    }

    private static MySqlProviderOptions Options() => new()
    {
        ConnectionStringName = "reader",
        SqlConsoleConnectionStringName = "sql",
        EnabledCapabilities = RazorDbCapabilitySets.ReadOnly | RazorDbCapability.ExecuteSql,
        SqlCommandTimeoutSeconds = 30,
    };

    private sealed class DelayedCredentialProvider : IRazorDbCredentialProvider
    {
        public async ValueTask<RazorDbCredential> GetCredentialAsync(
            DatabaseRegistration registration,
            RazorDbCredentialPurpose purpose,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new RazorDbCredential(MySqlProviderOptionsValidatorTests.SecureConnection("app"));
        }
    }

    private sealed class CountingCredentialProvider : IRazorDbCredentialProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<RazorDbCredential> GetCredentialAsync(
            DatabaseRegistration registration,
            RazorDbCredentialPurpose purpose,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(new RazorDbCredential(
                MySqlProviderOptionsValidatorTests.SecureConnection("app")));
        }
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
