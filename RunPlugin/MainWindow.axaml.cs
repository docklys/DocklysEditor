using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Docklys.ModuleContracts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace RunPlugin;

public partial class MainWindow : Window
{
    private sealed record PluginEntry(
        string FolderName,
        string CsprojPath,
        string DllPath,
        Type PluginType,
        PluginLoadContext LoadContext);

    private readonly List<PluginEntry> _catalog = new();
    private readonly List<Control> _footerCommands = new();
    private Grid? _footerRows;
    private int _currentIndex;

    // Names of the bundled starter templates shown in the "Template" combo.
    private static readonly string[] TemplateNames =
        { "Greeter (text + toggles)", "Counter (buttons + slider)" };

    public MainWindow()
    {
        InitializeComponent();
        MinWidth = 360;
        MinHeight = 230;
        ArrangeCommandBar();
        SizeChanged += (_, _) => ReflowFooter();
        
        LoadDocklysColors();

        Loaded += (_, _) =>
        {
            LoadCatalog();
            ShowPluginAtIndex(_currentIndex);
        };
    }

    private void ArrangeCommandBar()
    {
        if (Content is not Grid root) return;

        var headerBar = root.Children.OfType<Border>().FirstOrDefault();
        MakeHorizontallyScrollable(headerBar);

        var commandBar = root.Children.OfType<Border>().LastOrDefault();
        if (commandBar == null) return;

        PrepareResponsiveFooter(commandBar);
    }

