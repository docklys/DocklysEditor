using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Docklys.ModuleContracts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RunPattern;

public partial class MainWindow : Window
{
    private sealed record PatternEntry(
        string FolderName,
        string CsprojPath,
        string DllPath,
        Type PatternType,
        PatternLoadContext LoadContext);

    private readonly List<PatternEntry> _catalog = new();
    private int _currentIndex;
    private IPatternInteraction? _interaction;

    // Names of the bundled starter templates shown in the "Template" combo.
    private static readonly string[] TemplateNames =
        { "Reactive dots (purple hover)", "Reactive hex mesh (purple hover)", "Reactive grid (purple hover)" };

    public MainWindow()
    {
        InitializeComponent();

        var preview = this.FindControl<Border>("PreviewBorder")!;
        preview.PointerMoved += OnPreviewPointerMoved;
        preview.PointerExited += OnPreviewPointerExited;

        Loaded += (_, _) =>
        {
            LoadCatalog();
            ShowPatternAtIndex(_currentIndex);
        };
    }

    // ── Catalog ───────────────────────────────────────────────────────────────

    private void LoadCatalog()
    {
        foreach (var e in _catalog) TryUnload(e.LoadContext);
        _catalog.Clear();

        var patternsDir = GetPatternsSourceDir();
        if (patternsDir == null || !Directory.Exists(patternsDir))
            return;

        foreach (var folder in Directory.EnumerateDirectories(patternsDir))
        {
            var folderName = Path.GetFileName(folder);
            var csproj = Path.Combine(folder, folderName + ".csproj");
            if (!File.Exists(csproj)) continue;

            var dll = FindFreshestDll(folder, folderName);
            if (dll == null) continue;

            PatternLoadContext? ctx = null;
            try
            {
                ctx = new PatternLoadContext(dll);
                var asm = ctx.LoadPattern();
                var type = SafeGetTypes(asm).FirstOrDefault(IsPatternType);
                if (type != null)
                {
                    _catalog.Add(new PatternEntry(folderName, csproj, dll, type, ctx));
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

    private void ShowPatternAtIndex(int index)
    {
        var slot = this.FindControl<ContentControl>("PreviewSlot")!;
        var label = this.FindControl<TextBlock>("PatternNameLabel")!;
        _interaction = null;

        if (_catalog.Count == 0)
        {
            slot.Content = null;
            label.Text = "(no patterns — click New)";
            return;
        }

        index = Math.Clamp(index, 0, _catalog.Count - 1);
        _currentIndex = index;
        var entry = _catalog[index];

        try
        {
            var pattern = (IPattern)Activator.CreateInstance(entry.PatternType)!;
            var view = pattern.CreateView(BuildContext());
            _interaction = view as IPatternInteraction ?? pattern as IPatternInteraction;
            slot.Content = view;
            label.Text = $"{pattern.PatternName}   ({entry.FolderName})";
            RefreshMetaForCurrentPattern();
        }
        catch (Exception ex)
        {
            slot.Content = null;
            label.Text = entry.FolderName + " (failed)";
            _ = ShowMessageDialog("Pattern load failed",
                $"'{entry.FolderName}' could not be loaded:\n\n{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static PatternContext BuildContext()
    {
        var res = Application.Current?.Resources;
        Color C(string key, Color fb)
            => res != null && res.TryGetValue(key, out var v) && v is Color c ? c : fb;

        return new PatternContext
        {
            Color1 = C("ColorColorBackground", Color.Parse("#2A2D31")),
            Color2 = C("ColorColor2Background", Color.Parse("#1A1C1F")),
            Color3 = C("ColorColor3Background", Colors.White),
            Accent = C("ColorAccent", Color.Parse("#4F9CF9")),
            Density = 0.03
        };
    }

    // ── Pointer forwarding ──────────────────────────────────────────────────

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_interaction == null) return;
        var border = (Border)sender!;
        var b = border.Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;
        var p = e.GetPosition(border);
        _interaction.OnPointerMoved(p.X / b.Width, p.Y / b.Height);
    }

    private void OnPreviewPointerExited(object? sender, PointerEventArgs e)
        => _interaction?.OnPointerMoved(null, null);

    // ── Carousel buttons ──────────────────────────────────────────────────────

    private void OnPrevClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) return;
        ShowPatternAtIndex((_currentIndex - 1 + _catalog.Count) % _catalog.Count);
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) return;
        ShowPatternAtIndex((_currentIndex + 1) % _catalog.Count);
    }

    private void OnReloadClick(object? sender, RoutedEventArgs e)
    {
        LoadCatalog();
        ShowPatternAtIndex(_currentIndex);
    }

    // ── New pattern (scaffold from template) ───────────────────────────────────

    private async void OnNewClick(object? sender, RoutedEventArgs e)
    {
        var patternsDir = GetPatternsSourceDir();
        if (patternsDir == null)
        {
            await ShowMessageDialog("New Pattern", "Couldn't locate the Patterns source folder.");
            return;
        }

        // Dialog: pick a template + name (mirrors RunModule's create flow).
        var spec = await PromptForNewPatternSpec(patternsDir);
        if (spec == null) return;
        var name = spec.Name;
        var templateIdx = spec.TemplateIndex;

        var folder = Path.Combine(patternsDir, name);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, name + ".csproj"), CsprojTemplate);
            File.WriteAllText(Path.Combine(folder, name + "Pattern.cs"), BuildSource(templateIdx, name));

            var (ok, log) = await RunDotnetAsync("build", Path.Combine(folder, name + ".csproj"));
            if (!ok)
            {
                await ShowMessageDialog("Build failed",
                    $"Pattern '{name}' was created at:\n{folder}\n\nbut the build failed:\n\n{log}");
                return;
            }

            LoadCatalog();
            var idx = _catalog.FindIndex(c => string.Equals(c.FolderName, name, StringComparison.OrdinalIgnoreCase));
            ShowPatternAtIndex(idx >= 0 ? idx : _currentIndex);
            await ShowMessageDialog("Pattern created",
                $"Pattern '{name}' created and built at:\n{folder}\n\nTune the code, then Push to Docklys.");
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(folder);
            await ShowMessageDialog("New pattern failed", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Push to Docklys ─────────────────────────────────────────────────────────

    private async void OnDeployClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) { await ShowMessageDialog("Push to Docklys", "No pattern selected."); return; }
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
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Docklys", "Pattern");
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
                $"✓ Pushed '{entry.FolderName}' to:\n{dest}\n\nOpen Docklys → Settings to select it per window.");
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

