using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace RunModule;

// The "▶ Start Docklys" / "■ Close Docklys" button.
//
// This used to be folded into the Push button as a three-way state machine (push → "Start
// Docklys!" → "Close Docklys!"), which made the label describe the next click rather than the
// button's own action, and left Start reachable only immediately after a push. It's now a
// standalone control whose label simply mirrors whether Docklys is currently running.
public partial class MainWindow
{
    private DispatcherTimer? _docklyStateTimer;

    // Set while a start/close is in flight, so the poll below doesn't overwrite the transient
    // "Starting…" / "Closing…" label with the process state it's about to change anyway.
    private bool _lifecycleBusy;

    // Sticky, like the Push button's error state: a failed start stays on screen (hover for the
    // full search report, click to copy it and retry) instead of being wiped by the next poll.
    private string? _lastLifecycleError;

    private void InitializeDocklyLifecycleButton()
    {
        RefreshDocklyLifecycleButton();

        // Docklys can also be started or closed outside this editor, so poll rather than only
        // refreshing after our own actions.
        _docklyStateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _docklyStateTimer.Tick += (_, _) => RefreshDocklyLifecycleButton();
        _docklyStateTimer.Start();
        this.Closed += (_, _) => _docklyStateTimer?.Stop();
    }

    private void RefreshDocklyLifecycleButton()
    {
        if (_lifecycleBusy) return;
        var btn = this.FindControl<Button>("DocklyLifecycleButton");
        if (btn == null) return;

        btn.IsEnabled = true;

        if (_lastLifecycleError != null)
        {
            btn.Content = "✗ Start failed — click to copy & retry";
            ToolTip.SetTip(btn, new TextBlock
            {
                Text = _lastLifecycleError,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 600,
                FontFamily = new FontFamily("Consolas,Courier New,monospace"),
                FontSize = 11,
            });
            return;
        }

        var running = IsDocklyRunning();
        var content = running ? "■ Close Docklys" : "▶ Start Docklys";
        if (btn.Content as string != content) btn.Content = content;
        ToolTip.SetTip(btn, running
            ? "Terminate every running Docklys instance."
            : "Launch Docklys.");
    }

    private async void DocklyLifecycle_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (_lifecycleBusy) return;

        _lifecycleBusy = true;
        btn.IsEnabled = false;
        try
        {
            // In the error state, the click copies the report and retries — same contract as the
            // Push button, so both error affordances behave identically.
            if (_lastLifecycleError != null)
            {
                var errorText = _lastLifecycleError;
                _lastLifecycleError = null;
                try
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard != null) await clipboard.SetTextAsync(errorText);
                }
                catch { }
                btn.Content = "✓ Copied! Retrying...";
                ToolTip.SetTip(btn, null);
                await Task.Delay(700);
            }

            if (IsDocklyRunning())
            {
                btn.Content = "Closing Docklys...";
                ToolTip.SetTip(btn, null);
                try
                {
                    await Task.Run(KillDocklyProcesses);
                }
                catch (Exception killEx)
                {
                    Debug.WriteLine($"[Lifecycle] Kill Docklys failed: {killEx}");
                }
                // Give the OS a moment to release the file handles on the module DLLs, so an
                // immediately following push isn't blocked by a process that's already gone.
                await Task.Delay(500);
            }
            else
            {
                btn.Content = "Starting Docklys...";
                ToolTip.SetTip(btn, null);

                // Discovery walks bin trees and probes processes — keep it off the UI thread.
                var (started, errors) = await Task.Run(StartDocklyInstances);

                if (started.Count == 0)
                {
                    _lastLifecycleError = errors.Count > 0
                        ? string.Join("\n\n", errors)
                        : "Could not locate the Docklys executable.";
                }
                else
                {
                    btn.Content = started.Count == 1 ? "✓ Started" : $"✓ Started ({started.Count})";
                    var tip = "Launched:\n  " + string.Join("\n  ", started);
                    if (errors.Count > 0) tip += "\n\nFailed:\n  " + string.Join("\n  ", errors);
                    ToolTip.SetTip(btn, tip);
                    await Task.Delay(1500);
                }
            }
        }
        finally
        {
            _lifecycleBusy = false;
            RefreshDocklyLifecycleButton();
        }
    }

    // Relaunches every instance the last Close terminated; falls back to auto-discovery when we
    // have no such record (nothing was closed from here this session).
    private (List<string> Started, List<string> Errors) StartDocklyInstances()
    {
        List<string> exes;
        lock (_lastDocklyExePaths) exes = _lastDocklyExePaths.ToList();

        var errors = new List<string>();
        if (exes.Count == 0)
        {
            var found = FindDocklyExecutableWithReport();
            if (found.exePath != null) exes.Add(found.exePath);
            else errors.Add(found.searchReport ?? "Could not locate the Docklys executable.");
        }

        var started = new List<string>();
        foreach (var exe in exes)
        {
            if (!File.Exists(exe))
            {
                errors.Add($"{exe}: file no longer exists");
                continue;
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    // We have resolved the executable itself. On Linux shell execution delegates
                    // to xdg-open, which is for documents/URLs and can return without launching
                    // this apphost. Start the apphost directly instead.
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                });
                started.Add(exe);
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to launch {exe}\n\n{ex.GetType().Name}: {ex.Message}");
                Debug.WriteLine($"[Lifecycle] Start Docklys failed for {exe}: {ex}");
            }
        }

        lock (_lastDocklyExePaths)
        {
            _lastDocklyExePaths.Clear();
            foreach (var s in started) _lastDocklyExePaths.Add(s);
        }
        return (started, errors);
    }
}