    private void PrepareResponsiveFooter(Border commandBar)
    {
        if (commandBar.Child is not Panel layout) return;

        CollectCommands(layout, _footerCommands);
        if (_footerCommands.Count == 0) return;

        _footerRows = new Grid { RowSpacing = 6 };
        commandBar.Child = new ScrollViewer
        {
            Content = _footerRows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        ReflowFooter();
    }

    private void ReflowFooter()
    {
        if (_footerRows == null || _footerCommands.Count == 0) return;

        var availableWidth = Math.Max(Bounds.Width - 32, 180);
        var requiredWidth = 0d;
        foreach (var command in _footerCommands)
        {
            command.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            requiredWidth += Math.Max(command.DesiredSize.Width, 80) + 6;
        }

        var rowCount = Math.Clamp((int)Math.Ceiling(requiredWidth / availableWidth), 1, 4);
        var commandsPerRow = (int)Math.Ceiling(_footerCommands.Count / (double)rowCount);
        foreach (var existingRow in _footerRows.Children.OfType<Panel>().ToList())
            existingRow.Children.Clear();
        _footerRows.Children.Clear();
        _footerRows.RowDefinitions = new RowDefinitions(string.Join(',', Enumerable.Repeat("Auto", rowCount)));

        for (var row = 0; row < rowCount; row++)
        {
            var rowCommands = _footerCommands.Skip(row * commandsPerRow).Take(commandsPerRow).ToList();
            var rowPanel = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat("*", rowCommands.Count)))
            };
            Grid.SetRow(rowPanel, row);
            _footerRows.Children.Add(rowPanel);
            for (var column = 0; column < rowCommands.Count; column++)
            {
                var command = rowCommands[column];
                Grid.SetColumn(command, column);
                command.HorizontalAlignment = HorizontalAlignment.Stretch;
                rowPanel.Children.Add(command);
            }
        }
    }

    private static void CollectCommands(Panel panel, List<Control> commands)
    {
        foreach (var child in panel.Children.ToList())
        {
            panel.Children.Remove(child);
            if (child is Panel nested)
                CollectCommands(nested, commands);
            else
                commands.Add(child);
        }
    }

    private static void MakeHorizontallyScrollable(Border? bar)
    {
        if (bar == null || bar.Child is ScrollViewer) return;

        var content = bar.Child;
        bar.Child = null;
        bar.Child = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }
    
    private static void LoadDocklysColors()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Docklys", "colors.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                
                var app = Application.Current;
                if (app?.Resources == null) return;
                
                void Map(string key)
                {
                    if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                    {
                        var hex = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var c))
                        {
                            app.Resources[key] = new SolidColorBrush(c);
                            // Also map the exact keys SettingsWindow uses
                            if (key.StartsWith("Color") && !key.StartsWith("ColorColor"))
                            {
                                app.Resources["Color" + key] = c;
                            }
                        }
                    }
                }
                
                Map("ColorBackground");
                Map("Color2Background");
                Map("Color3Background");
                Map("ColorModuleBackground");
                Map("ColorModuleBorder");
                Map("ColorFont");
                Map("ColorAccent");
                Map("ColorWindowBorder");
                Map("ColorModuleAccentColor");
                Map("ColorModuleColor");
                Map("ColorModuleFont");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load docklys colors: {ex.Message}");
        }
    }

    // ── Catalog ───────────────────────────────────────────────────────────────

    private void LoadCatalog()
    {
        foreach (var e in _catalog) TryUnload(e.LoadContext);
        _catalog.Clear();

        var pluginsDir = GetPluginsSourceDir();
        if (pluginsDir == null || !Directory.Exists(pluginsDir))
            return;

        foreach (var folder in Directory.EnumerateDirectories(pluginsDir))
        {
            var folderName = Path.GetFileName(folder);
            var csproj = Path.Combine(folder, folderName + ".csproj");
            if (!File.Exists(csproj)) continue;

            var dll = FindFreshestDll(folder, folderName);
            if (dll == null) continue;

            PluginLoadContext? ctx = null;
            try
            {
                ctx = new PluginLoadContext(dll);
                var asm = ctx.LoadPlugin();
                var type = SafeGetTypes(asm).FirstOrDefault(IsPluginType);
                if (type != null)
                {
                    _catalog.Add(new PluginEntry(folderName, csproj, dll, type, ctx));
                    ctx = null; // ownership transferred
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Catalog] {dll}: {ex.Message}");
            }
            finally { TryUnload(ctx); }
        }

        _catalog.Sort((a, b) => string.Compare(a.FolderName, b.FolderName, StringComparison.OrdinalIgnoreCase));
        if (_currentIndex >= _catalog.Count) _currentIndex = 0;
    }

    private void ShowPluginAtIndex(int index)
    {
        var slot = this.FindControl<ContentControl>("PreviewSlot")!;
        var label = this.FindControl<TextBlock>("PluginNameLabel")!;

        if (_catalog.Count == 0)
        {
            slot.Content = null;
            label.Text = "(no plugins — click New)";
            return;
        }

        index = Math.Clamp(index, 0, _catalog.Count - 1);
        _currentIndex = index;
        var entry = _catalog[index];

        try
        {
            var plugin = (IPlugin)Activator.CreateInstance(entry.PluginType)!;
            var ctx = BuildContext(plugin.UniquePluginId);
            var view = plugin.CreateSettingsView(ctx);

            slot.Content = new ScrollViewer
            {
                Content = new Border
                {
                    Padding = new Thickness(16, 32),
                    Child = BuildPreviewCard(plugin, view),
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            label.Text = $"{plugin.PluginName}   ({entry.FolderName})";
            RefreshMetaForCurrentPlugin();
        }
        catch (Exception ex)
        {
            slot.Content = null;
            label.Text = entry.FolderName + " (failed)";
            _ = ShowMessageDialog("Plugin load failed",
                $"'{entry.FolderName}' could not be loaded:\n\n{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static PluginContext BuildContext(string pluginId)
    {
        var res = Application.Current?.Resources;
        Color C(string key, Color fb)
        {
            if (res != null && res.TryGetValue(key, out var v))
            {
                if (v is Color c) return c;
                if (v is SolidColorBrush b) return b.Color;
            }
            return fb;
        }

        var bag = PreviewSettingsBag.For(pluginId);
        return new PluginContext
        {
            Color1 = C("ColorColorBackground", Color.Parse("#2A2D31")),
            Color2 = C("ColorColor2Background", Color.Parse("#1A1C1F")),
            Color3 = C("ColorColor3Background", Colors.White),
            Accent = C("ColorAccent", Color.Parse("#E7EAED")),
            Font = C("ColorFont", Color.Parse("#EDEDED")),
            GetSetting = bag.Get,
            SetSetting = bag.Set,
        };
    }

    private static IBrush ResolveBrush(string key, IBrush fallback)
    {
        var res = Application.Current?.Resources;
        if (res != null && res.TryGetValue(key, out var v))
        {
            if (v is IBrush b) return b;
            if (v is Color c) return new SolidColorBrush(c);
        }
        return fallback;
    }

    // Wraps the plugin's settings view in the same card the app renders in
    // Settings ▸ Plugins: outer border → title row → description → divider → view.
    private static Control BuildPreviewCard(IPlugin plugin, Control view)
    {
        var font = ResolveBrush("ColorFont", Brushes.White);
        var accent = ResolveBrush("ColorAccent", new SolidColorBrush(Color.Parse("#E7EAED")));
        var bg = ResolveBrush("ColorModuleBackground", Brushes.DimGray);
        var border = ResolveBrush("ColorModuleBorder", Brushes.Gray);

        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleRow.Children.Add(new TextBlock
        {
            Text = plugin.PluginName,
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = font,
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = "v" + plugin.PluginVersion,
            FontSize = 11,
            Opacity = 0.7,
            Foreground = font,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2),
        });
        titleRow.Children.Add(new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "installed", FontSize = 10, Foreground = Brushes.White },
        });

        var inner = new StackPanel { Spacing = 8 };
        inner.Children.Add(titleRow);

        var desc = plugin.PluginDescription;
        if (!string.IsNullOrWhiteSpace(desc))
            inner.Children.Add(new TextBlock
            {
                Text = desc,
                FontSize = 12,
                Opacity = 0.8,
                Foreground = font,
                TextWrapping = TextWrapping.Wrap,
            });

        inner.Children.Add(new Avalonia.Controls.Shapes.Rectangle
        {
            Height = 1,
            Fill = accent,
            Margin = new Thickness(0, 2, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });

        inner.Children.Add(view);

        return new Border
        {
            Background = bg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Width = 210,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = inner,
        };
    }

    // ── Carousel buttons ──────────────────────────────────────────────────────

    private void OnPrevClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) return;
        ShowPluginAtIndex((_currentIndex - 1 + _catalog.Count) % _catalog.Count);
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) return;
        ShowPluginAtIndex((_currentIndex + 1) % _catalog.Count);
    }

    private void OnReloadClick(object? sender, RoutedEventArgs e)
    {
        LoadCatalog();
        ShowPluginAtIndex(_currentIndex);
    }

    // ── New plugin (scaffold from template) ─────────────────────────────────────

    private async void OnNewClick(object? sender, RoutedEventArgs e)
    {
        var pluginsDir = GetPluginsSourceDir();
        if (pluginsDir == null)
        {
            await ShowMessageDialog("New Plugin", "Couldn't locate the Plugins source folder.");
            return;
        }

        // Dialog: pick a template + name (mirrors RunPattern's create flow).
        var spec = await PromptForNewPluginSpec(pluginsDir);
        if (spec == null) return;
        var name = spec.Name;
        var templateIdx = spec.TemplateIndex;

        var folder = Path.Combine(pluginsDir, name);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, name + ".csproj"), CsprojTemplate);
            File.WriteAllText(Path.Combine(folder, name + "Plugin.cs"), BuildSource(templateIdx, name));

            var (ok, log) = await RunDotnetAsync("build", Path.Combine(folder, name + ".csproj"));
            if (!ok)
            {
                await ShowMessageDialog("Build failed",
                    $"Plugin '{name}' was created at:\n{folder}\n\nbut the build failed:\n\n{log}");
                return;
            }

            LoadCatalog();
            var idx = _catalog.FindIndex(c => string.Equals(c.FolderName, name, StringComparison.OrdinalIgnoreCase));
            ShowPluginAtIndex(idx >= 0 ? idx : _currentIndex);
            await ShowMessageDialog("Plugin created",
                $"Plugin '{name}' created and built at:\n{folder}\n\nTune the code, then Push to Docklys.");
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(folder);
            await ShowMessageDialog("New plugin failed", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Push to Docklys ─────────────────────────────────────────────────────────

    private async void OnDeployClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) { await ShowMessageDialog("Push to Docklys", "No plugin selected."); return; }
        var entry = _catalog[Math.Clamp(_currentIndex, 0, _catalog.Count - 1)];
        var button = sender as Button;
        if (button != null) button.IsEnabled = false;

        try
        {
            var (ok, log) = await RunDotnetAsync("build", entry.CsprojPath);
            if (!ok) { await ShowMessageDialog("Build failed", $"'{entry.FolderName}' failed to build:\n\n{log}"); return; }

            var dll = FindFreshestDll(Path.GetDirectoryName(entry.CsprojPath)!, entry.FolderName);
            if (dll == null || !File.Exists(dll)) { await ShowMessageDialog("Push to Docklys", "Built, but no DLL found to copy."); return; }

            var targetDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Docklys", "Plugin");
            Directory.CreateDirectory(targetDir);
            var dest = Path.Combine(targetDir, entry.FolderName + ".dll");

            try
            {
                File.Copy(dll, dest, overwrite: true);
            }
            catch (IOException)
            {
                await ShowMessageDialog("Push to Docklys",
                    $"Couldn't copy to:\n{dest}\n\nClose Docklys (it may be holding the DLL) and try again.");
                return;
            }

            await ShowMessageDialog("Pushed to Docklys",
                $"✓ Pushed '{entry.FolderName}' to:\n{dest}\n\nOpen Docklys → Settings → Plugins to see it.");
        }
        catch (Exception ex)
        {
            await ShowMessageDialog("Deploy failed", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (button != null) button.IsEnabled = true;
        }
    }

    // ── Folder buttons ──────────────────────────────────────────────────────────

    private void OnOpenPluginFolderClick(object? sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Docklys", "Plugin");
        Directory.CreateDirectory(dir);
        OpenFolder(dir);
    }

    private void OpenLegalLink_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Legal] Failed to open link {url}: {ex.Message}");
            }
        }
    }

    private async void OnOpenOutputFolderClick(object sender, RoutedEventArgs e)
    {
        var root = FindEditorSolutionDir();
        if (root == null) { _ = ShowMessageDialog("Open folder", "Editor solution root not found."); return; }
        var dir = Path.Combine(root, "OutputPluginDLL");
        Directory.CreateDirectory(dir);
        OpenFolder(dir);
    }

    private void OpenFolder(string dir)
    {
        try { Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true }); }
        catch (Exception ex) { _ = ShowMessageDialog("Open folder", $"Couldn't open {dir}:\n\n{ex.Message}"); }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { Debug.WriteLine($"[RunPlugin] rollback delete failed: {ex.Message}"); }
    }

    private static bool IsPluginType(Type t) =>
        !t.IsAbstract && !t.IsInterface
        && t.GetInterfaces().Any(i => i.Name == nameof(IPlugin))
        && t.GetConstructor(Type.EmptyTypes) != null;

    private static IEnumerable<Type> SafeGetTypes(System.Reflection.Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
    }

    private static void TryUnload(PluginLoadContext? ctx)
    {
        try { ctx?.Unload(); } catch { }
    }

    private static string? FindFreshestDll(string projectFolder, string assemblyName)
    {
        var candidates = new List<string>();

        var root = FindEditorSolutionDirFrom(projectFolder);
        if (root != null)
        {
            var outDir = Path.Combine(root, "OutputPluginDLL");
            if (Directory.Exists(outDir))
                candidates.AddRange(Directory.GetFiles(outDir, assemblyName + ".dll", SearchOption.AllDirectories));
        }

        var bin = Path.Combine(projectFolder, "bin");
        if (Directory.Exists(bin))
            candidates.AddRange(Directory.GetFiles(bin, assemblyName + ".dll", SearchOption.AllDirectories));

        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private static string? GetPluginsSourceDir()
    {
        var root = FindEditorSolutionDir();
        if (root == null) return null;
        var dir = Path.Combine(root, "Plugins");
        return dir;
    }

    private static string? FindEditorSolutionDir() => FindEditorSolutionDirFrom(AppContext.BaseDirectory);

    private static string? FindEditorSolutionDirFrom(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DefaultModule.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static async Task<(bool ok, string log)> RunDotnetAsync(string verb, string csprojPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"{verb} \"{csprojPath}\" -c Debug",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, "Could not launch dotnet.");
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var log = (stdout + "\n" + stderr).Trim();
            // Keep the tail — the useful errors are at the end.
            if (log.Length > 1200) log = "…" + log[^1200..];
            return (p.ExitCode == 0, log);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Embedded scaffolding templates ───────────────────────────────────────────

    private const string CsprojTemplate =
@"<Project Sdk=""Microsoft.NET.Sdk"">
    <PropertyGroup>
        <TargetFramework>net9.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
    </PropertyGroup>
    <ItemGroup>
        <PackageReference Include=""Avalonia"" Version=""11.3.1"" />
    </ItemGroup>
    <ItemGroup>
        <ProjectReference Include=""..\..\Docklys.ModuleContracts\Docklys.ModuleContracts.csproj"" />
    </ItemGroup>
    <PropertyGroup>
        <OutputPluginDir Condition="" '$(SolutionDir)' == '' "">$(MSBuildProjectDirectory)\..\..\OutputPluginDLL</OutputPluginDir>
        <OutputPluginDir Condition="" '$(SolutionDir)' != '' "">$(SolutionDir)OutputPluginDLL</OutputPluginDir>
    </PropertyGroup>
    <Target Name=""CopyPluginDllAfterBuild"" AfterTargets=""Build"">
        <MakeDir Directories=""$(OutputPluginDir)"" Condition=""!Exists('$(OutputPluginDir)')"" />
        <Copy SourceFiles=""$(OutputPath)$(AssemblyName).dll"" DestinationFolder=""$(OutputPluginDir)"" />
    </Target>
</Project>
";

    private static string BuildSource(int templateIdx, string name)
    {
        var id = "user.plugin." + name.ToLowerInvariant();
        return templateIdx switch
        {
            1 => CounterSource(name, id),
            _ => GreeterSource(name, id),
        };
    }

    private static string GreeterSource(string n, string id) => $@"using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Docklys.ModuleContracts;

namespace DocklysPlugins;

// Greeter template: a greeting card with a few persisted settings and a live
// preview. Each control writes through ctx.SetSetting, so values survive a
// 'Push to Docklys' and reload in the app's Settings ▸ Plugins page.
public sealed class {n}Plugin : IPlugin
{{
    public string PluginName => ""{n}"";
    public string PluginVersion => ""1.0"";
    public string UniquePluginId {{ get; private set; }} = ""{id}"";
    public string PluginDescription => ""A greeting with live, persisted settings."";
    public void SetPluginId(string uniquePluginId) => UniquePluginId = uniquePluginId;
    public Control CreateSettingsView(PluginContext ctx) => new {n}View(ctx);
}}

internal sealed class {n}View : UserControl
{{
    private readonly PluginContext _ctx;
    private readonly TextBlock _preview = new();

    private const string KeyGreeting = ""greeting"";
    private const string KeyName = ""name"";
    private const string KeySize = ""size"";
    private const string KeyUpper = ""upper"";

    public {n}View(PluginContext ctx)
    {{
        _ctx = ctx;
        var text = new SolidColorBrush(ctx.Font);
        var accent = new SolidColorBrush(ctx.Accent);

        var greeting = new TextBox {{ Text = ctx.GetSetting(KeyGreeting) ?? ""Hello"", Watermark = ""Greeting"", Width = 240, HorizontalAlignment = HorizontalAlignment.Left }};
        greeting.TextChanged += (_, _) => {{ _ctx.SetSetting(KeyGreeting, greeting.Text); Update(); }};

        var name = new TextBox {{ Text = ctx.GetSetting(KeyName) ?? ""world"", Watermark = ""Name"", Width = 240, HorizontalAlignment = HorizontalAlignment.Left }};
        name.TextChanged += (_, _) => {{ _ctx.SetSetting(KeyName, name.Text); Update(); }};

        var size = new Slider {{ Minimum = 12, Maximum = 48, Value = ParseDouble(ctx.GetSetting(KeySize), 22), Width = 240, HorizontalAlignment = HorizontalAlignment.Left, Foreground = accent }};
        size.PropertyChanged += (_, e) =>
        {{
            if (e.Property != Slider.ValueProperty) return;
            _ctx.SetSetting(KeySize, size.Value.ToString(""F0"", CultureInfo.InvariantCulture));
            Update();
        }};

        var upper = new CheckBox {{ Content = ""UPPERCASE"", IsChecked = ParseBool(ctx.GetSetting(KeyUpper)), Foreground = text }};
        upper.IsCheckedChanged += (_, _) => {{ _ctx.SetSetting(KeyUpper, upper.IsChecked == true ? ""true"" : ""false""); Update(); }};

        _preview.Foreground = accent;
        _preview.TextWrapping = TextWrapping.Wrap;

        Content = new StackPanel
        {{
            Spacing = 8,
            Children =
            {{
                Label(""Greeting"", text), greeting,
                Label(""Name"", text), name,
                Label(""Font size"", text), size,
                upper,
                Label(""Preview"", text),
                new Border {{ Background = new SolidColorBrush(ctx.Color2), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Child = _preview }},
            }},
        }};
        Update();
    }}

    private void Update()
    {{
        var greeting = _ctx.GetSetting(KeyGreeting) ?? ""Hello"";
        var name = _ctx.GetSetting(KeyName) ?? ""world"";
        var s = $""{{greeting}}, {{name}}!"";
        if (ParseBool(_ctx.GetSetting(KeyUpper))) s = s.ToUpperInvariant();
        _preview.Text = s;
        _preview.FontSize = ParseDouble(_ctx.GetSetting(KeySize), 22);
    }}

    private static TextBlock Label(string s, IBrush b) => new() {{ Text = s, Foreground = b, FontSize = 12, Opacity = 0.85 }};
    private static double ParseDouble(string? s, double fb) => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fb;
    private static bool ParseBool(string? s) => string.Equals(s, ""true"", StringComparison.OrdinalIgnoreCase);
}}
";

    private static string CounterSource(string n, string id) => $@"using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Docklys.ModuleContracts;

namespace DocklysPlugins;

// Counter template: a persisted integer with +/- buttons and an adjustable step.
// Demonstrates buttons + a slider writing through ctx.SetSetting.
public sealed class {n}Plugin : IPlugin
{{
    public string PluginName => ""{n}"";
    public string PluginVersion => ""1.0"";
    public string UniquePluginId {{ get; private set; }} = ""{id}"";
    public string PluginDescription => ""A persisted counter with an adjustable step."";
    public void SetPluginId(string uniquePluginId) => UniquePluginId = uniquePluginId;
    public Control CreateSettingsView(PluginContext ctx) => new {n}View(ctx);
}}

internal sealed class {n}View : UserControl
{{
    private readonly PluginContext _ctx;
    private readonly TextBlock _value = new();
    private int _count;
    private int _step;

    private const string KeyValue = ""value"";
    private const string KeyStep = ""step"";

    public {n}View(PluginContext ctx)
    {{
        _ctx = ctx;
        _count = ParseInt(ctx.GetSetting(KeyValue), 0);
        _step = Math.Max(1, ParseInt(ctx.GetSetting(KeyStep), 1));

        var text = new SolidColorBrush(ctx.Font);
        var accent = new SolidColorBrush(ctx.Accent);

        _value.Foreground = accent;
        _value.FontSize = 32;
        _value.FontWeight = FontWeight.Bold;

        var minus = new Button {{ Content = ""−"", Width = 44, FontSize = 18 }};
        minus.Click += (_, _) => {{ _count -= _step; Persist(); }};
        var plus = new Button {{ Content = ""+"", Width = 44, FontSize = 18 }};
        plus.Click += (_, _) => {{ _count += _step; Persist(); }};
        var reset = new Button {{ Content = ""Reset"" }};
        reset.Click += (_, _) => {{ _count = 0; Persist(); }};

        var step = new Slider {{ Minimum = 1, Maximum = 10, Value = _step, Width = 240, HorizontalAlignment = HorizontalAlignment.Left, Foreground = accent }};
        step.PropertyChanged += (_, e) =>
        {{
            if (e.Property != Slider.ValueProperty) return;
            _step = Math.Max(1, (int)Math.Round(step.Value));
            _ctx.SetSetting(KeyStep, _step.ToString(CultureInfo.InvariantCulture));
        }};

        Content = new StackPanel
        {{
            Spacing = 10,
            Children =
            {{
                _value,
                new StackPanel {{ Orientation = Orientation.Horizontal, Spacing = 8, Children = {{ minus, plus, reset }} }},
                new TextBlock {{ Text = ""Step"", Foreground = text, FontSize = 12, Opacity = 0.85 }},
                step,
            }},
        }};
        Render();
    }}

    private void Persist()
    {{
        _ctx.SetSetting(KeyValue, _count.ToString(CultureInfo.InvariantCulture));
        Render();
    }}

    private void Render() => _value.Text = _count.ToString(CultureInfo.InvariantCulture);

    private static int ParseInt(string? s, int fb) => int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fb;
}}
";
}
