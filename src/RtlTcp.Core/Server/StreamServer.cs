using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RtlTcp.Core.Auth;
using RtlTcp.Core.Device;

namespace RtlTcp.Core.Server;

/// <summary>TCP listener for one stick (one port). Accepts any number of connections; the DeviceManager decides who owns the stick.</summary>
public sealed class StreamServer : IAsyncDisposable
{
    private readonly DeviceManager _manager;
    private readonly Func<AccessPolicy> _policy;
    private readonly IStreamUserStore _users;
    private readonly string _version;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Task, byte> _sessions = new();
    private Socket? _listener;
    private Task? _accept;

    /// <param name="policy">Called per connection so changes from the web GUI apply immediately.</param>
    public StreamServer(DeviceManager manager, Func<AccessPolicy> policy, IStreamUserStore users, string version, ILogger log)
    {
        _manager = manager;
        _policy = policy;
        _users = users;
        _version = version;
        _log = log;
    }

    public IPEndPoint? LocalEndPoint => _listener?.LocalEndPoint as IPEndPoint;

    public void Start()
    {
        var cfg = _manager.Config;
        var addr = IPAddress.Parse(cfg.Bind);
        var listener = new Socket(addr.AddressFamily == AddressFamily.InterNetworkV6 || addr.Equals(IPAddress.Any)
            ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        if (listener.AddressFamily == AddressFamily.InterNetworkV6)
        {
            listener.DualMode = true; // IPv4 + IPv6 on one socket
            if (addr.Equals(IPAddress.Any)) addr = IPAddress.IPv6Any;
        }
        listener.Bind(new IPEndPoint(addr, cfg.Port));
        listener.Listen(16);
        _listener = listener;
        _log.LogInformation("{Device}: listening on {EndPoint}", cfg.Name, listener.LocalEndPoint);
        _accept = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener!.AcceptAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex)
            {
                _log.LogWarning("accept failed: {Error}", ex.Message);
                continue;
            }

            var session = new ClientSession(client, _manager, _policy(), _users, _version, _log, ct);
            var task = Task.Run(session.RunAsync, CancellationToken.None);
            _sessions.TryAdd(task, 0);
            _ = task.ContinueWith(t => _sessions.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener?.Dispose();
        if (_accept is not null) await _accept;
        await Task.WhenAll(_sessions.Keys);
        _cts.Dispose();
    }
}
