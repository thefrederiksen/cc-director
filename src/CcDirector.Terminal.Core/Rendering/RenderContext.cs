namespace CcDirector.Terminal.Core.Rendering;

/// <summary>
/// Carries all state the renderer needs beyond the cell grid:
/// scrollback, scroll offset, selection, cursor, links, font metrics, etc.
/// </summary>
public readonly struct RenderContext
{
    public readonly List<TerminalCell[]> Scrollback;
    public readonly int ScrollOffset;
    public readonly bool HasSelection;
    public readonly int SelectionStartCol;
    public readonly int SelectionStartRow;
    public readonly int SelectionEndCol;
    public readonly int SelectionEndRow;
    public readonly bool CursorVisible;
    public readonly int CursorCol;
    public readonly int CursorRow;
    public readonly List<LinkRegionInfo> LinkRegions;
    public readonly double DpiScale;
    public readonly double FontSize;
    public readonly string? RepoPath;

    public RenderContext(
        List<TerminalCell[]> scrollback,
        int scrollOffset,
        bool hasSelection,
        int selectionStartCol, int selectionStartRow,
        int selectionEndCol, int selectionEndRow,
        bool cursorVisible, int cursorCol, int cursorRow,
        List<LinkRegionInfo> linkRegions,
        double dpiScale, double fontSize,
        string? repoPath)
    {
        Scrollback = scrollback;
        ScrollOffset = scrollOffset;
        HasSelection = hasSelection;
        SelectionStartCol = selectionStartCol;
        SelectionStartRow = selectionStartRow;
        SelectionEndCol = selectionEndCol;
        SelectionEndRow = selectionEndRow;
        CursorVisible = cursorVisible;
        CursorCol = cursorCol;
        CursorRow = cursorRow;
        LinkRegions = linkRegions;
        DpiScale = dpiScale;
        FontSize = fontSize;
        RepoPath = repoPath;
    }
}
