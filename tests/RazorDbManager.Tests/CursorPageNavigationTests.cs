using RazorDbManager.Components;
using RazorDbManager.Core;

namespace RazorDbManager.Tests;

public sealed class CursorPageNavigationTests
{
    [Fact]
    public void StartsAtOffsetZeroAndUsesCursorForNextPage()
    {
        var navigation = new CursorPageNavigation();
        RowCursor cursor = new([DbValue.FromSignedInteger(100)]);
        RowPage page = Page(cursor, hasMore: true);

        Assert.Equal(0, navigation.CurrentRequest(50).Offset);
        Assert.True(navigation.TryCreateNext(page, 50, out CursorPageTransition next));
        Assert.Same(cursor, next.Request.After);

        navigation.Commit(next);

        Assert.Equal(1, navigation.PageIndex);
        Assert.Same(cursor, navigation.CurrentRequest(50).After);
    }

    [Fact]
    public void PreviousUsesRememberedForwardCursorAndFirstPageUsesOffset()
    {
        var navigation = new CursorPageNavigation();
        RowCursor firstCursor = new([DbValue.FromSignedInteger(100)]);
        RowCursor secondCursor = new([DbValue.FromSignedInteger(200)]);
        Assert.True(navigation.TryCreateNext(Page(firstCursor, true), 50, out CursorPageTransition second));
        navigation.Commit(second);
        Assert.True(navigation.TryCreateNext(Page(secondCursor, true), 50, out CursorPageTransition third));
        navigation.Commit(third);

        Assert.True(navigation.TryCreatePrevious(50, out CursorPageTransition backToSecond));
        Assert.Same(firstCursor, backToSecond.Request.After);
        navigation.Commit(backToSecond);
        Assert.True(navigation.TryCreatePrevious(50, out CursorPageTransition backToFirst));
        Assert.Equal(0, backToFirst.Request.Offset);
    }

    [Fact]
    public void FailedOrUnavailableTransitionDoesNotMovePage()
    {
        var navigation = new CursorPageNavigation();

        Assert.False(navigation.TryCreatePrevious(50, out _));
        Assert.True(navigation.TryCreateNext(Page(null, hasMore: true), 50, out CursorPageTransition fallback));
        Assert.Equal(50, fallback.Request.Offset);
        Assert.False(navigation.TryCreateNext(Page(new RowCursor([DbValue.FromSignedInteger(1)]), hasMore: false), 50, out _));
        Assert.Equal(0, navigation.PageIndex);
    }

    [Fact]
    public void OffsetFallbackIsRememberedForPreviousNavigation()
    {
        var navigation = new CursorPageNavigation();
        Assert.True(navigation.TryCreateNext(Page(null, hasMore: true), 25, out CursorPageTransition second));
        navigation.Commit(second);
        Assert.True(navigation.TryCreateNext(Page(null, hasMore: true), 25, out CursorPageTransition third));
        navigation.Commit(third);

        Assert.True(navigation.TryCreatePrevious(25, out CursorPageTransition backToSecond));
        Assert.Equal(25, backToSecond.Request.Offset);
        navigation.Commit(backToSecond);
        Assert.Equal(1, navigation.PageIndex);
    }

    [Fact]
    public void TruncatedSortPageWithMoreRowsUsesAbsoluteOffsetFallback()
    {
        var navigation = new CursorPageNavigation();
        RowPage truncated = new(
            [],
            [],
            null,
            NextCursor: null,
            HasMore: true,
            "fingerprint",
            Truncated: true,
            NextOffset: 37);

        Assert.True(navigation.TryCreateNext(truncated, 100, out CursorPageTransition next));

        Assert.Equal(37, next.Request.Offset);
        Assert.Null(next.Request.After);
    }

    [Fact]
    public void ExactContinuationOffsetIsRememberedAcrossForwardAndPreviousNavigation()
    {
        var navigation = new CursorPageNavigation();
        RowPage first = new([], [], null, null, true, "fingerprint", Truncated: true, NextOffset: 7);
        Assert.True(navigation.TryCreateNext(first, 100, out CursorPageTransition second));
        navigation.Commit(second);

        RowPage secondPage = new([], [], null, null, true, "fingerprint", Truncated: true, NextOffset: 12);
        Assert.True(navigation.TryCreateNext(secondPage, 100, out CursorPageTransition third));
        navigation.Commit(third);

        Assert.True(navigation.TryCreatePrevious(100, out CursorPageTransition backToSecond));
        Assert.Equal(7, backToSecond.Request.Offset);
        navigation.Commit(backToSecond);
        Assert.Equal(7, navigation.CurrentRequest(100).Offset);
    }

