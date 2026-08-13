using RazorDbManager.Core;

namespace RazorDbManager.Components;

internal sealed class CursorPageNavigation
{
    private readonly List<PageRequest> _pageRequests = [PageRequest.FromOffset(1)];

    public int PageIndex { get; private set; }

    public PageRequest CurrentRequest(int pageSize) => RequestFor(PageIndex, pageSize);

    public bool TryCreateNext(RowPage page, int pageSize, out CursorPageTransition transition)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!page.HasMore)
        {
            transition = default;
            return false;
        }

        PageRequest current = CurrentRequest(pageSize);
        PageRequest request;
        if (page.NextCursor is not null)
        {
            request = PageRequest.FromCursor(pageSize, page.NextCursor);
        }
        else
        {
            long nextOffset = page.NextOffset
                ?? checked((current.Offset ?? 0L) + (page.Rows.Count > 0 ? page.Rows.Count : pageSize));
            request = current.After is not null
                ? PageRequest.FromCursor(pageSize, current.After, nextOffset)
                : PageRequest.FromOffset(pageSize, nextOffset);
        }
        transition = new CursorPageTransition(PageIndex + 1, request);
        return true;
    }

    public bool TryCreatePrevious(int pageSize, out CursorPageTransition transition)
    {
        if (PageIndex == 0)
        {
            transition = default;
            return false;
        }

        int targetIndex = PageIndex - 1;
        transition = new CursorPageTransition(
            targetIndex,
            RequestFor(targetIndex, pageSize));
        return true;
    }

    public void Commit(CursorPageTransition transition)
    {
        if (transition.TargetPageIndex == PageIndex + 1)
        {
            int targetIndex = transition.TargetPageIndex;
            if (_pageRequests.Count > targetIndex)
            {
                _pageRequests.RemoveRange(targetIndex, _pageRequests.Count - targetIndex);
            }
            _pageRequests.Add(transition.Request);
            PageIndex = targetIndex;
            return;
        }

        if (transition.TargetPageIndex == PageIndex - 1)
        {
            PageIndex = transition.TargetPageIndex;
            return;
        }

        throw new ArgumentException("The page transition is no longer current.", nameof(transition));
    }

    public void Reset()
    {
        _pageRequests.Clear();
        _pageRequests.Add(PageRequest.FromOffset(1));
        PageIndex = 0;
    }

    private PageRequest RequestFor(int pageIndex, int pageSize)
    {
        if (pageIndex == 0) return PageRequest.FromOffset(pageSize);
        PageRequest remembered = _pageRequests[pageIndex];
        if (remembered.After is not null)
        {
            return remembered.Offset is long relativeOffset
                ? PageRequest.FromCursor(pageSize, remembered.After, relativeOffset)
                : PageRequest.FromCursor(pageSize, remembered.After);
        }

        return PageRequest.FromOffset(pageSize, remembered.Offset ?? checked((long)pageIndex * pageSize));
    }
}

internal readonly record struct CursorPageTransition(
    int TargetPageIndex,
    PageRequest Request);
