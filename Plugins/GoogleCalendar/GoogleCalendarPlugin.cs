using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Docklys.ModuleContracts;

namespace DocklysPlugins;

// Google Calendar tunnel plugin. The user pastes their calendar's secret iCal URL
// once on the Plugins page; this plugin owns the credential + the network + the
// parsing and publishes a single ICalendarTunnel on CalendarBridge.Current. Any
// module then reads upcoming events through the bridge without touching creds:
//
//     var t = CalendarBridge.Current;
//     if (t?.IsConfigured == true)
//         foreach (var e in await t.GetUpcomingEventsAsync(5)) { /* e.Title, e.Start … */ }
//
// Authored in the RunPlugin editor and pushed to %AppData%/Docklys/Plugin. Its
// constructor publishes the tunnel as soon as the host instantiates the plugin
// (at registry load), so the tunnel is live for modules from app startup.
public sealed class GoogleCalendarPlugin : IPlugin
{
    public const string PluginId = "plugin.googlecalendar";
    public const string KeyUrl = "ical_url";

    public string PluginName => "Google Calendar";
    public string PluginVersion => "1.0";
    public string UniquePluginId { get; private set; } = PluginId;
    public string PluginDescription =>
        "Tunnel your Google Calendar into Docklys — paste your secret iCal link and any module can read your events.";

    public GoogleCalendarPlugin()
    {
        // Publish the tunnel the moment the host loads the plugin, so modules can
        // use CalendarBridge.Current before anyone opens the Plugins page.
        GoogleCalendarTunnel.Instance.EnsureLoaded();
        CalendarBridge.Current = GoogleCalendarTunnel.Instance;
    }

    public void SetPluginId(string uniquePluginId) => UniquePluginId = uniquePluginId;

    public Control CreateSettingsView(PluginContext ctx) => new GoogleCalendarView(ctx);
}

// Process-wide tunnel. Reads its credential straight from the plugin's settings
// file so it can come up before any settings view exists, then fetches + parses
// the ICS feed on demand (cached). Self-contained: no host-internal dependencies.
internal sealed class GoogleCalendarTunnel : ICalendarTunnel
{
    public static GoogleCalendarTunnel Instance { get; } = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private bool _loaded;
    private string? _url;
    private string _status = "Not configured";
    private List<CalendarEvent>? _cache;
    private DateTimeOffset _cacheTimeUtc;

    public bool IsConfigured { get { lock (_gate) return !string.IsNullOrWhiteSpace(_url); } }
    public string Status => _status;
    public event Action? Changed;

    private GoogleCalendarTunnel() { }

    // Load the persisted credential once (idempotent). Called from the plugin ctor.
    public void EnsureLoaded()
    {
        lock (_gate) { if (_loaded) return; _loaded = true; }
        try { Configure(ReadSavedUrl()); }
        catch (Exception ex) { Console.WriteLine($"[GoogleCalendar] EnsureLoaded: {ex.Message}"); }
    }

    public void Configure(string? url)
    {
        lock (_gate)
        {
            _url = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
            _cache = null;
            _status = _url == null ? "Not configured" : "Configured — not yet refreshed";
        }
        Changed?.Invoke();
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetUpcomingEventsAsync(int maxResults = 10)
    {
        string? url;
        lock (_gate)
        {
            url = _url;
            if (_cache != null && DateTimeOffset.UtcNow - _cacheTimeUtc < CacheTtl)
                return _cache.Take(maxResults).ToList();
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            _status = "Not configured";
            return Array.Empty<CalendarEvent>();
        }

        try
        {
            var ics = await Http.GetStringAsync(NormalizeUrl(url)).ConfigureAwait(false);
            var now = DateTimeOffset.Now;
            var upcoming = IcsParser.Parse(ics)
                .Where(e => e.End >= now)
                .OrderBy(e => e.Start)
                .ToList();

            lock (_gate) { _cache = upcoming; _cacheTimeUtc = DateTimeOffset.UtcNow; }
            _status = $"Connected — {upcoming.Count} upcoming";
            Changed?.Invoke();
            return upcoming.Take(maxResults).ToList();
        }
        catch (Exception ex)
        {
            _status = "Error: " + Short(ex.Message);
            Changed?.Invoke();
            return Array.Empty<CalendarEvent>();
        }
    }

    public Task<IReadOnlyList<CalendarEvent>> RefreshAsync(int maxResults = 10)
    {
        lock (_gate) { _cache = null; }
        return GetUpcomingEventsAsync(maxResults);
    }

    // Read the saved credential from %AppData%/Docklys/Plugin/<id>.settings.json —
    // the same file the host's PluginContext bag writes to.
    private static string? ReadSavedUrl()
    {
        var file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Docklys", "Plugin", GoogleCalendarPlugin.PluginId + ".settings.json");
        if (!File.Exists(file)) return null;
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
            return map != null && map.TryGetValue(GoogleCalendarPlugin.KeyUrl, out var v) ? v : null;
        }
        catch { return null; }
    }

