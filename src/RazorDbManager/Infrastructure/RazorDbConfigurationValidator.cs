using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RazorDbManager.Core;

namespace RazorDbManager;

internal sealed class RazorDbConfigurationValidator(
    IServiceProvider services,
    IAuthorizationPolicyProvider policyProvider,
    IOptions<RazorDbManagerOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        RazorDbManagerOptions configured = options.Value;
        configured.ResourceLimits.Validate();
        if (configured.DownloadTokenLifetime <= TimeSpan.Zero || configured.OperationTokenLifetime <= TimeSpan.Zero
            || configured.JobAuthorizationLifetime <= TimeSpan.Zero || configured.ArtifactLifetime <= TimeSpan.Zero
            || configured.TerminalJobRetention <= TimeSpan.Zero)
            throw new InvalidOperationException("RazorDbManager token, job authorization, artifact, and retention lifetimes must be positive.");
        if (await policyProvider.GetPolicyAsync(RazorDbManagerPolicies.Access) is null)
            throw new InvalidOperationException($"Authorization policy '{RazorDbManagerPolicies.Access}' must be configured by the host.");

        IRazorDbProviderRegistry? registry = services.GetService<IRazorDbProviderRegistry>();
        if (registry is null) return;
        bool transfers = registry.Registrations.Any(item =>
            (item.EnabledCapabilities & (RazorDbCapability.Import | RazorDbCapability.Export)) != 0);
        if (transfers && services.GetRequiredService<IRazorDbBackgroundAuthorizer>() is DefaultRazorDbBackgroundAuthorizer)
            throw new InvalidOperationException(
                "An identity-aware IRazorDbBackgroundAuthorizer must be registered when import or export capabilities are enabled.");
        bool highRisk = registry.Registrations.Any(item => (item.EnabledCapabilities & (RazorDbCapability.ExecuteSql | RazorDbCapability.ModifySchema | RazorDbCapability.DestructiveSchema)) != 0);
        if (!highRisk) return;
        if (await policyProvider.GetPolicyAsync(RazorDbManagerPolicies.HighRisk) is null)
            throw new InvalidOperationException($"Authorization policy '{RazorDbManagerPolicies.HighRisk}' is required when high-risk capabilities are enabled.");
        if (services.GetRequiredService<IRazorDbSessionValidator>() is DenyHighRiskSessionValidator)
            throw new InvalidOperationException("An IRazorDbSessionValidator must be registered when high-risk capabilities are enabled.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
