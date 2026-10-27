using CommunityToolkit.Mvvm.Messaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Skua.Core.Flash;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Skua.App.Avalonia.Flash;

/// <summary>
/// PoC: an <see cref="IFlashUtil"/> that drives skua.swf running inside the patched
/// Ruffle Android app ("Ruffle Skua PoC") over TCP, instead of a local Flash ActiveX.
///
/// Enable by setting the environment variable SKUA_REMOTE_FLASH to the phone address,
/// e.g. <c>192.168.1.23</c> or <c>192.168.1.23:5150</c>, before starting Skua.
///
/// Wire format (one JSON object per line):
///   -> {"id":1,"fn":"isNull","args":["world"]}
///   &lt;- {"id":1,"r":"true"}
///   &lt;- {"ev":"pext","args":[["{...}"]]}
///
/// Values are converted so Skua.Core sees exactly what the ActiveX host would give it
/// (including its XML quirks), so nothing above this class needs to change.
/// </summary>
public sealed class RemoteFlashUtil : IFlashUtil
{
    public const string EnvVar = "SKUA_REMOTE_FLASH";
    public const int DefaultPort = 5150;

    /// <summary>The configured endpoint, or null when remote Flash is not enabled.</summary>
    public static string? ConfiguredEndpoint
    {
        get
        {
            string? v = Environment.GetEnvironmentVariable(EnvVar);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
    }

    public static TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(10);

    private readonly IMessenger? _messenger;
    private readonly Lazy<IScriptManager>? _lazyManager;
    private readonly string _host;
    private readonly int _port;

    private readonly ConcurrentDictionary<long, TaskCompletionSource<JToken?>> _pending = new();
    private readonly BlockingCollection<(string Name, object[] Args)> _events = new();
    private readonly object _writeLock = new();
    private readonly CancellationTokenSource _cts = new();

    private TcpClient? _client;
    private StreamWriter? _writer;
    private long _nextId;
    private bool _started;
    private volatile bool _connected;

    public event FlashCallHandler? FlashCall;

    /// <summary>True while a phone is connected.</summary>
    public bool IsConnected => _connected;

    public RemoteFlashUtil(IMessenger messenger, Lazy<IScriptManager> manager)
        : this(ConfiguredEndpoint ?? "127.0.0.1", messenger, manager)
    {
    }

    public RemoteFlashUtil(string endpoint, IMessenger? messenger = null, Lazy<IScriptManager>? manager = null)
    {
        _messenger = messenger;
        _lazyManager = manager;
        (_host, _port) = ParseEndpoint(endpoint);
    }

    private static (string, int) ParseEndpoint(string endpoint)
    {
        int colon = endpoint.LastIndexOf(':');
        if (colon > 0 && int.TryParse(endpoint[(colon + 1)..], out int port))
            return (endpoint[..colon], port);
        return (endpoint, DefaultPort);
    }

    public void InitializeFlash()
    {
        if (_started)
            return;
        _started = true;

        // Events are raised on their own thread: handlers call back into Flash
        // synchronously, which needs the reader thread to stay free.
        new Thread(DispatchEvents) { IsBackground = true, Name = "RemoteFlash Events" }.Start();
        new Thread(ConnectLoop) { IsBackground = true, Name = "RemoteFlash Connection" }.Start();
    }

    private void ConnectLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                Trace.WriteLine($"[RemoteFlash] connecting to {_host}:{_port} ...");
                var client = new TcpClient { NoDelay = true };
                client.Connect(_host, _port);
                var stream = client.GetStream();
                lock (_writeLock)
                {
                    _client = client;
                    _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                }
                _connected = true;
                Trace.WriteLine($"[RemoteFlash] connected to {_host}:{_port}");

                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                string? line;
                while ((line = reader.ReadLine()) != null)
                    HandleLine(line);
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                Trace.WriteLine($"[RemoteFlash] connection error: {ex.Message}");
            }
            finally
            {
                _connected = false;
                lock (_writeLock)
                {
                    try { _client?.Dispose(); } catch { }
                    _client = null;
                    _writer = null;
                }
                foreach (var kv in _pending)
                    if (_pending.TryRemove(kv.Key, out var tcs))
                        tcs.TrySetResult(null);
            }