    private static string NormalizeUrl(string url) =>
        url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url.Substring("webcal://".Length)
            : url;

    private static string Short(string s) => s.Length > 140 ? s.Substring(0, 140) + "…" : s;
}

internal sealed class GoogleCalendarView : UserControl
{
    private readonly PluginContext _ctx;
    private readonly GoogleCalendarTunnel _tunnel = GoogleCalendarTunnel.Instance;
    private readonly TextBox _url;
    private readonly TextBlock _status = new();
    private readonly StackPanel _events = new() { Spacing = 4 };
    private readonly Action _onChanged;

    public GoogleCalendarView(PluginContext ctx)
    {
        _ctx = ctx;

        var font = new SolidColorBrush(ctx.Font);
        var accent = new SolidColorBrush(ctx.Accent);

        var help = new TextBlock
        {
            Text = "Paste your Google Calendar \"Secret address in iCal format\" "
                 + "(Google Calendar → calendar Settings → Integrate calendar). "
                 + "It stays on this machine; modules read events through the tunnel.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.85,
            Foreground = font,
        };

        _url = new TextBox
        {
            Text = ctx.GetSetting(GoogleCalendarPlugin.KeyUrl) ?? "",
            Watermark = "https://calendar.google.com/calendar/ical/…/basic.ics",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Foreground = font,
        };

        var save = new Button { Content = "Save & Connect", Foreground = Brushes.White, Background = accent };
        save.Click += async (_, _) => await SaveAndConnect();

        var refresh = new Button { Content = "Refresh", Foreground = font };
        refresh.Click += async (_, _) => await Reload(force: true);

        _status.Foreground = font;
        _status.FontSize = 12;
        _status.Opacity = 0.9;
        _status.TextWrapping = TextWrapping.Wrap;

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                help,
                Label("Secret iCal URL (credential)", font),
                _url,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, refresh } },
                _status,
                Label("Upcoming events (via the tunnel)", font, top: 6),
                new Border
                {
                    Background = new SolidColorBrush(_ctx.Color2),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12),
                    Child = _events,
                },
            },
        };

        // Keep the status line in step with the tunnel (modules may drive refreshes
        // too). Changed can fire off-thread — marshal to the UI thread.
        _onChanged = () => Dispatcher.UIThread.Post(() => _status.Text = _tunnel.Status);
        _tunnel.Changed += _onChanged;
        DetachedFromVisualTree += (_, _) => _tunnel.Changed -= _onChanged;

        _status.Text = _tunnel.Status;
        _ = Reload(force: false);
    }

    private async Task SaveAndConnect()
    {
        var url = _url.Text?.Trim();
        _ctx.SetSetting(GoogleCalendarPlugin.KeyUrl, string.IsNullOrEmpty(url) ? null : url);
        _tunnel.Configure(url);
        await Reload(force: true);
    }

    private async Task Reload(bool force)
    {
        _events.Children.Clear();
        if (!_tunnel.IsConfigured)
        {
            _events.Children.Add(Muted("No calendar connected yet."));
            return;
        }

        _events.Children.Add(Muted("Loading…"));
        var list = force ? await _tunnel.RefreshAsync(8) : await _tunnel.GetUpcomingEventsAsync(8);

        _events.Children.Clear();
        if (list.Count == 0)
        {
            _events.Children.Add(Muted(_tunnel.Status));
            return;
        }

        var font = new SolidColorBrush(_ctx.Font);
        foreach (var e in list)
        {
            _events.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = When(e), Foreground = font, Opacity = 0.65, FontSize = 12, MinWidth = 130 },
                    new TextBlock { Text = e.Title, Foreground = font, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            });
        }
    }

    private TextBlock Muted(string s)
        => new() { Text = s, Foreground = new SolidColorBrush(_ctx.Font), Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Label(string s, IBrush font, double top = 0)
        => new() { Text = s, Foreground = font, FontSize = 12, Opacity = 0.85, Margin = new Thickness(0, top, 0, 0) };

    private static string When(CalendarEvent e)
    {
        var s = e.Start.ToLocalTime();
        return e.AllDay ? s.ToString("ddd, MMM d") : s.ToString("ddd, MMM d  HH:mm");
    }
}

