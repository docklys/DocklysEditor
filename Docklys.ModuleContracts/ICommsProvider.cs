using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Docklys.ModuleContracts;

// ─────────────────────────────────────────────────────────────────────────────
// Comms provider contract
//
// The "Comms" module is a protocol-agnostic chat shell. It never references
// Matrix (or any concrete protocol) — it talks only to ICommsProvider. A
// "Communication Provider" is published by an IPlugin (loaded from
// %AppData%/Docklys/Plugin, hot-loadable into a collectible ALC) and registers
// itself on CommsBridge, exactly like the Google Calendar plugin publishes an
// ICalendarTunnel on CalendarBridge.Current — see ICalendarTunnel.cs.
//
// To add a protocol (Matrix today, a P2P transport tomorrow) you write a new
// plugin implementing this interface and call CommsBridge.Register(this). No
// change to the Comms module is required.
// ─────────────────────────────────────────────────────────────────────────────

public enum MessageDirection { Incoming, Outgoing }

public enum DeliveryState { Pending, Sent, Delivered, Read, Failed }

// A conversation target: a person OR a room/group/channel. Ids are
// provider-scoped and stable (e.g. a Matrix room id "!abc:matrix.qwqc.de").
public sealed record CommsContact(
    string  Id,
    string  DisplayName,
    string? AvatarUrl  = null,
    bool    IsGroup    = false,
    string? Presence   = null,   // "online"/"away"/"offline"/null when unknown
    int     UnreadCount = 0);

// One message in a conversation.
public sealed record CommsMessage(
    string           Id,
    string           ContactId,           // conversation this belongs to
    string           SenderId,
    string           SenderDisplayName,
    string           Body,
    DateTimeOffset   Timestamp,
    MessageDirection Direction,
    CommsAttachment? Attachment = null);

// A file/media payload carried by a message.
public sealed record CommsAttachment(
    string  FileName,
    string  MimeType,
    long    SizeBytes,
    string? Url = null);                   // resolvable URL (provider may proxy/decrypt)

// The contract every messaging plugin implements. All async members may resolve
// off the UI thread; events may fire off the UI thread — the Comms module
// marshals to Dispatcher.UIThread before touching controls.
public interface ICommsProvider
{
    // Identity / discovery — mirrors the Name/Version/Id triple used by IModule,
    // IPattern, and IPlugin elsewhere in the contracts.
    string  ProviderId      { get; }       // stable, e.g. "comms.matrix"
    string  ProviderName    { get; }       // human label, e.g. "Matrix"
    string  ProviderVersion { get; }
    string? IconUrl => null;

    // True once a usable session exists (server reachable + credentials accepted).
    bool   IsConnected { get; }

    // Human-readable state ("Connected as @lz:matrix.qwqc.de", "Logging in…", error).
    string Status      { get; }

    // Bring the session up / down. Both are idempotent and safe to call repeatedly.
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();

    // Conversations to show in the sidebar / drawer.
    Task<IReadOnlyList<CommsContact>> GetContactsAsync();

    // Recent history for one conversation, chronological (oldest first, newest last).
    Task<IReadOnlyList<CommsMessage>> GetMessagesAsync(string contactId, int limit = 50);

    // sendMessage — returns the provider's message id once accepted.
    Task<string> SendMessageAsync(string contactId, string body, CancellationToken ct = default);

    // sendFile — stream the bytes; the provider handles upload (and encryption).
    Task<string> SendFileAsync(string contactId, string fileName, string mimeType,
                               Stream content, CancellationToken ct = default);

    // onMessageReceived — live push of inbound (and echoed outbound) messages.
    event Action<CommsMessage>? MessageReceived;

    // Contacts / presence / connection-state changed → the UI re-pulls.
    event Action? Changed;
}

// The shared rendezvous between provider plugins and the Comms module. Unlike
// CalendarBridge (a single Current tunnel), Comms keeps a REGISTRY so multiple
// protocols can be live at once and can be added/removed at runtime when a
// plugin is installed or uninstalled. It lives in the contracts assembly, which
// binds by simple name across the host, plugin DLLs, and module DLLs, so every
// party shares one CommsBridge instance.
public static class CommsBridge
{
    private static readonly object _gate = new();
    private static readonly List<ICommsProvider> _providers = new();

    // Snapshot of all providers currently published by loaded plugins.
    public static IReadOnlyList<ICommsProvider> Providers
    {
        get { lock (_gate) return _providers.ToArray(); }
    }

    // Publish a provider (called by a plugin, usually from its constructor).
    // Re-registering the same ProviderId replaces the previous instance.
    public static void Register(ICommsProvider provider)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        lock (_gate)
        {
            _providers.RemoveAll(p => p.ProviderId == provider.ProviderId);
            _providers.Add(provider);
        }
        ProvidersChanged?.Invoke();
    }

    // Remove a provider (called when a plugin is unloaded / uninstalled).
    public static void Unregister(string providerId)
    {
        lock (_gate) _providers.RemoveAll(p => p.ProviderId == providerId);
        ProvidersChanged?.Invoke();
    }

    public static ICommsProvider? Get(string providerId)
    {
        lock (_gate) return _providers.FirstOrDefault(p => p.ProviderId == providerId);
    }

    // Raised when a provider registers/unregisters so the Comms module can add or
    // remove a protocol live (install/uninstall without an app restart). May be
    // invoked off the UI thread.
    public static event Action? ProvidersChanged;
}
