using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Docklys.ModuleContracts;

namespace DocklysPlugins.Matrix;

// A Matrix "Communication Provider" for the Docklys Comms module. It owns a
// homeserver session (Matrix client-server API) and exposes it through the
// protocol-agnostic ICommsProvider contract, so the Comms UI never sees a single
// Matrix-specific type. Self-contained: just HttpClient + System.Text.Json, no
// SDK dependency — the same hand-rolled approach the Google Calendar plugin uses
// for its ICS feed.
//
// Decentralization: the homeserver is user-defined (default
// https://matrix.qwqc.de — the private Conduit server on philipedia). Point it
// at any homeserver and the rest is unchanged.
public sealed class MatrixProvider : ICommsProvider
{
    public const string ProviderIdConst = "comms.matrix";
    public const string KeyHomeserver = "homeserver";
    public const string KeyUser       = "user";
    public const string KeyPassword   = "password";
    public const string DefaultHomeserver = "https://matrix.qwqc.de";

    // One process-wide instance shared by the plugin (which registers it on the
    // bridge) and the settings view (which drives connect/disconnect).
    public static MatrixProvider Instance { get; } = new();

    public string ProviderId      => ProviderIdConst;
    public string ProviderName    => "Matrix";
    public string ProviderVersion => "1.0";

    public bool   IsConnected => _connected;
    public string Status      => _status;

    public event Action<CommsMessage>? MessageReceived;
    public event Action? Changed;

    private readonly object _gate = new();
    private volatile bool _connected;
    private volatile string _status = "Not configured";
    private bool _loaded;

    // Session state (in memory only).
    private string _homeserver = DefaultHomeserver;
    private string? _userId;            // full mxid "@lz:matrix.qwqc.de"
    private string? _token;             // access token
    private HttpClient? _http;
    private CancellationTokenSource? _syncCts;
    private Task? _syncTask;
    private bool _initialSyncDone;
    private readonly TaskCompletionSource _initialSync = NewTcs();
    private long _txn;

    // Caches built from /sync.
    private readonly ConcurrentDictionary<string, RoomInfo> _rooms = new();

    private MatrixProvider() { }

    // ── Startup: load saved creds and auto-connect, so messages flow from app
    //    launch (mirrors GoogleCalendarTunnel.EnsureLoaded). Idempotent. ────────
    public void EnsureLoaded()
    {
        lock (_gate) { if (_loaded) return; _loaded = true; }
        try
        {
            var (hs, user, pass) = ReadSavedCreds();
            if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
                _ = ConnectAsync();
            else
                SetStatus("Not configured");
        }
        catch (Exception ex) { SetStatus("Error: " + Short(ex.Message)); }
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_connected) return;