// Minimal iCalendar (RFC 5545) reader — just enough to turn a Google Calendar
// ".ics" feed into CalendarEvents: line unfolding, VEVENT blocks, the common
// DTSTART/DTEND forms (UTC "…Z", floating local, VALUE=DATE all-day), and text
// escaping. Reads the concrete instances Google already materialises (no RRULE).
internal static class IcsParser
{
    public static IReadOnlyList<CalendarEvent> Parse(string ics)
    {
        var events = new List<CalendarEvent>();
        if (string.IsNullOrEmpty(ics)) return events;

        var lines = Unfold(ics);

        bool inEvent = false;
        string? summary = null, location = null, description = null;
        DateTimeOffset start = default, end = default;
        bool haveStart = false, haveEnd = false, allDay = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                inEvent = true;
                summary = location = description = null;
                start = end = default;
                haveStart = haveEnd = allDay = false;
                continue;
            }
            if (line.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (inEvent && haveStart)
                {
                    if (!haveEnd) end = allDay ? start.AddDays(1) : start.AddHours(1);
                    events.Add(new CalendarEvent(
                        string.IsNullOrWhiteSpace(summary) ? "(no title)" : summary!,
                        start, end, allDay, location, description));
                }
                inEvent = false;
                continue;
            }
            if (!inEvent) continue;

            var (name, prms, value) = SplitProperty(line);
            switch (name.ToUpperInvariant())
            {
                case "SUMMARY": summary = Unescape(value); break;
                case "LOCATION": location = Unescape(value); break;
                case "DESCRIPTION": description = Unescape(value); break;
                case "DTSTART":
                    if (TryParseDate(value, prms, out start, out var ad)) { haveStart = true; allDay = ad; }
                    break;
                case "DTEND":
                    if (TryParseDate(value, prms, out end, out _)) haveEnd = true;
                    break;
            }
        }

        return events;
    }

    private static List<string> Unfold(string ics)
    {
        var raw = ics.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var outLines = new List<string>(raw.Length);
        foreach (var l in raw)
        {
            if (l.Length > 0 && (l[0] == ' ' || l[0] == '\t') && outLines.Count > 0)
                outLines[^1] += l.Substring(1);
            else
                outLines.Add(l);
        }
        return outLines;
    }

    private static (string name, string prms, string value) SplitProperty(string line)
    {
        var colon = line.IndexOf(':');
        if (colon < 0) return (line, "", "");
        var left = line.Substring(0, colon);
        var value = line.Substring(colon + 1);

        var semi = left.IndexOf(';');
        if (semi < 0) return (left, "", value);
        return (left.Substring(0, semi), left.Substring(semi + 1), value);
    }

    private static bool TryParseDate(string value, string prms, out DateTimeOffset result, out bool allDay)
    {
        result = default;
        allDay = prms.IndexOf("VALUE=DATE", StringComparison.OrdinalIgnoreCase) >= 0
                 || (value.Length == 8 && !value.Contains('T'));

        value = value.Trim();

        if (allDay && value.Length >= 8 &&
            DateTime.TryParseExact(value.Substring(0, 8), "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            result = new DateTimeOffset(d.Date, TimeZoneInfo.Local.GetUtcOffset(d.Date));
            return true;
        }

        if (value.EndsWith("Z", StringComparison.OrdinalIgnoreCase) &&
            DateTime.TryParseExact(value.TrimEnd('Z', 'z'), "yyyyMMddTHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var u))
        {
            result = new DateTimeOffset(u, TimeSpan.Zero);
            return true;
        }

        if (DateTime.TryParseExact(value, "yyyyMMddTHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            result = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
            return true;
        }

        return false;
    }

    private static string Unescape(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var n = s[++i];
                sb.Append(n switch { 'n' or 'N' => '\n', ',' => ',', ';' => ';', '\\' => '\\', _ => n });
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
