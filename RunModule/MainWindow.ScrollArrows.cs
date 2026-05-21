using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RunModule;

// Carousel cycle: ◀ / ▶ swap which module the ActiveModuleSlot is showing.
// Only one module is on screen at a time; the arrows hide when there's
// nothing else to cycle to (catalog count ≤ 1).
public partial class MainWindow
{
    private void ScrollLeft_Click(object? sender, RoutedEventArgs e) => CycleBy(-1);

    private void ScrollRight_Click(object? sender, RoutedEventArgs e) => CycleBy(+1);

    private void CycleBy(int direction)
    {
        if (_catalog.Count == 0) return;

        // Modular wrap-around so the carousel feels continuous.
        var n = _catalog.Count;
        _currentIndex = ((_currentIndex + direction) % n + n) % n;

        ShowModuleAtIndex(_currentIndex);
    }

    private void UpdateScrollArrowVisibility()
    {
        var left = this.FindControl<Button>("ScrollLeftButton");
        var right = this.FindControl<Button>("ScrollRightButton");
        if (left == null || right == null) return;

        // Both arrows track catalog size, not scroll position — wrap-around
        // means both directions are always meaningful as long as there's
        // more than one module loaded.
        var canCycle = _catalog.Count > 1;
        left.IsVisible = canCycle;
        right.IsVisible = canCycle;
    }
}
