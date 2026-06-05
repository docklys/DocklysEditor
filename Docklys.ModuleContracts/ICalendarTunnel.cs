using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Docklys.ModuleContracts;

// A single calendar event exposed to modules through the tunnel. Times keep their
// UTC offset; AllDay events span whole days (End is exclusive midnight).
public sealed record CalendarEvent(
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool AllDay,
    string? Location,
    string? Description);

// The "tunnel" a calendar plugin publishes so modules can read calendar data
// without holding any credentials themselves. The Google Calendar plugin owns the
// credential (a secret iCal URL) plus the network + parsing; a module just asks
// for upcoming events through this interface.
public interface ICalendarTunnel
{
    // True once the providing plugin has a usable credential configured.
    bool IsConfigured { get; }

    // Human-readable state (e.g. "Connected — 7 upcoming" or an error message).
    string Status { get; }

    // Upcoming events (now → future), soonest first, capped at maxResults.
    Task<IReadOnlyList<CalendarEvent>> GetUpcomingEventsAsync(int maxResults = 10);

    // Raised when the credential changes or a refresh finishes, so subscribed
    // modules can re-pull. Handlers may be invoked off the UI thread — marshal.
    event Action? Changed;
}

// The shared channel between the calendar plugin and the modules that consume it.
// It lives in the contracts assembly, which binds by simple name across the app,
// plugin DLLs, and module DLLs — so whatever the plugin publishes to Current is
// the same instance every module sees. The Google Calendar plugin sets this; a
// module reads CalendarBridge.Current and calls GetUpcomingEventsAsync.
public static class CalendarBridge
{
    public static ICalendarTunnel? Current { get; set; }
}
