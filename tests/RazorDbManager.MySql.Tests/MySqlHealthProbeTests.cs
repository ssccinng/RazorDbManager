using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RazorDbManager.Core;
using RazorDbManager.MySql.Configuration;
using RazorDbManager.MySql.Health;
using RazorDbManager.MySql.Infrastructure;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlHealthProbeTests
{
    [Fact]
    public async Task CheckHealthAsync_PropagatesPreCancelledRequest()
    {
        MySqlHealthProbe probe = CreateProbe(
            "Server=127.0.0.1;Port=1;Database=app;User ID=user;Password=secret;SslMode=None;ConnectionTimeout=1");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await probe.CheckHealthAsync(cancellation.Token));
    }

    [Fact]
    public async Task CheckHealthAsync_ConnectionRefusalReturnsSanitizedDegradedReport()
    {
        MySqlHealthProbe probe = CreateProbe(
            "Server=127.0.0.1;Port=1;Database=app;User ID=user;Password=do-not-leak;SslMode=None;ConnectionTimeout=1");

        RazorDbProviderHealthReport report = await probe.CheckHealthAsync();

        Assert.Equal(RazorDbHealthStatus.Degraded, report.Status);
        Assert.Equal(RazorDbCapability.None, report.DiagnosticCapabilities);
        Assert.Null(report.ProductName);
        Assert.Null(report.ProductVersion);
        Assert.Null(report.CurrentDatabase);
        Assert.Contains(report.Diagnostics, diagnostic =>
            diagnostic.Code is "reader-connection-failed" or "reader-connection-timeout");
        Assert.All(report.Diagnostics, diagnostic =>
        {
            Assert.DoesNotContain("do-not-leak", diagnostic.Code, StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1", diagnostic.Code, StringComparison.Ordinal);
        });
    }

    private static MySqlHealthProbe CreateProbe(string connectionString)
    {
        var options = new MySqlProviderOptions
        {
            ConnectionStringName = "MainDatabase",
            EnabledCapabilities = RazorDbCapabilitySets.ReadOnly,
            AllowInsecureDevelopmentConnection = true,
        };
        var registration = new DatabaseRegistration
        {
            Id = "Main",
            ProviderName = "mysql",
            ConnectionStringName = options.ConnectionStringName,
            EnabledCapabilities = options.EnabledCapabilities,
            AllowedSchemas = ["app"],
        };
        var credentialProvider = new StaticCredentialProvider(connectionString);
        var validator = new MySqlCredentialValidator(
            registration,
            options,
            new TestEnvironment(Environments.Development));
        var credentials = new MySqlCredentialSource(registration, credentialProvider, validator);
        return new MySqlHealthProbe(registration, credentials, ["app"]);
    }

    private sealed class StaticCredentialProvider(string connectionString) : IRazorDbCredentialProvider
    {
        public ValueTask<RazorDbCredential> GetCredentialAsync(
            DatabaseRegistration registration,
            RazorDbCredentialPurpose purpose,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new RazorDbCredential(connectionString));
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
