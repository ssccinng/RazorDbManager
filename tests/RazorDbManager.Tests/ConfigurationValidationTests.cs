using RazorDbManager.Core;
using RazorDbManager.Tests.Infrastructure;

namespace RazorDbManager.Tests;

public sealed class ConfigurationValidationTests
{
    [Fact]
    public async Task Startup_RequiresAccessPolicy()
    {
        await using RazorDbTestHost host = RazorDbTestHost.CreateStoreHost(addAccessPolicy: false);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync());

        Assert.Contains(RazorDbManagerPolicies.Access, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_RequiresExplicitSessionValidatorForHighRiskCapabilities()
    {
        await using RazorDbTestHost host = RazorDbTestHost.CreateStoreHost(
            RazorDbCapabilitySets.DataEditor | RazorDbCapability.ExecuteSql,
            addHighRiskPolicy: true);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync());

        Assert.Contains(nameof(IRazorDbSessionValidator), error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_AcceptsHighRiskPoliciesWithSessionValidator()
    {
        await using RazorDbTestHost host = RazorDbTestHost.CreateStoreHost(
            RazorDbCapabilitySets.DataEditor | RazorDbCapability.ExecuteSql,
            addHighRiskPolicy: true,
            sessionValidator: new AllowSessionValidator());
        await host.StartAsync();

        Assert.True(File.Exists(Path.Combine(host.StoragePath, "state.db")));
    }

    [Fact]
    public async Task Startup_RequiresIdentityAwareBackgroundAuthorizerForTransfers()
    {
        await using RazorDbTestHost host = RazorDbTestHost.CreateStoreHost(
            RazorDbCapabilitySets.DataEditor | RazorDbCapability.Export);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync());

        Assert.Contains(nameof(IRazorDbBackgroundAuthorizer), error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_AcceptsTransfersWithBackgroundAuthorizer()
    {
        await using RazorDbTestHost host = RazorDbTestHost.CreateStoreHost(
            RazorDbCapabilitySets.DataEditor | RazorDbCapability.Export,
            backgroundAuthorizer: new AllowBackgroundAuthorizer());

        await host.StartAsync();

        Assert.True(File.Exists(Path.Combine(host.StoragePath, "state.db")));
    }

    [Fact]
    public async Task Startup_RejectsNonPositiveTokenLifetime()
    {
        await using RazorDbTestHost host = RazorDbTestHost.CreateStoreHost(
            configure: options => options.DownloadTokenLifetime = TimeSpan.Zero);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync());

        Assert.Contains("lifetimes must be positive", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Startup_RejectsNonPositiveTerminalJobRetention()
    {
        await using RazorDbTestHost host = RazorDbTestHost.CreateStoreHost(
            configure: options => options.TerminalJobRetention = TimeSpan.Zero);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync());

        Assert.Contains("retention lifetimes must be positive", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
