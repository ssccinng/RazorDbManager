using RazorDbManager.Components;
using RazorDbManager.Core;

namespace RazorDbManager.Tests;

public sealed class SessionQueryHistoryTests
{
    [Fact]
    public void Add_KeepsNewestFiftyEntriesAndClearRemovesThem()
    {
        var history = new SessionQueryHistory();

        for (var index = 0; index < 55; index++)
        {
            history.Add(
                new DbObjectName("app", $"table_{index}"),
                PageWithCommand($"SELECT {index}"),
                DateTimeOffset.UnixEpoch.AddMinutes(index));
        }

        Assert.Equal(50, history.Entries.Count);
        Assert.Equal("table_54", history.Entries[0].Table.Name);
        Assert.Equal("table_5", history.Entries[^1].Table.Name);
        Assert.Equal("SELECT 54", history.Entries[0].Commands[0].CommandText);

        history.Clear();

        Assert.Empty(history.Entries);
    }

    [Fact]
    public void Add_IgnoresPagesWithoutProviderDiagnostics()
    {
        var history = new SessionQueryHistory();
        var page = new RowPage([], [], null, null, false, "fingerprint");

        history.Add(new DbObjectName("app", "items"), page);

        Assert.Empty(history.Entries);
    }

    private static RowPage PageWithCommand(string sql) => new(
        [],
        [new DbRow([], null)],
        null,
        null,
        false,
        "fingerprint")
    {
        Commands = [new DbCommandDiagnostic(sql, [], TimeSpan.FromMilliseconds(2))],
    };
}
