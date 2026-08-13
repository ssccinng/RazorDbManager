namespace RazorDbManager.Components;

internal enum WorkspaceTab
{
    Data,
    Structure,
    Sql,
    Transfer,
    Activity,
}

internal static class WorkspaceTabNavigation
{
    internal static readonly WorkspaceTab[] Order =
        [WorkspaceTab.Data, WorkspaceTab.Structure, WorkspaceTab.Sql, WorkspaceTab.Transfer, WorkspaceTab.Activity];

    public static WorkspaceTab? Move(
        WorkspaceTab current,
        string? key,
        Func<WorkspaceTab, bool> isEnabled)
    {
        ArgumentNullException.ThrowIfNull(isEnabled);
        WorkspaceTab[] enabled = Order.Where(isEnabled).ToArray();
        if (enabled.Length == 0) return null;

        return key switch
        {
            "Home" => enabled[0],
            "End" => enabled[^1],
            "ArrowRight" => MoveBy(enabled, current, 1),
            "ArrowLeft" => MoveBy(enabled, current, -1),
            _ => null,
        };
    }

    public static WorkspaceTab Normalize(WorkspaceTab current, Func<WorkspaceTab, bool> isEnabled)
    {
        ArgumentNullException.ThrowIfNull(isEnabled);
        if (isEnabled(current)) return current;
        return Order.FirstOrDefault(isEnabled);
    }

    private static WorkspaceTab MoveBy(WorkspaceTab[] enabled, WorkspaceTab current, int delta)
    {
        int index = Array.IndexOf(enabled, current);
        if (index < 0) return delta > 0 ? enabled[0] : enabled[^1];
        return enabled[(index + delta + enabled.Length) % enabled.Length];
    }
}
