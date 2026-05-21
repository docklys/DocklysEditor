using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace RunModule;

// Hold-to-charge module deletion.
//
// PointerPressed on the Delete widget starts a 2-second charge timer.
// Releasing before 100% cancels (no prompt, no action). Reaching 100%
// flips _deleteCharged; releasing then prompts for the 1234 confirmation
// code. Wrong code or empty cancels. Right code: dotnet sln remove +
// strip any matching ProjectReference from RunModule.csproj + delete the
// folder + refresh catalog.
//
// DefaultModule is protected — Create Module clones from it, so deleting
// it would break the next scaffold. The check happens at press time so
// the user doesn't waste 2 seconds charging.
public partial class MainWindow
{
    private const int DeleteChargeMs = 2000;
    private const string DeleteConfirmCode = "1234";
    private const string ProtectedTemplate = "DefaultModule";

    private DispatcherTimer? _deleteChargeTimer;
    private DateTime _deleteChargeStart;
    private bool _deleteCharged;

    private void DeleteButton_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_catalog.Count == 0) return;

        var current = _catalog[_currentIndex];
        if (string.Equals(current.FolderName, ProtectedTemplate, StringComparison.OrdinalIgnoreCase))
        {
            _ = ShowMessageDialog("Delete Module",
                $"'{ProtectedTemplate}' is the template the Create Module button clones from — " +
                "deleting it would break the next scaffold. Pick a different module to delete.");
            return;
        }

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
    {
        await HandleDeleteRelease();
    }

    // If the user drags off the widget mid-charge, capture is lost — treat
    // that the same as a release-without-confirm so we don't leak state.
    private async void DeleteButton_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        await HandleDeleteRelease();
    }

    private async Task HandleDeleteRelease()
    {
        _deleteChargeTimer?.Stop();
        var wasCharged = _deleteCharged;
        _deleteCharged = false;
        ResetDeleteVisual();

        if (!wasCharged) return;
        if (_catalog.Count == 0) return;

        var current = _catalog[_currentIndex];
        var name = current.FolderName;

        var code = await PromptForCode(
            "Confirm deletion",
            $"To permanently delete '{name}', enter the confirmation code:\n\n" +
            "Code: 1234");
        if (code != DeleteConfirmCode)
        {
            if (!string.IsNullOrWhiteSpace(code))
                await ShowMessageDialog("Delete cancelled",
                    "That wasn't the right code. The module was not deleted.");
            return;
        }

        try
        {
            await DeleteModuleOnDisk(name);
        }
        catch (Exception ex)
        {
            await ShowMessageDialog("Delete failed",
                $"Could not delete '{name}':\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                "If the folder is locked by another process (Rider, VS, Explorer), " +
                "close it and try again.");
            return;
        }

        // Land on the next valid index. If we just deleted the last module
        // the catalog may be empty — ShowModuleAtIndex handles that with
        // the "no modules" placeholder.
        if (_currentIndex >= _catalog.Count) _currentIndex = Math.Max(0, _catalog.Count - 1);
        ShowModuleAtIndex(_currentIndex);
        UpdateScrollArrowVisibility();

        await ShowMessageDialog("Module deleted",
            $"'{name}' has been removed from the solution and disk.");
    }

    private void ResetDeleteVisual()
    {
        var fill = this.FindControl<Border>("DeleteChargeFill");
        var label = this.FindControl<TextBlock>("DeleteButtonLabel");
        if (fill != null) fill.Width = 0;
        if (label != null) label.Text = "✕ Hold to delete";
    }

    private async Task DeleteModuleOnDisk(string folderName)
    {
        var solutionDir = FindEditorSolutionDir()
            ?? throw new IOException("Could not locate the editor solution directory.");

        var folder = Path.Combine(solutionDir, folderName);
        var csproj = Path.Combine(folder, folderName + ".csproj");
        var sln = Path.Combine(solutionDir, "DefaultModule.sln");

        // Remove from the .sln first. Best-effort — non-zero exit is OK
        // because the project may have already been gone from the sln.
        await TryRemoveFromSolution(sln, csproj);

        // Strip the static <ProjectReference> in RunModule.csproj if it
        // points at the doomed module — otherwise the next solution
        // build will fail with "project not found".
        StripProjectReferenceFromRunModule(solutionDir, folderName);

        if (Directory.Exists(folder))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (UnauthorizedAccessException ex)
            {
                // Mark readonly files writable and retry once — git index
                // files inside .git can be marked readonly on Windows.
                Debug.WriteLine($"[Delete] Initial delete blocked ({ex.Message}); clearing readonly and retrying.");
                ClearReadOnlyRecursively(folder);
                Directory.Delete(folder, recursive: true);
            }
        }

        // Re-scan source tree so the catalog reflects the deletion.
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

    private static void StripProjectReferenceFromRunModule(string solutionDir, string folderName)
    {
        var csproj = Path.Combine(solutionDir, "RunModule", "RunModule.csproj");
        if (!File.Exists(csproj)) return;

        var text = File.ReadAllText(csproj);
        var marker = $"<ProjectReference Include=\"..\\{folderName}\\{folderName}.csproj\" />";
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;

        // Remove the whole line including its leading indentation and trailing newline.
        var lineStart = text.LastIndexOf('\n', idx) + 1;
        var lineEnd = text.IndexOf('\n', idx);
        if (lineEnd < 0) lineEnd = text.Length; else lineEnd++;

        text = text.Remove(lineStart, lineEnd - lineStart);
        File.WriteAllText(csproj, text);
    }

    // Tiny modal — same pattern as PromptForModuleName but with a
    // multi-line instruction label above the input.
    private async Task<string?> PromptForCode(string title, string message)
    {
        var tcs = new TaskCompletionSource<string?>();

        var label = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White,
        };
        var textbox = new TextBox { Width = 220, Watermark = "1234" };
        var okBtn = new Button { Content = "Confirm", IsDefault = true, Padding = new Thickness(16, 4) };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(16, 4),
            Margin = new Thickness(8, 0, 0, 0),
        };

        var window = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    label,
                    textbox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 12, 0, 0),
                        Children = { okBtn, cancel },
                    },
                },
            },
        };
        StyleDialog(window);

        okBtn.Click += (_, _) => { tcs.TrySetResult(textbox.Text); window.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(null);
        textbox.AttachedToVisualTree += (_, _) => textbox.Focus();

        await window.ShowDialog(this);
        return await tcs.Task;
    }
}
