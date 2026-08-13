using System.ComponentModel.DataAnnotations;
using RazorDbManager.Core;

namespace RazorDbManager;

/// <summary>Configures RazorDbManager and its default single-instance stores.</summary>
public sealed class RazorDbManagerOptions
{
    /// <summary>The database used by the built-in <c>/db-manager</c> page.</summary>
    public string? DefaultDatabaseId { get; set; }

    /// <summary>Restricts the built-in <c>/db-manager</c> page to read-only behavior.</summary>
    public bool BuiltInPageReadOnly { get; set; }

    /// <summary>Provider-neutral operation limits.</summary>
    public RazorDbResourceLimits ResourceLimits { get; set; } = new();

    /// <summary>Duration for which provider metadata may be cached.</summary>
    public TimeSpan MetadataCacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Directory used for local state and temporary artifacts.</summary>
    public string StoragePath { get; set; } = Path.Combine("App_Data", "RazorDbManager");

    /// <summary>Maximum artifact upload size. Defaults to 100 MiB.</summary>
    [Range(1, 1024L * 1024 * 1024)]
    public long MaximumUploadBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>Lifetime of one-time artifact download tokens.</summary>
    public TimeSpan DownloadTokenLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Lifetime of schema confirmation tokens.</summary>
    public TimeSpan OperationTokenLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Maximum delay before a queued job's authorization envelope expires.</summary>
    public TimeSpan JobAuthorizationLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Lifetime of unclaimed local artifacts.</summary>
    public TimeSpan ArtifactLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Retention period for completed, failed, and cancelled job summaries.</summary>
    public TimeSpan TerminalJobRetention { get; set; } = TimeSpan.FromDays(30);
}
