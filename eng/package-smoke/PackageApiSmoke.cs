using Microsoft.Extensions.DependencyInjection;
using RazorDbManager;
using RazorDbManager.Core;
using RazorDbManager.MySql;

namespace RazorDbManager.PackageSmoke;

public static class PackageApiSmoke
{
    public static IServiceCollection Configure(IServiceCollection services)
    {
        services
            .AddRazorDbManager(options => options.DefaultDatabaseId = "Main")
            .AddMySql("Main", options =>
            {
                options.ConnectionStringName = "MainDatabase";
                options.EnabledCapabilities = RazorDbCapabilitySets.DataEditor;
            });

        return services;
    }
}
