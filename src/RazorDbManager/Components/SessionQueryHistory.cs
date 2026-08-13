using RazorDbManager.Core;

namespace RazorDbManager.Components;

internal sealed record SessionQueryLogEntry(
    DateTimeOffset Timestamp,
    DbObjectName Table,
    int RowsReturned,
    bool Truncated,
    IReadOnlyList<DbCommandDiagnostic> Commands);

internal sealed class SessionQueryHistory(int capacity = 50)
{
    private readonly List<SessionQueryLogEntry> entries = [];

    public IReadOnlyList<SessionQueryLogEntry> Entries => entries;

    public void Add(DbObjectName table, RowPage page, DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Commands.Count == 0) return;

        entries.Insert(0, new SessionQueryLogEntry(
            timestamp ?? DateTimeOffset.UtcNow,
            table,
            page.Rows.Count,
            page.Truncated,
            page.Commands));
        if (entries.Count > capacity)
        {
            entries.RemoveRange(capacity, entries.Count - capacity);
        }
    }

    public void Clear() => entries.Clear();
}
