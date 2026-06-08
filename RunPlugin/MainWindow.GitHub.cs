using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace RunPlugin;

public partial class MainWindow
{
    private sealed record GitHubSpec(string RepoName, string Description, bool IsPublic);

    private async void OnGitHubClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) { await ShowMessageDialog("GitHub", "No plugin selected."); return; }

        var entry = _catalog[Math.Clamp(_currentIndex, 0, _catalog.Count - 1)];
        var folder = Path.GetDirectoryName(entry.CsprojPath);
        if (folder == null || !Directory.Exists(folder))
        {
            await ShowMessageDialog("GitHub", "Plugin source folder not found.");
            return;
        }

        var repoName = "Docklys.Plugin." + entry.FolderName;
        if (!IsGhCliInstalled()) { await ShowGhCliMissingDialog(); return; }
        if (!await EnsureGhAuth()) return;

        bool isUpdate = await RepoExists(repoName);
        var suggestedDesc = isUpdate ? "Update plugin files" : $"A Docklys plugin: {entry.FolderName}";

        var spec = await PromptGitHubSpec(repoName, suggestedDesc, isUpdate);
        if (spec == null) return;

        await RunGitHubFlow(folder, spec, isUpdate);
    }

    private async Task RunGitHubFlow(string folder, GitHubSpec spec, bool isUpdate)
    {
        var btn = this.FindControl<Button>("GitHubButton");

        if (btn != null) btn.IsEnabled = false;

        try
        {
            // Ensure the folder is a git repo.
            bool gitInited = Directory.Exists(Path.Combine(folder, ".git"));
            if (!gitInited)
            {
                var (initOk, initLog) = await RunProcessAsync("git", "init", folder);
                if (!initOk) { await ShowMessageDialog("git init failed", initLog); return; }
            }

            if (isUpdate)
            {
                await RunProcessAsync("git", "add .", folder);
                await RunProcessAsync("git", $"commit -m \"{spec.Description}\"", folder);

                // If we just inited, we need to add remote and push with upstream
                if (!gitInited)
                {
                    var user = GetGitHubUser();
                    await RunProcessAsync("git", $"remote add origin https://github.com/{user}/{spec.RepoName}.git", folder);
                    var (okPush, logPush) = await RunProcessAsync("git", "push -u origin main", folder);
                    if (okPush) await ShowMessageDialog("Repository Updated", $"✓ '{spec.RepoName}' updated on GitHub.");
                    else await ShowMessageDialog("Update Failed", $"git push failed:\n\n{logPush}");
                }
                else
                {
                    var (okPush, logPush) = await RunProcessAsync("git", "push", folder);
                    if (okPush) await ShowMessageDialog("Repository Updated", $"✓ '{spec.RepoName}' updated on GitHub.");
                    else await ShowMessageDialog("Update Failed", $"git push failed:\n\n{logPush}");
                }
                return;
            }

            // Create workflow
            await RunProcessAsync("git", "add .", folder);
            await RunProcessAsync("git", $"commit -m \"Initial commit: {spec.RepoName}\"", folder);

            var visibility = spec.IsPublic ? "--public" : "--private";
            var ghPath = ResolveGhPath();
            var (ok, log) = await RunProcessAsync(ghPath,
                $"repo create \"{spec.RepoName}\" --description \"{spec.Description}\" {visibility} --source . --push",
                folder);

            if (ok)
                await ShowMessageDialog("Repository Created",
                    $"✓ '{spec.RepoName}' pushed to GitHub.");
            else
                await ShowMessageDialog("GitHub Failed", $"gh repo create failed:\n\n{log}");
        }
        catch (Exception ex)
        {
            await ShowMessageDialog("GitHub Error", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (btn != null) btn.IsEnabled = true;
        }
    }

    private async Task ShowGhCliMissingDialog()
    {
        var tcs = new TaskCompletionSource<bool>();
        var installBtn = new Button { Content = "Install GitHub CLI", Padding = new Thickness(14, 4), Background = Brushes.DarkGreen, Foreground = Brushes.White };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4), Margin = new Thickness(8, 0, 0, 0) };

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        if (IsWingetAvailable()) buttonRow.Children.Add(installBtn);
        buttonRow.Children.Add(cancel);

        var textPanel = new StackPanel { Spacing = 8 };
        textPanel.Children.Add(new TextBlock { Text = "GitHub CLI (gh) Not Found", FontWeight = FontWeight.Bold, FontSize = 16, Foreground = Brushes.White });
        textPanel.Children.Add(new TextBlock { Text = "The GitHub CLI is required to create and push repositories directly from the editor.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White });
        
        if (IsWingetAvailable())
        {
            textPanel.Children.Add(new TextBlock { Text = "Would you like to install it now using winget?", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White });
        }
        else
        {
            textPanel.Children.Add(new TextBlock { Text = "Please install it manually from:", Foreground = Brushes.White });
            var linkBtn = new Button { Content = "https://cli.github.com", Foreground = Brushes.SkyBlue, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Avalonia.Input.Cursor.Parse("Hand"), Padding = new Thickness(0) };
            linkBtn.Click += (_, _) => Process.Start(new ProcessStartInfo("https://cli.github.com") { UseShellExecute = true });
            textPanel.Children.Add(linkBtn);
            textPanel.Children.Add(new TextBlock { Text = "After installation, run 'gh auth login' in a terminal.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White });
        }

        var scrollViewer = new ScrollViewer { Content = textPanel, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        
        var dockPanel = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        DockPanel.SetDock(buttonRow, Dock.Bottom);
        dockPanel.Children.Add(buttonRow);
        dockPanel.Children.Add(scrollViewer);

        var window = new Window
        {
            Title = "GitHub CLI",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            MinHeight = 200,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = dockPanel
        };
        StyleDialog(window);

        installBtn.Click += async (_, _) =>
        {
            installBtn.IsEnabled = false;
            installBtn.Content = "Installing...";
            
            var (ok, log) = await RunProcessAsync("powershell", "-NoProfile -Command \"winget install GitHub.cli --silent --accept-source-agreements --accept-package-agreements\"", AppContext.BaseDirectory);
            
            if (ok)
            {
                await ShowMessageDialog("Success", "GitHub CLI installed successfully. Please restart the application for PATH changes to take effect.");
                window.Close();
            }
            else
            {
                await ShowMessageDialog("Installation Failed", $"Could not install GitHub CLI via winget:\n\n{log}\n\nYou can install it manually from https://cli.github.com");
                installBtn.IsEnabled = true;
                installBtn.Content = "Install GitHub CLI";
            }
        };

        cancel.Click += (_, _) => window.Close();

        await window.ShowDialog(this);
    }

    private static string ResolveGhPath()
    {
        foreach (var path in new[] {
            "gh",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "GitHub CLI", "gh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI", "gh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitHub CLI", "gh.exe")
        })
        {
            try
            {
                var psi = new ProcessStartInfo(path, "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi);
                p?.WaitForExit(1000);
                if (p?.ExitCode == 0) return path;
            } catch { }
        }
        return "gh";
    }

    private static bool IsGhCliInstalled()
    {
        return ResolveGhPath() != "gh" || CanRun("gh", "--version");
    }

    private async Task<bool> EnsureGhAuth()
    {
        if (await IsGhAuthenticated()) return true;

        var loginBtn = new Button { Content = "Login to GitHub", Padding = new Thickness(14, 4), Background = Brushes.DarkBlue, Foreground = Brushes.White };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4), Margin = new Thickness(8, 0, 0, 0) };
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { loginBtn, cancel } };

        var textPanel = new StackPanel { Spacing = 8, Children = {
            new TextBlock { Text = "GitHub Login Required", FontWeight = FontWeight.Bold, FontSize = 16, Foreground = Brushes.White },
            new TextBlock { Text = "Authentication is required to create repositories. A terminal will open to handle the login via your browser.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White },
            new TextBlock { Text = "1. If a terminal appears, press Enter if prompted.", Foreground = Brushes.Gray, FontSize = 12 },
            new TextBlock { Text = "2. Complete the login in your web browser.", Foreground = Brushes.Gray, FontSize = 12 },
            new TextBlock { Text = "3. The editor will continue once the login is finished.", Foreground = Brushes.Gray, FontSize = 12 }
        }};

        var window = new Window { Title = "GitHub Login", Width = 400, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new DockPanel { Margin = new Thickness(16), Children = { buttonRow, textPanel } } };
        DockPanel.SetDock(buttonRow, Dock.Bottom);
        StyleDialog(window);

        bool authenticated = false;
        loginBtn.Click += async (_, _) => {
            loginBtn.IsEnabled = false;
            loginBtn.Content = "Processing...";
            try {
                var ghPath = ResolveGhPath();
                var psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"echo `n | & '{ghPath}' auth login -h github.com -p https -w\"") {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal 
                };
                var p = Process.Start(psi);
                if (p != null) await p.WaitForExitAsync();
                
                authenticated = await IsGhAuthenticated();
                if (authenticated) window.Close();
                else {
                    await ShowMessageDialog("Auth Failed", "Authentication was not completed. Please try again.");
                    loginBtn.IsEnabled = true;
                    loginBtn.Content = "Login to GitHub";
                }
            } catch (Exception ex) {
                await ShowMessageDialog("Auth Error", $"Failed to start login: {ex.Message}");
                loginBtn.IsEnabled = true;
                loginBtn.Content = "Login to GitHub";
            }
        };
        cancel.Click += (_, _) => window.Close();
        await window.ShowDialog(this);
        return authenticated;
    }

    private static async Task<bool> IsGhAuthenticated()
    {
        var (ok, _) = await RunProcessAsync(ResolveGhPath(), "auth status", AppContext.BaseDirectory);
        return ok;
    }

    private static bool CanRun(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            p?.WaitForExit(1000);
            return p?.ExitCode == 0;
        } catch { return false; }
    }

    private static bool IsWingetAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("winget", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string GetGitHubUser()
    {
        try
        {
            var psi = new ProcessStartInfo(ResolveGhPath(), "api user --jq .login")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            var user = p?.StandardOutput.ReadToEnd().Trim();
            p?.WaitForExit(5000);
            return user ?? "you";
        }
        catch { return "you"; }
    }

    private static async Task<(bool ok, string log)> RunProcessAsync(string exe, string args, string workDir)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            });
            if (p == null) return (false, $"Could not start '{exe}'.");
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var log = (stdout + "\n" + stderr).Trim();
            if (log.Length > 1200) log = "…" + log[^1200..];
            return (p.ExitCode == 0, log);
        }
        catch (Exception ex) { return (false, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    private static async Task<bool> RepoExists(string repoName)
    {
        var (ok, _) = await RunProcessAsync(ResolveGhPath(), $"repo view \"{repoName}\"", AppContext.BaseDirectory);
        return ok;
    }

    private async Task<GitHubSpec?> PromptGitHubSpec(string suggestedName, string suggestedDesc, bool isUpdate)
    {
        var tcs = new TaskCompletionSource<GitHubSpec?>();

        var nameBox = new TextBox { Width = 340, Text = suggestedName, IsReadOnly = isUpdate };
        var descBox = new TextBox { Width = 340, Text = suggestedDesc };
        var publicCheck = new CheckBox { Content = "Public repository", IsChecked = true, Foreground = Brushes.White, IsVisible = !isUpdate };

        var okBtn = new Button { Content = isUpdate ? "Commit & Push" : "Create & Push", IsDefault = true, Padding = new Thickness(16, 4) };
        var cancelBtn = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 4), Margin = new Thickness(8, 0, 0, 0) };

        var w = new Window
        {
            Title = isUpdate ? "Update GitHub Repository" : "Create GitHub Repository",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Repository name", Foreground = Brushes.White },
                    nameBox,
                    new TextBlock { Text = isUpdate ? "Commit message" : "Description", Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 0) },
                    descBox,
                    publicCheck,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 12, 0, 0),
                        Children = { okBtn, cancelBtn },
                    },
                },
            },
        };
        StyleDialog(w);

        okBtn.Click += (_, _) =>
        {
            var name = nameBox.Text?.Trim() ?? "";
            if (name.Length == 0) return;
            tcs.TrySetResult(new GitHubSpec(name, descBox.Text?.Trim() ?? "", publicCheck.IsChecked == true));
            w.Close();
        };
        cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); w.Close(); };
        w.Closed += (_, _) => tcs.TrySetResult(null);
        nameBox.AttachedToVisualTree += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };

        await w.ShowDialog(this);
        return await tcs.Task;
    }
}