    private void OnOpenPatternFolderClick(object? sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Docklys", "Pattern");
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
        var dir = Path.Combine(root, "OutputPatternDLL");
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
        catch (Exception ex) { Debug.WriteLine($"[RunPattern] rollback delete failed: {ex.Message}"); }
    }

    private static bool IsPatternType(Type t) =>
        !t.IsAbstract && !t.IsInterface
        && t.GetInterfaces().Any(i => i.Name == nameof(IPattern))
        && t.GetConstructor(Type.EmptyTypes) != null;

    private static IEnumerable<Type> SafeGetTypes(System.Reflection.Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
    }

    private static void TryUnload(PatternLoadContext? ctx)
    {
        try { ctx?.Unload(); } catch { }
    }

    private static string? FindFreshestDll(string projectFolder, string assemblyName)
    {
        var candidates = new List<string>();

        var root = FindEditorSolutionDirFrom(projectFolder);
        if (root != null)
        {
            var outDir = Path.Combine(root, "OutputPatternDLL");
            if (Directory.Exists(outDir))
                candidates.AddRange(Directory.GetFiles(outDir, assemblyName + ".dll", SearchOption.AllDirectories));
        }

        var bin = Path.Combine(projectFolder, "bin");
        if (Directory.Exists(bin))
            candidates.AddRange(Directory.GetFiles(bin, assemblyName + ".dll", SearchOption.AllDirectories));

        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private static string? GetPatternsSourceDir()
    {
        var root = FindEditorSolutionDir();
        if (root == null) return null;
        var dir = Path.Combine(root, "Patterns");
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
        <OutputPatternDir Condition="" '$(SolutionDir)' == '' "">$(MSBuildProjectDirectory)\..\..\OutputPatternDLL</OutputPatternDir>
        <OutputPatternDir Condition="" '$(SolutionDir)' != '' "">$(SolutionDir)OutputPatternDLL</OutputPatternDir>
    </PropertyGroup>
    <Target Name=""CopyPatternDllAfterBuild"" AfterTargets=""Build"">
        <MakeDir Directories=""$(OutputPatternDir)"" Condition=""!Exists('$(OutputPatternDir)')"" />
        <Copy SourceFiles=""$(OutputPath)$(AssemblyName).dll"" DestinationFolder=""$(OutputPatternDir)"" />
    </Target>
</Project>
";

    private static string BuildSource(int templateIdx, string name)
    {
        var id = "user." + name.ToLowerInvariant();
        return templateIdx switch
        {
            1 => HexSource(name, id),
            2 => GridSource(name, id),
            _ => ReactiveDotsSource(name, id),
        };
    }

    private static string ReactiveDotsSource(string n, string id) => $@"using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Docklys.ModuleContracts;

namespace DocklysPatterns;

// Reactive template: a grid of dots that light up purple as the cursor passes
// near them, blending from the skin's ink color and growing by proximity.
public sealed class {n}Pattern : IPattern
{{
    public string PatternName => ""{n}"";
    public string PatternVersion => ""1.0"";
    public string UniquePatternId {{ get; private set; }} = ""{id}"";
    public void SetPatternId(string uniquePatternId) => UniquePatternId = uniquePatternId;
    public Control CreateView(PatternContext ctx) => new {n}View(ctx);
}}

internal sealed class {n}View : Control, IPatternInteraction
{{
    private readonly Color _c1, _c2, _ink;
    private static readonly Color Purple = Color.FromRgb(0xB0, 0x61, 0xFF);
    private Point? _pointer;

    private const double Spacing = 26;
    private const double BaseRadius = 2;
    private const double Reach = 110;

    public {n}View(PatternContext ctx)
    {{
        _c1 = ctx.Color1; _c2 = ctx.Color2; _ink = ctx.Color3;
        ClipToBounds = true;
    }}

    public void OnPointerMoved(double? x, double? y)
    {{
        _pointer = (x is {{ }} px && y is {{ }} py) ? new Point(px, py) : null;
        InvalidateVisual();
    }}

    public override void Render(DrawingContext context)
    {{
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var bg = new LinearGradientBrush
        {{
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative)
        }};
        bg.GradientStops.Add(new GradientStop(_c1, 0));
        bg.GradientStops.Add(new GradientStop(_c2, 1));
        context.FillRectangle(bg, rect);

        Point? cursor = _pointer is {{ }} p ? new Point(p.X * rect.Width, p.Y * rect.Height) : null;

        for (double y = Spacing / 2; y < rect.Height; y += Spacing)
            for (double x = Spacing / 2; x < rect.Width; x += Spacing)
            {{
                double glow = 0;
                if (cursor is {{ }} c)
                {{
                    double dx = x - c.X, dy = y - c.Y;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    glow = Math.Clamp(1 - dist / Reach, 0, 1);
                    glow *= glow;
                }}

                var color = Lerp(_ink, Purple, glow);
                byte alpha = (byte)(110 + 145 * glow);
                var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
                double radius = BaseRadius + glow * 4.5;
                context.DrawEllipse(brush, null, new Point(x, y), radius, radius);
            }}
    }}

    private static Color Lerp(Color a, Color b, double t)
    {{
        byte L(byte from, byte to) => (byte)(from + (to - from) * t);
        return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }}
}}
";

    private static string HexSource(string n, string id) => $@"using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Docklys.ModuleContracts;

namespace DocklysPatterns;

// Reactive template: a honeycomb of hexagon outlines that light up purple near
// the cursor — stroke brightens and thickens by proximity. No trails.
public sealed class {n}Pattern : IPattern
{{
    public string PatternName => ""{n}"";
    public string PatternVersion => ""1.0"";
    public string UniquePatternId {{ get; private set; }} = ""{id}"";
    public void SetPatternId(string uniquePatternId) => UniquePatternId = uniquePatternId;
    public Control CreateView(PatternContext ctx) => new {n}View(ctx);
}}

internal sealed class {n}View : Control, IPatternInteraction
{{
    private readonly Color _c1, _c2, _ink;
    private static readonly Color Purple = Color.FromRgb(0xB0, 0x61, 0xFF);
    private Point? _pointer;
    private const double R = 22, Reach = 150;

    public {n}View(PatternContext ctx)
    {{
        _c1 = ctx.Color1; _c2 = ctx.Color2; _ink = ctx.Color3;
        ClipToBounds = true;
    }}

    public void OnPointerMoved(double? x, double? y)
    {{
        _pointer = (x is {{ }} px && y is {{ }} py) ? new Point(px, py) : null;
        InvalidateVisual();
    }}

    public override void Render(DrawingContext context)
    {{
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var bg = new LinearGradientBrush
        {{
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative)
        }};
        bg.GradientStops.Add(new GradientStop(_c1, 0));
        bg.GradientStops.Add(new GradientStop(_c2, 1));
        context.FillRectangle(bg, rect);

        Point? cursor = _pointer is {{ }} p ? new Point(p.X * rect.Width, p.Y * rect.Height) : null;
        double hStep = Math.Sqrt(3) * R, vStep = 1.5 * R;
        int row = 0;
        for (double cy = 0; cy < rect.Height + R; cy += vStep, row++)
        {{
            double offset = (row % 2 == 0) ? 0 : hStep / 2;
            for (double cx = -hStep + offset; cx < rect.Width + hStep; cx += hStep)
            {{
                double glow = Proximity(cursor, cx, cy);
                var color = Lerp(_ink, Purple, glow);
                var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(55 + 170 * glow), color.R, color.G, color.B)), 1 + glow * 1.6);
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {{
                    for (int i = 0; i < 6; i++)
                    {{
                        double ang = Math.PI / 180 * (60 * i - 90);
                        var pt = new Point(cx + R * Math.Cos(ang), cy + R * Math.Sin(ang));
                        if (i == 0) g.BeginFigure(pt, false); else g.LineTo(pt);
                    }}
                    g.EndFigure(true);
                }}
                context.DrawGeometry(null, pen, geo);
            }}
        }}
    }}

    private static double Proximity(Point? cursor, double x, double y)
    {{
        if (cursor is not {{ }} c) return 0;
        double dx = x - c.X, dy = y - c.Y;
        double t = Math.Clamp(1 - Math.Sqrt(dx * dx + dy * dy) / Reach, 0, 1);
        return t * t;
    }}

    private static Color Lerp(Color a, Color b, double t)
    {{
        byte L(byte from, byte to) => (byte)(from + (to - from) * t);
        return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }}
}}
";

    private static string GridSource(string n, string id) => $@"using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Docklys.ModuleContracts;

