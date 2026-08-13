using System.Reflection;

namespace RazorDbManager;

/// <summary>Provides the RCL assembly to a host application's router.</summary>
public static class RazorDbManagerRouting
{
    /// <summary>Gets the assembly collection to pass to a Blazor router's additional assemblies.</summary>
    public static IEnumerable<Assembly> Assemblies { get; } =
        new[] { typeof(RazorDbManagerRouting).Assembly };
}