            if (!_cts.IsCancellationRequested)
                Thread.Sleep(2000);
        }
    }

    private void HandleLine(string line)
    {
        JObject msg;
        try
        {
            msg = JObject.Parse(line);
        }
        catch (JsonException)
        {
            return;
        }

        if (msg.TryGetValue("id", out JToken? idTok) && idTok.Type == JTokenType.Integer)
        {
            if (_pending.TryRemove(idTok.Value<long>(), out var tcs))
                tcs.TrySetResult(msg["r"]);
            return;
        }

        string? ev = msg.Value<string>("ev");
        if (string.IsNullOrEmpty(ev) || ev.StartsWith("__"))
            return;

        // The ActiveX host parses <arguments> as a single element, so handlers always
        // get exactly one argument: the concatenated text of everything passed.
        string text = ConcatText(msg["args"]);
        _events.Add((ev, new object[] { text }));
    }

    private void DispatchEvents()
    {
        try
        {
            foreach (var (name, args) in _events.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    FlashCall?.Invoke(name, args);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[RemoteFlash] handler for '{name}' threw: {ex}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }

    public string? Call(string function, params object[] args)
    {
        return Call<string>(function, args);
    }

    public T? Call<T>(string function, params object[] args)
    {
        try
        {
            object? o = Call(function, typeof(T), args);
            return o is T value ? value : default;
        }
        catch
        {
            return default;
        }
    }

    public object? Call(string function, Type type, params object[] args)
    {
        if (_lazyManager is not null && _lazyManager.Value.ShouldExit && Thread.CurrentThread.Name == "Script Thread")
            _lazyManager.Value.ScriptCts?.Token.ThrowIfCancellationRequested();

        try
        {
            if (!_connected)
                return null;

            long id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            var request = new JObject
            {
                ["id"] = id,
                ["fn"] = function,
                ["args"] = new JArray(args.Select(ToJson)),
            };

            lock (_writeLock)
            {
                if (_writer is null)
                {
                    _pending.TryRemove(id, out _);
                    return null;
                }
                _writer.WriteLine(request.ToString(Formatting.None));
            }

            if (!tcs.Task.Wait(CallTimeout))
            {
                _pending.TryRemove(id, out _);
                Trace.WriteLine($"[RemoteFlash] call '{function}' timed out");
                return null;
            }

            string? raw = ToActiveXResultText(tcs.Task.Result);
            if (raw is null)
                return null;
            return Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
        }
        catch (Exception e)
        {
            if (function != "isKicked")
                _messenger?.Send<FlashErrorMessage>(new(e, function, args));
            return null;
        }
    }

    /// <summary>
    /// Desktop returns <c>XElement.Parse(result).FirstNode.ToString()</c>, i.e. the text of
    /// &lt;string&gt;/&lt;number&gt; re-escaped as XML (so "&amp;" stays "&amp;amp;"), and nothing for
    /// &lt;true/&gt;, &lt;false/&gt;, &lt;null/&gt;. All skua.swf callbacks return strings.
    /// </summary>
    internal static string? ToActiveXResultText(JToken? r)
    {
        if (r is null)
            return null;
        return r.Type switch
        {
            JTokenType.String => new XText(r.Value<string>()!).ToString(),
            JTokenType.Integer or JTokenType.Float => FormatNumber(r),
            _ => null,
        };
    }

    /// <summary>Mirror of FlashUtil.ToFlashXml's type mapping.</summary>
    internal static JToken ToJson(object? o)
    {
        switch (o)
        {
            case null:
                return JValue.CreateNull();
            case bool b:
                return new JValue(b);
            case double or float or long or int:
                double d = Convert.ToDouble(o, CultureInfo.InvariantCulture);
                return d == Math.Floor(d) && Math.Abs(d) < 9e15 ? new JValue((long)d) : new JValue(d);
            case ExpandoObject:
                var obj = new JObject();
                foreach (KeyValuePair<string, object?> kvp in (IDictionary<string, object?>)o)
                    obj[kvp.Key] = ToJson(kvp.Value);
                return obj;
            case Array arr:
                return new JArray(arr.Cast<object?>().Select(ToJson));
            default:
                return new JValue(o.ToString());
        }
    }

    /// <summary>Equivalent of XElement.Value over the Flash XML for these arguments.</summary>
    internal static string ConcatText(JToken? token)
    {
        var sb = new StringBuilder();
        void Walk(JToken? t)
        {
            switch (t)
            {
                case null:
                    return;
                case JArray a:
                    foreach (var c in a) Walk(c);
                    break;
                case JObject o:
                    foreach (var p in o.Properties()) Walk(p.Value);
                    break;
                case JValue v when v.Type == JTokenType.String:
                    sb.Append(v.Value<string>());
                    break;
                case JValue v when v.Type is JTokenType.Integer or JTokenType.Float:
                    sb.Append(FormatNumber(v));
                    break;
            }
        }
        Walk(token);
        return sb.ToString();
    }

    private static string FormatNumber(JToken n)
    {
        return n.Type == JTokenType.Integer
            ? n.Value<long>().ToString(CultureInfo.InvariantCulture)
            : n.Value<double>().ToString("R", CultureInfo.InvariantCulture);
    }

    public object FromFlashXml(XElement el)
    {
        switch (el.Name.ToString())
        {
            case "number":
                return int.TryParse(el.Value, out int i) ? i : float.TryParse(el.Value, out float f) ? f : 0;
            case "true":
                return true;
            case "false":
                return false;
            case "null":
                return null!;
            case "array":
                return el.Elements().Select(FromFlashXml).ToArray();
            case "object":
                IDictionary<string, object?> d = new ExpandoObject();
                foreach (var e in el.Elements())
                    d[e.Attribute("id")!.Value] = FromFlashXml(e.Elements().First());
                return d;
            default:
                return el.Value;
        }
    }

    public IFlashObject<T> CreateFlashObject<T>(string path)
    {
        return new FlashObject<T>(Call<int>("lnkCreate", path), this);
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_writeLock)
        {
            try { _client?.Dispose(); } catch { }
        }
        _events.CompleteAdding();
    }
}
