using Microsoft.Extensions.DependencyInjection;

namespace RazorDbManager;

/// <summary>Builder returned by RazorDbManager service registration.</summary>
public sealed class RazorDbManagerBuilder
{
    internal RazorDbManagerBuilder(IServiceCollection services) => Services = services;

    /// <summary>Services of the host application, exposed for provider packages.</summary>
    public IServiceCollection Services { get; }
}