        var (hs, user, pass) = ReadSavedCreds();
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            SetStatus("Not configured");
            return;
        }

        _homeserver = NormalizeHomeserver(hs);
        SetStatus("Logging in…");

        try
        {
            _http = new HttpClient { BaseAddress = new Uri(_homeserver), Timeout = TimeSpan.FromSeconds(65) };

            var loginBody = new
            {
                type = "m.login.password",
                identifier = new { type = "m.id.user", user = LocalOrFull(user!) },
                password = pass,
                initial_device_display_name = "Docklys Comms",
            };
            using var resp = await PostJsonAsync("/_matrix/client/v3/login", loginBody, ct).ConfigureAwait(false);
            var json = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                SetStatus("Login failed: " + (Str(json, "error") ?? resp.StatusCode.ToString()));
                _http.Dispose(); _http = null;
                return;
            }

            _token  = Str(json, "access_token");
            _userId = Str(json, "user_id");
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            _connected = true;
            SetStatus($"Connected as {_userId}");

            // Start the long-poll sync loop.
            _syncCts = new CancellationTokenSource();
            _syncTask = Task.Run(() => SyncLoop(_syncCts.Token));
        }
        catch (Exception ex)
        {
            SetStatus("Error: " + Short(ex.Message));
            try { _http?.Dispose(); } catch { }
            _http = null;
            _connected = false;
        }
    }

    public Task DisconnectAsync()
    {
        try { _syncCts?.Cancel(); } catch { }
        _connected = false;
        _initialSyncDone = false;
        _rooms.Clear();
        try { _http?.Dispose(); } catch { }
        _http = null;
        _token = null;
        SetStatus("Disconnected");
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<CommsContact>> GetContactsAsync()
    {
        if (!_connected) return Array.Empty<CommsContact>();
        // Wait for the first /sync so room names/membership are populated.
        if (!_initialSyncDone)
            await Task.WhenAny(_initialSync.Task, Task.Delay(8000)).ConfigureAwait(false);

        return _rooms.Values
            .OrderByDescending(r => r.LastActivity)
            .Select(r => new CommsContact(
                Id: r.RoomId,
                DisplayName: r.DisplayName,
                AvatarUrl: McxToHttp(r.AvatarMxc),
                IsGroup: r.MemberCount > 2,
                UnreadCount: r.UnreadCount))
            .ToList();
    }

    public async Task<IReadOnlyList<CommsMessage>> GetMessagesAsync(string contactId, int limit = 50)
    {
        if (_http == null || !_connected) return Array.Empty<CommsMessage>();
        try
        {
            var path = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(contactId)}/messages?dir=b&limit={limit}";
            using var resp = await _http.GetAsync(path).ConfigureAwait(false);
            var json = await ReadJsonAsync(resp, default).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Array.Empty<CommsMessage>();

            var msgs = new List<CommsMessage>();
            if (json.TryGetProperty("chunk", out var chunk) && chunk.ValueKind == JsonValueKind.Array)
                foreach (var ev in chunk.EnumerateArray())
                {
                    var m = MapMessage(ev, contactId);
                    if (m != null) msgs.Add(m);
                }
            // /messages dir=b is newest-first → flip to chronological.
            msgs.Reverse();
            return msgs;
        }
        catch { return Array.Empty<CommsMessage>(); }
    }

    public async Task<string> SendMessageAsync(string contactId, string body, CancellationToken ct = default)
    {
        if (_http == null || !_connected) throw new InvalidOperationException("Matrix not connected.");
        var txn = NextTxn();
        var path = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(contactId)}/send/m.room.message/{txn}";
        var content = new { msgtype = "m.text", body };
        using var resp = await PutJsonAsync(path, content, ct).ConfigureAwait(false);
        var json = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException("send failed: " + (Str(json, "error") ?? resp.StatusCode.ToString()));
        return Str(json, "event_id") ?? txn;
    }

    public async Task<string> SendFileAsync(string contactId, string fileName, string mimeType,
                                            System.IO.Stream content, CancellationToken ct = default)
    {
        if (_http == null || !_connected) throw new InvalidOperationException("Matrix not connected.");

        // 1) Upload bytes → mxc:// URI.
        using var ms = new System.IO.MemoryStream();
        await content.CopyToAsync(ms, ct).ConfigureAwait(false);
        var bytes = ms.ToArray();
        var uploadReq = new HttpRequestMessage(HttpMethod.Post,
            $"/_matrix/media/v3/upload?filename={Uri.EscapeDataString(fileName)}")
        { Content = new ByteArrayContent(bytes) };
        uploadReq.Content.Headers.ContentType = new MediaTypeHeaderValue(SafeMime(mimeType));
        using var upResp = await _http.SendAsync(uploadReq, ct).ConfigureAwait(false);
        var upJson = await ReadJsonAsync(upResp, ct).ConfigureAwait(false);
        if (!upResp.IsSuccessStatusCode)
            throw new HttpRequestException("upload failed: " + (Str(upJson, "error") ?? upResp.StatusCode.ToString()));
        var mxc = Str(upJson, "content_uri") ?? throw new HttpRequestException("upload: no content_uri");

        // 2) Reference it in an m.file (or m.image) message.
        var msgtype = mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "m.image" : "m.file";
        var txn = NextTxn();
        var path = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(contactId)}/send/m.room.message/{txn}";
        var msg = new
        {
            msgtype,
            body = fileName,
            filename = fileName,
            url = mxc,
            info = new { mimetype = SafeMime(mimeType), size = bytes.LongLength },
        };
        using var resp = await PutJsonAsync(path, msg, ct).ConfigureAwait(false);
        var json = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException("send file failed: " + (Str(json, "error") ?? resp.StatusCode.ToString()));
        return Str(json, "event_id") ?? txn;
    }

    // ── Long-poll /sync loop ────────────────────────────────────────────────────
    private async Task SyncLoop(CancellationToken ct)
    {
        string? since = null;
        // Lazy-load members + small initial timeline so the first sync is fast.
        const string filter = "{\"room\":{\"timeline\":{\"limit\":20},\"state\":{\"lazy_load_members\":true}}}";

        while (!ct.IsCancellationRequested && _http != null)
        {
            try
            {
                var url = since == null
                    ? $"/_matrix/client/v3/sync?timeout=0&filter={Uri.EscapeDataString(filter)}"
                    : $"/_matrix/client/v3/sync?timeout=30000&since={Uri.EscapeDataString(since)}&filter={Uri.EscapeDataString(filter)}";

                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        SetStatus("Session expired — reconnect");
                        _connected = false;
                        return;
                    }
                    await Task.Delay(3000, ct).ConfigureAwait(false);
                    continue;
                }

                var json = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
                ProcessSync(json, emitLive: since != null);
                since = Str(json, "next_batch") ?? since;

                if (!_initialSyncDone)
                {
                    _initialSyncDone = true;
                    _initialSync.TrySetResult();
                    Changed?.Invoke();
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception)
            {
                try { await Task.Delay(3000, ct).ConfigureAwait(false); } catch { return; }
            }
        }
    }

    private void ProcessSync(JsonElement root, bool emitLive)
    {
        if (!root.TryGetProperty("rooms", out var rooms)) return;
        if (!rooms.TryGetProperty("join", out var join) || join.ValueKind != JsonValueKind.Object) return;

        bool roomsChanged = false;

        foreach (var roomProp in join.EnumerateObject())
        {
            var roomId = roomProp.Name;
            var room = roomProp.Value;
            var info = _rooms.GetOrAdd(roomId, id => new RoomInfo(id));

            // State (room name, avatar, members).
            if (room.TryGetProperty("state", out var state) &&
                state.TryGetProperty("events", out var stateEvents) &&
                stateEvents.ValueKind == JsonValueKind.Array)
            {
                foreach (var ev in stateEvents.EnumerateArray())
                    ApplyState(info, ev);
            }

            // Timeline (messages + inline state).
            if (room.TryGetProperty("timeline", out var timeline) &&
                timeline.TryGetProperty("events", out var tlEvents) &&
                tlEvents.ValueKind == JsonValueKind.Array)
            {
                foreach (var ev in tlEvents.EnumerateArray())
                {
                    var type = Str(ev, "type");
                    if (type == "m.room.message")
                    {
                        info.LastActivity = Ts(ev);
                        roomsChanged = true;
                        if (emitLive)
                        {
                            var msg = MapMessage(ev, roomId);
                            if (msg != null)
                            {
                                if (msg.Direction == MessageDirection.Incoming) info.UnreadCount++;
                                MessageReceived?.Invoke(msg);
                            }
                        }
                    }
                    else if (type != null && type.StartsWith("m.room."))
                    {
                        ApplyState(info, ev); // m.room.name / member updates also arrive here
                    }
                }
            }

            info.RecomputeDisplayName(_userId);
        }

        if (roomsChanged) Changed?.Invoke();
    }

    private void ApplyState(RoomInfo info, JsonElement ev)
    {
        var type = Str(ev, "type");
        if (!ev.TryGetProperty("content", out var content)) { }

        switch (type)
        {
            case "m.room.name":
                info.Name = Str(content, "name");
                break;
            case "m.room.canonical_alias":
                info.CanonicalAlias = Str(content, "alias");
                break;
            case "m.room.avatar":
                info.AvatarMxc = Str(content, "url");
                break;
            case "m.room.member":
            {
                var stateKey = Str(ev, "state_key");
                var membership = Str(content, "membership");
                var display = Str(content, "displayname");
                if (stateKey != null)
                {
                    if (membership == "join")
                    {
                        info.Members[stateKey] = display ?? LocalPart(stateKey);
                    }
                    else
                    {
                        info.Members.TryRemove(stateKey, out _);
                    }
                }
                break;
            }
        }
    }

    private CommsMessage? MapMessage(JsonElement ev, string roomId)
    {
        if (Str(ev, "type") != "m.room.message") return null;
        var sender = Str(ev, "sender") ?? "@unknown";
        var id = Str(ev, "event_id") ?? Guid.NewGuid().ToString();
        var ts = Ts(ev);

        string body = "";
        CommsAttachment? attachment = null;
        if (ev.TryGetProperty("content", out var content))
        {
            body = Str(content, "body") ?? "";
            var msgtype = Str(content, "msgtype");
            var mxc = Str(content, "url");
            if ((msgtype is "m.file" or "m.image" or "m.video" or "m.audio") && mxc != null)
            {
                long size = 0; string mime = "application/octet-stream";
                if (content.TryGetProperty("info", out var infoEl))
                {
                    mime = Str(infoEl, "mimetype") ?? mime;
                    if (infoEl.TryGetProperty("size", out var s) && s.TryGetInt64(out var sl)) size = sl;
                }
                attachment = new CommsAttachment(body, mime, size, McxToHttp(mxc));
            }
        }

        var senderName = _rooms.TryGetValue(roomId, out var ri) && ri.Members.TryGetValue(sender, out var dn)
            ? dn : LocalPart(sender);
        var dir = sender == _userId ? MessageDirection.Outgoing : MessageDirection.Incoming;
        return new CommsMessage(id, roomId, sender, senderName, body, ts, dir, attachment);
    }

    // ── Settings persistence (reads the same file the host's PluginContext bag
    //    writes — %AppData%/Docklys/Plugin/<id>.settings.json) ─────────────────────
    private (string hs, string? user, string? pass) ReadSavedCreds()
    {
        var map = ReadSettings();
        var hs   = map.TryGetValue(KeyHomeserver, out var h) && !string.IsNullOrWhiteSpace(h) ? h : DefaultHomeserver;
        map.TryGetValue(KeyUser, out var user);
        map.TryGetValue(KeyPassword, out var pass);
        return (hs, user, pass);
    }

    private static Dictionary<string, string> ReadSettings()
    {
        var file = SettingsFile();
        if (!System.IO.File.Exists(file)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(file))
                   ?? new();
        }
        catch { return new(); }
    }

    private static string SettingsFile() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Docklys", "Plugin", ProviderIdConst + ".settings.json");

    // ── HTTP + JSON helpers ─────────────────────────────────────────────────────
    private Task<HttpResponseMessage> PostJsonAsync(string path, object body, CancellationToken ct)
        => _http!.PostAsync(path, JsonContent(body), ct);

    private Task<HttpResponseMessage> PutJsonAsync(string path, object body, CancellationToken ct)
        => _http!.PutAsync(path, JsonContent(body), ct);

    private static StringContent JsonContent(object body)
        => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(s)) return default;
        try { return JsonDocument.Parse(s).RootElement.Clone(); }
        catch { return default; }
    }

    private static string? Str(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static DateTimeOffset Ts(JsonElement ev)
        => ev.TryGetProperty("origin_server_ts", out var t) && t.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.UtcNow;

    private string NextTxn() => $"docklys-{Interlocked.Increment(ref _txn)}-{Guid.NewGuid():N}";

    private string? McxToHttp(string? mxc)
    {
        if (string.IsNullOrWhiteSpace(mxc) || !mxc.StartsWith("mxc://")) return null;
        var rest = mxc.Substring("mxc://".Length);
        var slash = rest.IndexOf('/');
        if (slash < 0) return null;
        var server = rest.Substring(0, slash);
        var mediaId = rest.Substring(slash + 1);
        return $"{_homeserver}/_matrix/media/v3/download/{server}/{mediaId}";
    }

    private static string LocalOrFull(string user)
    {
        // "@lz:matrix.qwqc.de" → "lz"; "lz" stays "lz".
        if (user.StartsWith("@") && user.Contains(':'))
            return user.Substring(1, user.IndexOf(':') - 1);
        return user;
    }

    private static string LocalPart(string mxid)
    {
        if (mxid.StartsWith("@") && mxid.Contains(':'))
            return mxid.Substring(1, mxid.IndexOf(':') - 1);
        return mxid;
    }

    private static string NormalizeHomeserver(string hs)
    {
        hs = hs.Trim().TrimEnd('/');
        if (!hs.StartsWith("http://") && !hs.StartsWith("https://")) hs = "https://" + hs;
        return hs;
    }

    private static string SafeMime(string mime)
        => string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime;

    private void SetStatus(string s) { _status = s; Changed?.Invoke(); }

    private static string Short(string s) => s.Length > 140 ? s.Substring(0, 140) + "…" : s;

    private static TaskCompletionSource NewTcs()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Per-room aggregate built from /sync.
    private sealed class RoomInfo
    {
        public RoomInfo(string roomId) { RoomId = roomId; DisplayName = roomId; }
        public string RoomId { get; }
        public string? Name { get; set; }
        public string? CanonicalAlias { get; set; }
        public string? AvatarMxc { get; set; }
        public string DisplayName { get; set; }
        public int UnreadCount { get; set; }
        public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.MinValue;
        public ConcurrentDictionary<string, string> Members { get; } = new();
        public int MemberCount => Members.Count;

        public void RecomputeDisplayName(string? selfId)
        {
            if (!string.IsNullOrWhiteSpace(Name)) { DisplayName = Name!; return; }
            // 1:1 / small DM → name after the other participant.
            var others = Members.Where(kv => kv.Key != selfId).Select(kv => kv.Value).ToList();
            if (others.Count >= 1 && others.Count <= 2) { DisplayName = string.Join(", ", others); return; }
            if (!string.IsNullOrWhiteSpace(CanonicalAlias)) { DisplayName = CanonicalAlias!; return; }
            DisplayName = RoomId;
        }
    }
}
