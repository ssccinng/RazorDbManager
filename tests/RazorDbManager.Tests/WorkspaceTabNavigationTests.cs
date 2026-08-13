using RazorDbManager.Components;

namespace RazorDbManager.Tests;

public sealed class WorkspaceTabNavigationTests
{
    [Theory]
    [InlineData(0, "ArrowRight", 1)]
    [InlineData(4, "ArrowRight", 0)]
    [InlineData(0, "ArrowLeft", 4)]
    [InlineData(2, "Home", 0)]
    [InlineData(2, "End", 4)]
    public void MoveSupportsRovingTabKeyboardNavigation(int currentValue, string key, int expectedValue)
    {
        WorkspaceTab current = (WorkspaceTab)currentValue;
        WorkspaceTab expected = (WorkspaceTab)expectedValue;
        WorkspaceTab? result = WorkspaceTabNavigation.Move(current, key, _ => true);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void MoveSkipsDisabledTabsAndWraps()
    {
        bool Enabled(WorkspaceTab tab) => tab is not (WorkspaceTab.Sql or WorkspaceTab.Transfer);

        Assert.Equal(WorkspaceTab.Activity, WorkspaceTabNavigation.Move(WorkspaceTab.Structure, "ArrowRight", Enabled));
        Assert.Equal(WorkspaceTab.Structure, WorkspaceTabNavigation.Move(WorkspaceTab.Activity, "ArrowLeft", Enabled));
    }

    [Fact]
    public void MoveIgnoresUnrelatedKeys()
    {
        Assert.Null(WorkspaceTabNavigation.Move(WorkspaceTab.Data, "Enter", _ => true));
    }

    [Fact]
    public void NormalizeMovesDisabledSelectionToFirstEnabledTab()
    {
        WorkspaceTab normalized = WorkspaceTabNavigation.Normalize(
            WorkspaceTab.Sql,
            tab => tab is not (WorkspaceTab.Data or WorkspaceTab.Sql));

        Assert.Equal(WorkspaceTab.Structure, normalized);
    }
}