    [Fact]
    public void TruncatedSortAfterKeysetPagePreservesCursorAnchorAndRelativeOffset()
    {
        var navigation = new CursorPageNavigation();
        RowCursor anchor = new([DbValue.FromString("short"), DbValue.FromSignedInteger(1)]);
        Assert.True(navigation.TryCreateNext(Page(anchor, true), 100, out CursorPageTransition anchoredPage));
        navigation.Commit(anchoredPage);

        RowPage truncated = new(
            [],
            [new DbRow([DbValue.FromString("truncated"), DbValue.FromSignedInteger(2)], null)],
            null,
            NextCursor: null,
            HasMore: true,
            "fingerprint",
            Truncated: true,
            NextOffset: 1);
        Assert.True(navigation.TryCreateNext(truncated, 100, out CursorPageTransition continuedPage));

        Assert.Same(anchor, continuedPage.Request.After);
        Assert.Equal(1, continuedPage.Request.Offset);
        navigation.Commit(continuedPage);
        Assert.Same(anchor, navigation.CurrentRequest(100).After);
        Assert.Equal(1, navigation.CurrentRequest(100).Offset);
    }

    [Fact]
    public void KeysetThenTruncatedSortContinuationTraversesRowsWithoutDuplicateOrSkip()
    {
        int[] orderedRows = [1, 2, 3, 4, 5, 6, 7, 8];
        var navigation = new CursorPageNavigation();
        List<int> visited = [];

        PageRequest firstRequest = navigation.CurrentRequest(2);
        int[] firstRows = Apply(firstRequest, orderedRows, 2);
        visited.AddRange(firstRows);
        RowCursor anchor = new([DbValue.FromSignedInteger(firstRows[^1])]);
        RowPage firstPage = Result(firstRows, anchor, nextOffset: null);
        Assert.True(navigation.TryCreateNext(firstPage, 2, out CursorPageTransition second));
        navigation.Commit(second);

        PageRequest secondRequest = navigation.CurrentRequest(2);
        int[] secondRows = Apply(secondRequest, orderedRows, 2);
        visited.AddRange(secondRows);
        RowPage truncatedSortPage = Result(secondRows, cursor: null, nextOffset: 2);
        Assert.True(navigation.TryCreateNext(truncatedSortPage, 2, out CursorPageTransition third));
        navigation.Commit(third);

        PageRequest thirdRequest = navigation.CurrentRequest(2);
        Assert.Same(anchor, thirdRequest.After);
        Assert.Equal(2, thirdRequest.Offset);
        int[] thirdRows = Apply(thirdRequest, orderedRows, 2);
        visited.AddRange(thirdRows);

        Assert.Equal([1, 2, 3, 4, 5, 6], visited);
        Assert.Equal(visited.Count, visited.Distinct().Count());
    }

    [Fact]
    public void ResetDropsHistoryAndReturnsToOffsetFirstPage()
    {
        var navigation = new CursorPageNavigation();
        Assert.True(navigation.TryCreateNext(
            Page(new RowCursor([DbValue.FromSignedInteger(1)]), true),
            25,
            out CursorPageTransition next));
        navigation.Commit(next);

        navigation.Reset();

        Assert.Equal(0, navigation.PageIndex);
        Assert.Equal(0, navigation.CurrentRequest(25).Offset);
        Assert.False(navigation.TryCreatePrevious(25, out _));
    }

    private static RowPage Page(RowCursor? cursor, bool hasMore) =>
        new([], [], null, cursor, hasMore, "fingerprint");

    private static RowPage Result(IReadOnlyList<int> values, RowCursor? cursor, long? nextOffset) =>
        new(
            [],
            values.Select(value => new DbRow([DbValue.FromSignedInteger(value)], null)).ToArray(),
            null,
            cursor,
            HasMore: true,
            "fingerprint",
            Truncated: true,
            nextOffset);

    private static int[] Apply(PageRequest request, IReadOnlyList<int> rows, int take)
    {
        IEnumerable<int> query = rows;
        if (request.After is not null)
        {
            int anchor = int.Parse(request.After.Values[0].Text!, System.Globalization.CultureInfo.InvariantCulture);
            query = query.Where(value => value > anchor);
        }

        return query.Skip(checked((int)(request.Offset ?? 0))).Take(take).ToArray();
    }
}
