using Avalonia.Controls;
using Avalonia.Media;
using CcDirector.Terminal.Core;
using CcDirector.Terminal.Core.Rendering;

namespace CcDirector.Terminal.Avalonia.Rendering;

/// <summary>
/// Interface for terminal rendering modes (Avalonia version).
/// Each implementation reads the same TerminalCell[,] grid but paints it differently.
/// </summary>
public interface ITerminalRenderer
{
    /// <summary>Display name for the mode button (e.g. "ORG", "PRO", "LITE").</summary>
    string Name { get; }

    /// <summary>
    /// Render the terminal cell grid into the DrawingContext.
    /// </summary>
    void Render(DrawingContext dc, TerminalCell[,] cells, int cols, int rows,
                double cellWidth, double cellHeight, RenderContext ctx);

    /// <summary>
    /// Apply control-level settings to the host element.
    /// Called once when the renderer is activated.
    /// </summary>
    void ApplyControlSettings(Control control);

    /// <summary>
    /// The background color for the terminal area.
    /// </summary>
    Color GetBackgroundColor();
}