namespace DocklysPatterns;

// Reactive template: a faint graph-paper grid whose intersection nodes flare
// purple near the cursor. No trails.
public sealed class {n}Pattern : IPattern
{{
    public string PatternName => ""{n}"";
    public string PatternVersion => ""1.0"";
    public string UniquePatternId {{ get; private set; }} = ""{id}"";
    public void SetPatternId(string uniquePatternId) => UniquePatternId = uniquePatternId;
    public Control CreateView(PatternContext ctx) => new {n}View(ctx);
}}

internal sealed class {n}View : Control, IPatternInteraction
{{
    private readonly Color _c1, _c2, _ink;
    private static readonly Color Purple = Color.FromRgb(0xB0, 0x61, 0xFF);
    private Point? _pointer;
    private const double Step = 32, Reach = 130;

    public {n}View(PatternContext ctx)
    {{
        _c1 = ctx.Color1; _c2 = ctx.Color2; _ink = ctx.Color3;
        ClipToBounds = true;
    }}

    public void OnPointerMoved(double? x, double? y)
    {{
        _pointer = (x is {{ }} px && y is {{ }} py) ? new Point(px, py) : null;
        InvalidateVisual();
    }}

    public override void Render(DrawingContext context)
    {{
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var bg = new LinearGradientBrush
        {{
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative)
        }};
        bg.GradientStops.Add(new GradientStop(_c1, 0));
        bg.GradientStops.Add(new GradientStop(_c2, 1));
        context.FillRectangle(bg, rect);

        var line = new Pen(new SolidColorBrush(Color.FromArgb(28, _ink.R, _ink.G, _ink.B)), 1);
        for (double x = Step; x < rect.Width; x += Step)
            context.DrawLine(line, new Point(x, 0), new Point(x, rect.Height));
        for (double y = Step; y < rect.Height; y += Step)
            context.DrawLine(line, new Point(0, y), new Point(rect.Width, y));

        Point? cursor = _pointer is {{ }} p ? new Point(p.X * rect.Width, p.Y * rect.Height) : null;
        for (double y = Step; y < rect.Height; y += Step)
            for (double x = Step; x < rect.Width; x += Step)
            {{
                double glow = Proximity(cursor, x, y);
                if (glow <= 0.001) continue;
                var color = Lerp(_ink, Purple, glow);
                var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(60 + 180 * glow), color.R, color.G, color.B)), 1 + glow * 1.5);
                double arm = 3 + glow * 5;
                context.DrawLine(pen, new Point(x - arm, y), new Point(x + arm, y));
                context.DrawLine(pen, new Point(x, y - arm), new Point(x, y + arm));
            }}
    }}

    private static double Proximity(Point? cursor, double x, double y)
    {{
        if (cursor is not {{ }} c) return 0;
        double dx = x - c.X, dy = y - c.Y;
        double t = Math.Clamp(1 - Math.Sqrt(dx * dx + dy * dy) / Reach, 0, 1);
        return t * t;
    }}

    private static Color Lerp(Color a, Color b, double t)
    {{
        byte L(byte from, byte to) => (byte)(from + (to - from) * t);
        return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }}
}}
";
}
