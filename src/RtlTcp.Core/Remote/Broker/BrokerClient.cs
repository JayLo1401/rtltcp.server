using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Quic;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RtlTcp.Core.Device;

namespace RtlTcp.Core.Remote.Broker;

/// <summary>A stick shared in an instance, as announced by the broker (protocol.md §2.3).</summary>
public sealed record InstanceShare(string ShareId, int ProviderId, string ProviderName, string StickId, string Name,
    string Tuner, string State, bool Online);

/// <summary>One of our sticks, announced to an instance (protocol.md §2.2).</summary>
public sealed record AnnouncedShare(string StickId, string Name, string Tuner, string State);

public sealed class EnrollException(string message) : Exception(message);

public sealed record Enrollment(string Token, int DeviceId, int InstanceId, string InstanceName);

public enum BrokerState { Connecting, Connected, Disconnected, Disabled, Revoked }

/// <summary>
/// Control channel to rtl.over.internet for one instance (one device token): announces our shares, opens
/// sessions to shared sticks (user side) and answers stream requests (provider side). The stream itself never
/// passes the broker: both sides punch a UDP hole and run QUIC directly (docs/protocol.md in rtl.over.internet).
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class BrokerClient : IAsyncDisposable
{
    public sealed record Options(string Url, string Token, string ServerName, string Version);

    /// <summary>Things this server shares and how to serve them (provider side).</summary>
    public sealed record Provider(Func<IReadOnlyList<AnnouncedShare>> Shares, Func<string, DeviceManager?> FindDevice);

    private static readonly TimeSpan GatherTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan PunchWait = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan OpenWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FirstConnectionWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private const int MaxMessage = 64 * 1024;

    private readonly Options _options;
    private readonly PeerIdentity _identity;
    private readonly Provider _provider;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Session>> _pending = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private ClientWebSocket? _ws;
    private Task? _run;
    private int _req;
    private string? _lastAnnounce;
    private List<(string Host, int Port)> _rendezvous = [];

    public BrokerClient(Options options, PeerIdentity identity, Provider provider, ILogger log)
    {
        _options = options;
        _identity = identity;
        _provider = provider;
        _log = log;
    }

    public BrokerState State { get; private set; } = BrokerState.Connecting;
    public string? Message { get; private set; }
    public string? InstanceName { get; private set; }
    public int? DeviceId { get; private set; }
    public IReadOnlyList<InstanceShare> Shares { get; private set; } = [];

    /// <summary>Sessions currently set up or streaming (for the status page).</summary>
    public int ActiveSessions => _sessions.Count;

    public void Start() => _run = RunAsync(_cts.Token);

    // ---------------------------------------------------------------- enrollment

    /// <param name="password">Password of the instance (or the admin password of rtl.over.internet).</param>
    public static async Task<Enrollment> EnrollAsync(string url, string code, string password,
        string serverId, string serverName, string version, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync(new Uri(new Uri(url.TrimEnd('/') + "/"), "api/v1/enroll"), new
            {
                instance_code = code, password, server_id = serverId, server_name = serverName, version,
            }, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            throw new EnrollException($"rtl.over.internet nicht erreichbar: {ex.Message}");
        }
        using var _ = resp;
        JsonNode? body = null;
        try { body = await resp.Content.ReadFromJsonAsync<JsonNode>(ct); } catch (JsonException) { }
        if (resp.IsSuccessStatusCode && body?["device_token"]?.GetValue<string>() is { Length: > 0 } token)
            return new Enrollment(token, body["device_id"]!.GetValue<int>(), body["instance"]?["id"]?.GetValue<int>() ?? 0,
                body["instance"]?["name"]?.GetValue<string>() ?? "");
        throw new EnrollException((string?)body?["error"] switch
        {
            "INVALID_CREDENTIALS" => "Code oder Passwort falsch",
            "INSTANCE_DISABLED" => "Die Instanz ist deaktiviert",
            "RATE_LIMITED" => $"Zu viele Versuche, bitte in {(int?)body?["retry_after"] ?? 600} s erneut versuchen",
            { } e => $"Anmeldung abgelehnt: {e}",
            null => $"Anmeldung fehlgeschlagen (HTTP {(int)resp.StatusCode})",
        });
    }

    // ---------------------------------------------------------------- control channel

    private Uri ControlUri()
    {
        var b = new UriBuilder(_options.Url.TrimEnd('/') + "/api/v1/control");
        b.Scheme = b.Scheme == "http" ? "ws" : "wss";
        return b.Uri;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", "Bearer " + _options.Token);
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            _ws = ws;
            try
            {
                State = BrokerState.Connecting;
                await ws.ConnectAsync(ControlUri(), ct);
                await SendAsync(new JsonObject
                {
                    ["type"] = "hello", ["server_name"] = _options.ServerName, ["version"] = _options.Version,
                    ["cert_fp"] = _identity.Fingerprint,
                });
                _lastAnnounce = null;
                using var announce = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var announcer = AnnounceLoopAsync(announce.Token);
                await ReceiveLoopAsync(ws, ct);
                await announce.CancelAsync();
                await announcer;
                failures = State == BrokerState.Connected ? 0 : failures;

                switch ((int?)ws.CloseStatus)
                {
                    case 4401:
                        SetState(BrokerState.Revoked, "Anmeldung widerrufen oder ungültig – Instanz neu hinzufügen");
                        EndAllSessions("token_revoked");
                        return;
                    case 4403:
                        SetState(BrokerState.Disabled, "Instanz deaktiviert");
                        wait = TimeSpan.FromMinutes(5);
                        break;
                    case 4409:
                        SetState(BrokerState.Disconnected, "Dieselbe Anmeldung ist an anderer Stelle verbunden");
                        wait = Backoff(++failures);
                        break;
                    default:
                        SetState(BrokerState.Disconnected, $"Verbindung getrennt ({ws.CloseStatusDescription ?? ws.CloseStatus?.ToString() ?? "ohne Grund"})");
                        wait = Backoff(++failures);
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or SocketException
                                           or OperationCanceledException or JsonException or InvalidOperationException)
            {
                SetState(BrokerState.Disconnected, $"rtl.over.internet nicht erreichbar: {ex.Message}");
                wait = Backoff(++failures);
            }
            finally
            {
                _ws = null;
            }
            EndAllSessions("peer_offline");
            try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private static TimeSpan Backoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(failures - 1, 6))));

    private void SetState(BrokerState state, string? message)
    {
        if (State != state || Message != message)
            _log.LogInformation("rtl.over.internet {Instance}: {State} {Message}", InstanceName ?? _options.Url, state, message);
        State = state;
        Message = message;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[MaxMessage];
        while (ws.State == WebSocketState.Open)
        {
            var n = 0;
            WebSocketReceiveResult r;
            do
            {
                if (n == buf.Length) throw new InvalidOperationException("Nachricht vom Vermittler zu groß");
                r = await ws.ReceiveAsync(buf.AsMemory(n), ct) is var vr ? new WebSocketReceiveResult(vr.Count, vr.MessageType, vr.EndOfMessage) : null!;
                n += r.Count;
            } while (!r.EndOfMessage && r.MessageType != WebSocketMessageType.Close);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
                catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { }
                return;
            }
            if (r.MessageType != WebSocketMessageType.Text) continue;
            JsonObject? msg;
            try { msg = JsonNode.Parse(buf.AsSpan(0, n)) as JsonObject; }
            catch (JsonException) { continue; }
            if (msg is not null)
            {
                try { Handle(msg); }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException or NullReferenceException)
                {
                    _log.LogWarning("rtl.over.internet: cannot handle {Type}: {Error}", (string?)msg["type"], ex.Message);
                }
            }
        }
    }

    private async Task SendAsync(JsonObject msg)
    {
        var ws = _ws ?? throw new WebSocketException("Steuerkanal nicht verbunden");
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendLock.WaitAsync();
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task TrySendAsync(JsonObject msg)
    {
        try { await SendAsync(msg); }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
    }

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (State == BrokerState.Connected)
                {
                    var arr = new JsonArray();
                    foreach (var s in _provider.Shares().Take(64))
                        arr.Add(new JsonObject
                        {
                            ["stick_id"] = s.StickId, ["name"] = Clip(s.Name, 100), ["tuner"] = Clip(s.Tuner, 32),
                            ["state"] = Clip(s.State, 32), ["compression"] = false,
                        });
                    var msg = new JsonObject { ["type"] = "announce", ["shares"] = arr };
                    var text = msg.ToJsonString();
                    if (text != _lastAnnounce)
                    {
                        await SendAsync(msg);
                        _lastAnnounce = text;
                    }
                }
                await Task.Delay(AnnounceInterval, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    // ---------------------------------------------------------------- messages

    private void Handle(JsonObject m)
    {
        switch ((string?)m["type"])
        {
            case "welcome":
                DeviceId = (int?)m["device_id"];
                InstanceName = (string?)m["instance"]?["name"];
                _rendezvous = m["rendezvous"] is JsonArray rv
                    ? [.. rv.OfType<JsonObject>().Select(e => ((string)e["host"]!, (int)e["port"]!))]
                    : [];
                SetState(BrokerState.Connected, null);
                break;

            case "shares" when m["shares"] is JsonArray arr:
                Shares = [.. arr.OfType<JsonObject>().Select(s => new InstanceShare(
                    (string)s["share_id"]!, (int?)s["provider"]?["device_id"] ?? 0, (string?)s["provider"]?["name"] ?? "",
                    (string?)s["stick_id"] ?? "", (string?)s["name"] ?? "", (string?)s["tuner"] ?? "",
                    (string?)s["state"] ?? "", (bool?)s["online"] ?? false))];
                break;

            case "stream_request":
                OnStreamRequest((string)m["session"]!, (string?)m["stick_id"] ?? "", (string?)m["from"]?["name"] ?? "?");
                break;

            case "session":
                OnSession(m);
                break;

            case "punch" when Find(m) is { } s:
                s.Punch.TrySetResult(m);
                break;

            case "lease" when Find(m) is { } s:
                s.LeaseUntil = Environment.TickCount64 + 1000L * ((int?)m["ttl"] ?? 60);
                break;

            case "revoke" when Find(m) is { } s:
                s.End($"revoked: {(string?)m["reason"]}", notifyBroker: false);
                break;

            case "error":
                OnError(m);
                break;
        }
    }

    private Session? Find(JsonObject m) => (string?)m["session"] is { } id && _sessions.TryGetValue(id, out var s) ? s : null;

    private void OnError(JsonObject m)
    {
        var code = (string?)m["code"] ?? "ERROR";
        var text = ErrorText(code, (string?)m["provider_code"], (string?)m["message"], m["nat"] as JsonObject);
        var failed = new RemoteOpenException(text);
        if ((string?)m["req"] is { } req && _pending.TryRemove(req, out var tcs)) tcs.TrySetException(failed);
        if (Find(m) is { } s)
        {
            s.Failure = failed;
            s.End(text, notifyBroker: false);
        }
        if (m["req"] is null && m["session"] is null)
            _log.LogWarning("rtl.over.internet: {Code} {Message}", code, (string?)m["message"]);
    }

    private static string ErrorText(string code, string? providerCode, string? message, JsonObject? nat) => code switch
    {
        "NOT_FOUND" => "Freigabe nicht (mehr) vorhanden",
        "PEER_OFFLINE" => "Anbieter ist offline",
        "INSTANCE_DISABLED" => "Instanz deaktiviert",
        "LIMIT_REACHED" => "Zu viele gleichzeitige Sitzungen",
        "REJECTED" when providerCode == "BUSY" => $"Remote stick {message}",
        "REJECTED" => $"Anbieter lehnt ab: {providerCode} {message}",
        "TIMEOUT" => "Anbieter antwortet nicht",
        "DIRECT_CONNECTION_FAILED" =>
            $"Direkte Verbindung nicht möglich (NAT hier {nat?["client"] ?? "?"}, dort {nat?["server"] ?? "?"}). {message}",
        _ => $"{code} {message}",
    };

    // ---------------------------------------------------------------- sessions

    private sealed class Session(string id, BrokerClient owner)
    {
        public string Id { get; } = id;
        public Guid Guid { get; } = Guid.Parse(id);
        public string Role = "";
        public byte[] Key = [];
        public string PeerName = "";
        public string PeerFp = "";
        public string StickId = "";
        public long LeaseUntil = Environment.TickCount64 + 60_000;
        public readonly TaskCompletionSource<JsonObject> Punch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Cts = new();
        public RemoteOpenException? Failure;
        private int _ended;

        /// <summary>Ends the session once: stops punching/streaming, optionally tells the broker (stream_end).</summary>
        public void End(string reason, bool notifyBroker)
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return;
            owner._sessions.TryRemove(Id, out _);
            owner._log.LogInformation("Session {Session} ({Role}, {Peer}) ended: {Reason}", Id[..8], Role, PeerName, reason);
            Punch.TrySetCanceled();
            try { Cts.Cancel(); } catch (ObjectDisposedException) { }
            if (notifyBroker)
                _ = owner.TrySendAsync(new JsonObject { ["type"] = "stream_end", ["session"] = Id, ["bytes"] = 0, ["reason"] = reason });
        }
    }

    private void EndAllSessions(string reason)
    {
        foreach (var s in _sessions.Values) s.End(reason, notifyBroker: false);
        foreach (var (req, tcs) in _pending)
            if (_pending.TryRemove(req, out _)) tcs.TrySetException(new RemoteOpenException("Verbindung zu rtl.over.internet unterbrochen"));
    }

    private void OnStreamRequest(string sessionId, string stickId, string from)
    {
        var shared = _provider.Shares().Any(s => s.StickId == stickId) && _provider.FindDevice(stickId) is not null;
        if (!shared)
        {
            _ = TrySendAsync(new JsonObject
            {
                ["type"] = "stream_reject", ["session"] = sessionId, ["code"] = "NOT_FOUND", ["message"] = "Stick not shared",
            });
            return;
        }
        // Busy is reported inside the stream (BUSY names the owner), like for a local client.
        _sessions[sessionId] = new Session(sessionId, this) { Role = "server", StickId = stickId, PeerName = from };
        _ = TrySendAsync(new JsonObject { ["type"] = "stream_accept", ["session"] = sessionId });
    }

    private void OnSession(JsonObject m)
    {
        var id = (string)m["session"]!;
        var role = (string?)m["role"];
        var s = role == "client" ? _sessions.GetOrAdd(id, _ => new Session(id, this)) : Find(m);
        if (s is null) return;
        s.Role = role ?? "";
        s.Key = Convert.FromBase64String((string)m["punch_key"]!);
        s.PeerName = (string?)m["peer"]?["name"] ?? s.PeerName;
        s.PeerFp = (string?)m["peer"]?["cert_fp"] ?? "";
        s.LeaseUntil = Environment.TickCount64 + 1000L * ((int?)m["lease"] ?? 60);
        _ = LeaseWatchAsync(s);

        if (role == "client")
        {
            if ((string?)m["req"] is { } req && _pending.TryRemove(req, out var tcs)) tcs.TrySetResult(s);
            else s.End("no pending request", notifyBroker: true);
        }
        else if (role == "server")
        {
            _ = ProviderSessionAsync(s);
        }
    }

    private static async Task LeaseWatchAsync(Session s)
    {
        try
        {
            while (!s.Cts.IsCancellationRequested)
            {
                await Task.Delay(1000, s.Cts.Token);
                if (Environment.TickCount64 > s.LeaseUntil) s.End("lease expired", notifyBroker: true);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Candidates → punch → result. Returns the peer endpoint (or null) and the still open socket.</summary>
    private async Task<(IPEndPoint? Winner, SessionSocket Socket)> PunchAsync(Session s, CancellationToken ct)
    {
        var socket = SessionSocket.Bind();
        try
        {
            var g = await socket.GatherAsync(s.Guid, s.Key, _rendezvous, GatherTimeout, ct);
            _log.LogInformation("Session {Session}: candidates {Candidates}, NAT {Nat}", s.Id[..8], string.Join(" ", g.Candidates), g.Nat);
            var cand = new JsonArray();
            foreach (var c in g.Candidates) cand.Add(c);
            await SendAsync(new JsonObject { ["type"] = "candidates", ["session"] = s.Id, ["candidates"] = cand, ["nat"] = g.Nat });

            var punch = await s.Punch.Task.WaitAsync(PunchWait, ct);
            var peer = (punch["peer_candidates"] as JsonArray ?? []).Select(c => SessionSocket.Parse((string?)c ?? ""))
                .OfType<IPEndPoint>().ToList();
            var startAt = DateTimeOffset.FromUnixTimeMilliseconds((long?)punch["start_at"] ?? 0);
            var winner = await socket.PunchAsync(s.Guid, s.Key, peer, startAt, ct);
            await SendAsync(new JsonObject
            {
                ["type"] = "punch_result", ["session"] = s.Id, ["ok"] = winner is not null,
                ["path"] = winner is null ? null : SessionSocket.Format(winner),
            });
            _log.LogInformation("Session {Session}: punch {Result}", s.Id[..8], winner is null ? "failed" : SessionSocket.Format(winner));
            return (winner, socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------- user side

    /// <summary>Opens a session to a shared stick and returns the direct QUIC connection.</summary>
    public async Task<RemoteLink> OpenStreamAsync(string shareId, CancellationToken ct)
    {
        if (State != BrokerState.Connected) throw new RemoteOpenException(Message ?? "Nicht mit rtl.over.internet verbunden");
        var req = "r" + Interlocked.Increment(ref _req);
        var tcs = new TaskCompletionSource<Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[req] = tcs;
        Session s;
        try
        {
            await SendAsync(new JsonObject { ["type"] = "open_stream", ["req"] = req, ["share_id"] = shareId });
            s = await tcs.Task.WaitAsync(OpenWait, ct);
        }
        catch (TimeoutException)
        {
            throw new RemoteOpenException("rtl.over.internet antwortet nicht");
        }
        finally
        {
            _pending.TryRemove(req, out _);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, s.Cts.Token);
        try
        {
            var (winner, socket) = await PunchAsync(s, linked.Token);
            using (socket)
            {
                if (winner is null)
                {
                    // the broker follows up with DIRECT_CONNECTION_FAILED and the NAT types of both sides
                    try { await Task.Delay(TimeSpan.FromSeconds(3), s.Cts.Token); } catch (OperationCanceledException) { }
                    throw s.Failure ?? new RemoteOpenException("Direkte Verbindung nicht möglich (Hole-Punching ohne Antwort)");
                }
            }
            var local = new IPEndPoint(winner.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any,
                socket.LocalPort);
            var conn = await QuicConnection.ConnectAsync(RemoteProtocol.ClientOptions(winner, local, _identity, s.PeerFp), linked.Token);
            var link = new RemoteLink(conn, () =>
            {
                s.End("client left", notifyBroker: true);
                return ValueTask.CompletedTask;
            });
            // revoke / lease expiry / broker gone: drop the connection, the device manager reconnects
            s.Cts.Token.Register(() => _ = link.DisposeAsync().AsTask());
            return link;
        }
        catch (Exception ex) when (ex is OperationCanceledException && s.Failure is not null)
        {
            throw s.Failure;
        }
        catch (Exception ex) when (ex is TimeoutException)
        {
            s.End("punch timeout", notifyBroker: true);
            throw new RemoteOpenException("Verbindungsaufbau über rtl.over.internet abgebrochen (Zeitüberschreitung)");
        }
        catch (Exception ex) when (ex is not RemoteOpenException and not OperationCanceledException)
        {
            s.End($"setup failed: {ex.Message}", notifyBroker: true);
            throw;
        }
    }

    // ---------------------------------------------------------------- provider side

    private async Task ProviderSessionAsync(Session s)
    {
        var ct = s.Cts.Token;
        ShareHost? host = null;
        try
        {
            var (winner, socket) = await PunchAsync(s, ct);
            var port = socket.LocalPort;
            socket.Dispose();
            if (winner is null) return; // broker sends DIRECT_CONNECTION_FAILED and ends the session

            var shared = _provider.Shares().Select(x => x.StickId).ToHashSet();
            var name = $"{s.PeerName} via {InstanceName}";
            host = new ShareHost(new IPEndPoint(IPAddress.IPv6Any, port), _identity,
                cert => RemoteProtocol.FingerprintMatches(cert, s.PeerFp) ? new ShareHost.PeerGrant(name, shared.Contains) : null,
                _provider.FindDevice, _options.Version, _log);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.ConnectionClosed += () => closed.TrySetResult();
            await host.StartAsync();

            // first connection must come soon; the session ends with the (last) connection
            var first = await Task.WhenAny(closed.Task, Task.Delay(FirstConnectionWait, ct));
            if (first != closed.Task && host.Connections == 0)
            {
                s.End("peer did not connect", notifyBroker: true);
                return;
            }
            await closed.Task.WaitAsync(ct);
            s.End("peer disconnected", notifyBroker: true);
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { s.End("punch timeout", notifyBroker: true); }
        catch (Exception ex) when (ex is QuicException or SocketException or WebSocketException or InvalidOperationException)
        {
            s.End($"provider error: {ex.Message}", notifyBroker: true);
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        EndAllSessions("shutdown");
        if (_ws is { State: WebSocketState.Open } ws)
        {
            try
            {
                using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", t.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        }
        if (_run is not null)
        {
            try { await _run; } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}

/// <summary>User side: a stick shared in an instance, reached through the broker.</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class BrokerConnector(Func<BrokerClient?> client, string shareId) : IRemoteConnector
{
    /// <summary>share_id is "&lt;provider device id&gt;:&lt;stick id&gt;".</summary>
    public string Share => shareId[(shareId.IndexOf(':') + 1)..];
    public string Describe => $"share {shareId}";
    public TimeSpan ConnectTimeout => TimeSpan.FromSeconds(25);

    public Task<RemoteLink> ConnectAsync(CancellationToken ct) =>
        client() is { } c ? c.OpenStreamAsync(shareId, ct) : throw new RemoteOpenException("Instanz nicht eingerichtet");
}
