using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace RunPattern;

// Hold-to-charge pattern deletion — identical workflow to RunModule.
//
// PointerPressed starts a 2-second charge. Releasing before 100% cancels.
// Reaching 100% then releasing prompts for the 1234 confirmation code. The
// right code removes the pattern from DefaultModule.sln, strips any matching
// <ProjectReference> in RunPattern.csproj, deletes the folder, and refreshes
// the catalog.
public partial class MainWindow
{
    private const int DeleteChargeMs = 2000;
    private const string DeleteConfirmCode = "1234";

    private DispatcherTimer? _deleteChargeTimer;
    private DateTime _deleteChargeStart;
    private bool _deleteCharged;

    private void DeleteButton_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_catalog.Count == 0) return;

        if (sender is IInputElement el) e.Pointer.Capture(el);

        _deleteChargeStart = DateTime.UtcNow;
        _deleteCharged = false;
        StartDeleteChargeTimer();
    }

    private void StartDeleteChargeTimer()
    {
        _deleteChargeTimer?.Stop();
        _deleteChargeTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            OnDeleteChargeTick);
        _deleteChargeTimer.Start();
    }

    private void OnDeleteChargeTick(object? sender, EventArgs e)
    {
        var btn = this.FindControl<Border>("DeleteButton");
        var fill = this.FindControl<Border>("DeleteChargeFill");
        var label = this.FindControl<TextBlock>("DeleteButtonLabel");
        if (btn == null || fill == null) return;

        var elapsed = (DateTime.UtcNow - _deleteChargeStart).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / DeleteChargeMs, 0.0, 1.0);
        fill.Width = btn.Bounds.Width * progress;

        if (label != null)
        {
            label.Text = progress < 1.0
                ? $"✕ Charging {(int)(progress * 100)}%"
                : "✕ RELEASE TO CONFIRM";
        }

        if (progress >= 1.0 && !_deleteCharged)
        {
            _deleteCharged = true;
            _deleteChargeTimer?.Stop();
        }
    }

    private async void DeleteButton_PointerReleased(object? sender, PointerReleasedEventArgs e)
        => await HandleDeleteRelease();

    // Dragging off the widget loses capture — treat as release-without-confirm.
    private async void DeleteButton_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => await HandleDeleteRelease();

    private async Task HandleDeleteRelease()
    {
        _deleteChargeTimer?.Stop();
        var wasCharged = _deleteCharged;
        _deleteCharged = false;
        ResetDeleteVisual();

        if (!wasCharged) return;
        if (_catalog.Count == 0) return;

        var name = _catalog[_currentIndex].FolderName;

        var code = await PromptForCode(
            "Confirm deletion",
            $"To permanently delete '{name}', enter the confirmation code:\n\nCode: 1234");
        if (code != DeleteConfirmCode)
        {
            if (!string.IsNullOrWhiteSpace(code))
                await ShowMessageDialog("Delete cancelled",
                    "That wasn't the right code. The pattern was not deleted.");
            return;
        }

        try
        {
            await DeletePatternOnDisk(name);
        }
        catch (Exception ex)
        {
            await ShowMessageDialog("Delete failed",
                $"Could not delete '{name}':\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                "If the folder is locked by another process (Rider, VS, Explorer), close it and try again.");
            return;
        }

        if (_currentIndex >= _catalog.Count) _currentIndex = Math.Max(0, _catalog.Count - 1);
        ShowPatternAtIndex(_currentIndex);

        await ShowMessageDialog("Pattern deleted",
            $"'{name}' has been removed from the solution and disk.");
    }

    private void ResetDeleteVisual()
    {
        var fill = this.FindControl<Border>("DeleteChargeFill");
        var label = this.FindControl<TextBlock>("DeleteButtonLabel");
        if (fill != null) fill.Width = 0;
        if (label != null) label.Text = "✕ Hold to delete";
    }

    private async Task DeletePatternOnDisk(string folderName)
    {
        var solutionDir = FindEditorSolutionDir()
            ?? throw new IOException("Could not locate the editor solution directory.");
        var patternsDir = Path.Combine(solutionDir, "Patterns");

        var folder = Path.Combine(patternsDir, folderName);
        var csproj = Path.Combine(folder, folderName + ".csproj");
        var sln = Path.Combine(solutionDir, "DefaultModule.sln");

        await TryRemoveFromSolution(sln, csproj);
        StripProjectReferenceFromRunPattern(solutionDir, folderName);

        if (Directory.Exists(folder))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"[Delete] blocked ({ex.Message}); clearing readonly and retrying.");
                ClearReadOnlyRecursively(folder);
                Directory.Delete(folder, recursive: true);
            }
        }

        // Also drop the deployed copy so the carousel doesn't rediscover it.
        var outDll = Path.Combine(solutionDir, "OutputPatternDLL", folderName + ".dll");
        try { if (File.Exists(outDll)) File.Delete(outDll); }
        catch (Exception ex) { Debug.WriteLine($"[Delete] could not remove {outDll}: {ex.Message}"); }

        LoadCatalog();
    }

    private static void ClearReadOnlyRecursively(string root)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); }
                catch (Exception ex) { Debug.WriteLine($"[Delete] attribute clear failed for {f}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[Delete] ClearReadOnlyRecursively scan failed: {ex.Message}"); }
    }
}
