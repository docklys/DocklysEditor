using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace VolumeMixer;

/// <summary>Locates an application's freedesktop.org icon without requiring a GTK dependency.</summary>
internal static class LinuxIconResolver
{
    private static readonly ConcurrentDictionary<string, string?> IconPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] IconRoots =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "icons"),
        "/usr/local/share/icons",
        "/usr/share/icons",
        "/usr/share/pixmaps"
    };
    private static readonly string[] DesktopRoots =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applications"),
        "/usr/local/share/applications",
        "/usr/share/applications"
    };

    internal static IImage? Load(IAudioSession session)
    {
        var cacheKey = string.Join('|', session.IconName, session.GroupKey, session.DisplayName);
        var iconPath = IconPaths.GetOrAdd(cacheKey, _ => FindIconPath(session));
        if (string.IsNullOrWhiteSpace(iconPath)) return null;

        try
        {
            return LoadMonochrome(iconPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VolumeMixer] Could not load Linux icon '{iconPath}': {ex.Message}");
            return null;
        }
    }

    private static string? FindIconPath(IAudioSession session)
    {
        var iconName = session.IconName;
        if (string.IsNullOrWhiteSpace(iconName))
            iconName = FindDesktopIconName(session) ?? session.GroupKey;

        if (Path.IsPathFullyQualified(iconName) && File.Exists(iconName)) return iconName;

        var candidates = new[] { iconName, session.GroupKey, session.DisplayName }
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => candidate!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            foreach (var root in IconRoots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    var match = Directory.EnumerateFiles(root, candidate + ".*", SearchOption.AllDirectories)
                        .Where(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                            || path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                            || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(path => IconRank(path))
                        .FirstOrDefault();
                    if (match is not null) return match;
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }
        return null;
    }

    private static string? FindDesktopIconName(IAudioSession session)
    {
        foreach (var root in DesktopRoots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var desktopFile in Directory.EnumerateFiles(root, "*.desktop"))
                {
                    var lines = File.ReadLines(desktopFile).Take(40).ToArray();
                    var executable = lines.FirstOrDefault(line => line.StartsWith("Exec=", StringComparison.Ordinal))?[5..];
                    var name = lines.FirstOrDefault(line => line.StartsWith("Name=", StringComparison.Ordinal))?[5..];
                    if (!(executable?.Contains(session.GroupKey, StringComparison.OrdinalIgnoreCase) == true
                        || string.Equals(name, session.DisplayName, StringComparison.OrdinalIgnoreCase))) continue;
                    return lines.FirstOrDefault(line => line.StartsWith("Icon=", StringComparison.Ordinal))?[5..];
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return null;
    }

    private static int IconRank(string path) =>
        path.Contains("/16x16/", StringComparison.Ordinal) ? 0 :
        path.Contains("/22x22/", StringComparison.Ordinal) ? 1 :
        path.Contains("/24x24/", StringComparison.Ordinal) ? 2 :
        path.Contains("/32x32/", StringComparison.Ordinal) ? 3 : 4;

    // Rendering through ImageMagick rasterizes SVGs and PNGs uniformly while keeping the
    // alpha channel, so the icon stays a transparent-background glyph. The tone pipeline
    // here mirrors the Windows ApplyMonochrome() filter exactly, producing a punchy full
    // black-to-white monochrome that reads with high contrast on the medium-grey tile:
    //   -auto-level             stretch the icon's own tones across the full 0-255 range
    //   -sigmoidal-contrast 7x40%  strong S-curve, midpoint biased bright (white-leaning)
    // -channel RGB keeps the operators off the alpha mask so the background stays clear.
    private static readonly string[] MagickExecutables = { "convert", "magick" };

    private static IImage? LoadMonochrome(string iconPath)
    {
        foreach (var executable in MagickExecutables)
        {
            try
            {
                var info = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                info.ArgumentList.Add(iconPath);
                info.ArgumentList.Add("-background");
                info.ArgumentList.Add("none");
                info.ArgumentList.Add("-resize");
                info.ArgumentList.Add("32x32");
                info.ArgumentList.Add("-colorspace");
                info.ArgumentList.Add("Gray");
                // Restrict the tone operators to colour channels so the alpha mask (and thus the
                // transparent background) is left untouched.
                info.ArgumentList.Add("-channel");
                info.ArgumentList.Add("RGB");
                info.ArgumentList.Add("-auto-level");
                // Strong S-curve toward pure black/white; 40% midpoint biases the result bright.
                info.ArgumentList.Add("-sigmoidal-contrast");
                info.ArgumentList.Add("7x40%");
                info.ArgumentList.Add("+channel");
                info.ArgumentList.Add("png:-");

                using var process = Process.Start(info);
                if (process is null) continue;
                using var output = new MemoryStream();
                process.StandardOutput.BaseStream.CopyTo(output);
                var error = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(2_000) || process.ExitCode != 0)
                {
                    Debug.WriteLine($"[VolumeMixer] Icon conversion via '{executable}' failed: {error}");
                    continue;
                }
                output.Position = 0;
                return new Bitmap(output);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VolumeMixer] '{executable}' is unavailable: {ex.Message}");
            }
        }
        return null;
    }
}
