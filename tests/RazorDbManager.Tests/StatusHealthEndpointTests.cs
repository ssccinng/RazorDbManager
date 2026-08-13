using System.Net;
using System.Text.Json;
using RazorDbManager.Core;
using RazorDbManager.Tests.Infrastructure;

namespace RazorDbManager.Tests;

public sealed class StatusHealthEndpointTests
{
    [Fact]
    public async Task StatusEndpoint_ReturnsSanitizedLiveProviderHealth()
    {
        var health = new StubHealthProbe(new RazorDbProviderHealthReport(
            RazorDbHealthStatus.Ready,
            "MySQL\r\nInjected",
            "8.4.6\0-secret",
            "app",
            RazorDbCapability.BrowseMetadata | RazorDbCapability.ReadRows,
            [
                new RazorDbHealthDiagnostic(
                    "grants-ready\r\nsecret=value",
                    RazorDbHealthDiagnosticSeverity.Information),
            ]));
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(healthProbe: health);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);

        using HttpResponseMessage response = await client.SendAsync(
            RazorDbTestHost.Request(HttpMethod.Get, "/_razor-db-manager/status", "alice"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ready", json.RootElement.GetProperty("status").GetString());
        JsonElement database = Assert.Single(json.RootElement.GetProperty("databases").EnumerateArray());
        Assert.Equal("Main", database.GetProperty("id").GetString());
        Assert.Equal("ready", database.GetProperty("status").GetString());
        Assert.True(database.GetProperty("latencyMilliseconds").GetInt64() >= 0);
        Assert.Equal("MySQLInjected", database.GetProperty("productName").GetString());
        Assert.Equal("8.4.6-secret", database.GetProperty("productVersion").GetString());
        Assert.Contains(database.GetProperty("diagnosticCapabilities").EnumerateArray(), item =>
            item.GetString() == nameof(RazorDbCapability.ReadRows));
        JsonElement diagnostic = Assert.Single(database.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("grants-ready--secret-value", diagnostic.GetProperty("code").GetString());
        Assert.Equal("information", diagnostic.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task StatusEndpoint_ReportsProbeFailureWithoutExceptionDetails()
    {
        var health = new StubHealthProbe(new InvalidOperationException(
            "Server=private.example;Password=do-not-leak"));
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(healthProbe: health);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);

        using HttpResponseMessage response = await client.SendAsync(
            RazorDbTestHost.Request(HttpMethod.Get, "/_razor-db-manager/status", "alice"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("health-probe-failed", body, StringComparison.Ordinal);
        Assert.Contains("degraded", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private.example", body, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-leak", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusEndpoint_OmitsResourceDeniedDatabaseWithoutOpeningProviderAndAudits()
    {
        var health = new StubHealthProbe(new RazorDbProviderHealthReport(
            RazorDbHealthStatus.Ready,
            "MySQL",
            "8.4.6",
            "app",
            RazorDbCapabilitySets.ReadOnly,
            []));
        var authorizer = new RecordingAuthorizer(context =>
            context.Operation == RazorDbOperation.BrowseMetadata
                ? RazorDbAuthorizationResult.Denied("database-hidden")
                : RazorDbAuthorizationResult.Allowed);
        var audit = new RecordingAuditSink();
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(
            authorizer: authorizer,
            auditSink: audit,
            healthProbe: health);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);

        using HttpResponseMessage response = await client.SendAsync(
            RazorDbTestHost.Request(HttpMethod.Get, "/_razor-db-manager/status", "alice"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("degraded", json.RootElement.GetProperty("status").GetString());
        Assert.Empty(json.RootElement.GetProperty("databases").EnumerateArray());
        Assert.Equal(0, health.Calls);
        RazorDbAuthorizationContext context = Assert.Single(authorizer.Contexts);
        Assert.Equal("Main", context.Registration.Id);
        Assert.Equal(RazorDbCapability.BrowseMetadata, context.RequiredCapability);
        RazorDbAuditRecord denied = Assert.Single(audit.Records);
        Assert.Equal(RazorDbAuditStatus.Denied, denied.Status);
        Assert.Equal("database-hidden", denied.ResultCode);
    }

    private sealed class StubHealthProbe : IRazorDbProviderHealthProbe
    {
        private readonly RazorDbProviderHealthReport? _report;
        private readonly Exception? _failure;

        public StubHealthProbe(RazorDbProviderHealthReport report) => _report = report;

        public StubHealthProbe(Exception failure) => _failure = failure;

        public int Calls { get; private set; }

        public ValueTask<RazorDbProviderHealthReport> CheckHealthAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return _failure is null
                ? ValueTask.FromResult(_report!)
                : ValueTask.FromException<RazorDbProviderHealthReport>(_failure);
        }
    }
}
