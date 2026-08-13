using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace RazorDbManager;

internal sealed class LocalStorePath
{
    public LocalStorePath(IHostEnvironment environment, IOptions<RazorDbManagerOptions> options)
    {
        string configured = options.Value.StoragePath;
        Root = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured));
        ArtifactRoot = Path.Combine(Root, "artifacts");
        DatabasePath = Path.Combine(Root, "state.db");
    }

    public string Root { get; }
    public string ArtifactRoot { get; }
    public string DatabasePath { get; }
}
