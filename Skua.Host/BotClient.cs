using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace Skua.Host;

/// <summary>
/// Manager side of <see cref="BotController"/>: one connection to one bot, reconnecting on demand.
/// Through a Skua Server the same client reaches a bot on another machine: every request then
/// carries the server's token and the bot's slot.
/// </summary>
public sealed class BotClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string? _token;
    private readonly bool _viaServer;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JObject>> _pending = new();
    private TcpClient? _tcp;
    private StreamWriter? _writer;
    private long _nextId;

    /// <summary>A bot on this device.</summary>
    public BotClient(int slot)
    {
        Slot = slot;
        _host = "127.0.0.1";
        _port = BotController.ControlPort(slot);
    }

    /// <summary>Bot <paramref name="slot"/> of the Skua Server at host:port.</summary>
    public BotClient(string host, int port, string token, int slot)
    {
        Slot = slot;
        _host = host;
        _port = port;
        _token = token;
        _viaServer = true;
    }

    public int Slot { get; }
    public bool Connected => _tcp?.Connected == true;

    private async Task EnsureConnectedAsync(CancellationToken token)
    {
        if (Connected && _writer is not null)
            return;
        await _connectLock.WaitAsync(token);
        try
        {
            if (Connected && _writer is not null)
                return;
            Drop();
            TcpClient tcp = new() { NoDelay = true };
            using (CancellationTokenSource connect = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connect.CancelAfter(8000);
                await tcp.ConnectAsync(_host, _port, connect.Token);
            }
            NetworkStream stream = tcp.GetStream();
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _tcp = tcp;
            StreamReader reader = new(stream, new UTF8Encoding(false));
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        string? line = await reader.ReadLineAsync();
                        if (line is null)
                            break;
                        JObject reply = JObject.Parse(line);
                        if (reply.Value<long?>("id") is long id && _pending.TryRemove(id, out var tcs))
                            tcs.TrySetResult(reply);
                    }
                }
                catch { }
                finally
                {
                    if (ReferenceEquals(_tcp, tcp))
                        Drop();
                }
            });
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private void Drop()
    {
        try { _tcp?.Close(); } catch { }
        _tcp = null;
        _writer = null;
        foreach (var kv in _pending)
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(new IOException("Bot disconnected."));
    }

    /// <summary>Send a command; returns "data" or throws with the bot's error.</summary>
    public async Task<JToken?> SendAsync(string cmd, JObject? args = null, int timeoutMs = 15000, CancellationToken token = default)
    {
        await EnsureConnectedAsync(token);
        long id = Interlocked.Increment(ref _nextId);
        JObject req = args is null ? new JObject() : (JObject)args.DeepClone();
        req["id"] = id;
        req["cmd"] = cmd;
        if (_viaServer)
        {
            req["token"] = _token;
            req["slot"] = Slot;
        }
        TaskCompletionSource<JObject> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await _writer!.WriteLineAsync(req.ToString(Formatting.None));
        }
        catch
        {
            _pending.TryRemove(id, out _);
            Drop();
            throw;
        }
        Task done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, token));
        if (done != tcs.Task)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"Bot #{Slot} didn't answer '{cmd}'.");
        }
        JObject reply = await tcs.Task;
        if (reply.Value<bool?>("ok") != true)
            throw new InvalidOperationException(reply.Value<string>("error") ?? "failed");
        return reply["data"];
    }

    /// <summary>True when the bot answers within the timeout.</summary>
    public async Task<bool> PingAsync(int timeoutMs = 1500)
    {
        try
        {
            using CancellationTokenSource cts = new(timeoutMs);
            return (await SendAsync("ping", null, timeoutMs, cts.Token))?.ToString() == "pong";
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => Drop();
}
