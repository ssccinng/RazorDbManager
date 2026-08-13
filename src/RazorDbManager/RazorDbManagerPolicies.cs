namespace RazorDbManager;

/// <summary>Stable policy names used by RazorDbManager.</summary>
public static class RazorDbManagerPolicies
{
    /// <summary>The policy required for every manager operation.</summary>
    public const string Access = "RazorDbManager.Access";
    /// <summary>The additional policy required for arbitrary SQL and destructive schema operations.</summary>
    public const string HighRisk = "RazorDbManager.HighRisk";
}
