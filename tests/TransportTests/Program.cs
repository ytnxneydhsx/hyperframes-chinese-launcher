using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyperFramesChinese;

if (args.Length == 3 && args[0] == "--probe") {
    try {
        int debugPort = int.Parse(args[1]);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var pages = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{debugPort}/json/list"));
        var page = pages.RootElement.EnumerateArray().First(p => TargetPolicy.Page(p.GetProperty("type").GetString(), p.GetProperty("title").GetString(), p.GetProperty("url").GetString()));
        string ws = page.GetProperty("webSocketDebuggerUrl").GetString()!;
        if (!TargetPolicy.WebSocket(ws, debugPort)) throw new Exception("untrusted debugger target");
        using var connection = new Cdp();
        await connection.Connect(ws);
        var fixture = await connection.Evaluate(await File.ReadAllTextAsync(args[2]));
        if (!fixture.GetProperty("passed").GetBoolean()) throw new Exception("UI fixture failed");
        var status = await connection.Evaluate("window.__HFZH__.status()");
        bool passed = status.GetProperty("version").GetString() == "1.1.0"
            && status.GetProperty("enabled").GetBoolean()
            && status.GetProperty("dictionarySize").GetInt32() > 2500
            && status.GetProperty("translatedTextNodes").GetInt32() > 0;
        Console.WriteLine(JsonSerializer.Serialize(new { passed, version = "1.1.0", uiFixture = true,
            dictionarySize = status.GetProperty("dictionarySize").GetInt32(),
            translatedTextNodes = status.GetProperty("translatedTextNodes").GetInt32() }));
        return passed ? 0 : 1;
    } catch (Exception error) { Console.WriteLine(JsonSerializer.Serialize(new { passed = false, error = error.Message })); return 1; }
}
if (args.Length == 2 && args[0] == "--restoration") {
    int debugPort = int.Parse(args[1]);
    using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
    using var pages = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{debugPort}/json/list"));
    var page = pages.RootElement.EnumerateArray().First(p => TargetPolicy.Page(p.GetProperty("type").GetString(), p.GetProperty("title").GetString(), p.GetProperty("url").GetString()));
    using var connection = new Cdp();
    await connection.Connect(page.GetProperty("webSocketDebuggerUrl").GetString()!);
    var status = await connection.Evaluate("window.__HFZH__ ? window.__HFZH__.status() : ({enabled:false})");
    bool passed = !status.GetProperty("enabled").GetBoolean();
    Console.WriteLine(JsonSerializer.Serialize(new { passed, restored = passed }));
    return passed ? 0 : 1;
}

var results = new List<string>();
void Assert(bool condition, string name) { if (!condition) throw new Exception(name); results.Add(name); }
try {
    Assert(TargetPolicy.Page("page", "HyperFrames", "http://127.0.0.1:51884/"), "different-renderer-port");
    Assert(!TargetPolicy.Page("iframe", "HyperFrames", "http://127.0.0.1:51884/"), "reject-preview-frame");
    Assert(!TargetPolicy.Page("page", "HyperFrames", "http://192.168.1.1/"), "reject-LAN-page");
    Assert(!TargetPolicy.Page("page", "HyperFrames", "http://user:password@127.0.0.1/"), "reject-credentials");
    Assert(!TargetPolicy.WebSocket("ws://127.0.0.1:9/devtools/browser/abc", 9), "reject-browser-target");
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var serving = Task.Run(async () => {
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        await using var stream = peer.GetStream();
        var header = new List<byte>();
        var one = new byte[1];
        while (header.Count < 8192) {
            await stream.ReadExactlyAsync(one, deadline.Token); header.Add(one[0]);
            if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] {13,10,13,10})) break;
        }
        string text = Encoding.ASCII.GetString(header.ToArray());
        string key = text.Split("\r\n").Single(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), deadline.Token);
        using var socket = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
        async Task Send(byte[] data, bool fragmented = false) {
            if (fragmented) { await socket.SendAsync(data.AsMemory(0, 7000), WebSocketMessageType.Text, false, deadline.Token); await socket.SendAsync(data.AsMemory(7000), WebSocketMessageType.Text, true, deadline.Token); }
            else await socket.SendAsync(data.AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
        }
        for (int call = 0; call < 2; call++) {
            using var payload = new MemoryStream(); var buffer = new byte[8192]; ValueWebSocketReceiveResult received;
            do { received = await socket.ReceiveAsync(buffer.AsMemory(), deadline.Token); payload.Write(buffer, 0, received.Count); } while (!received.EndOfMessage);
            using var request = JsonDocument.Parse(payload.ToArray());
            var root = request.RootElement; int id = root.GetProperty("id").GetInt32();
            if (root.GetProperty("method").GetString() != "Runtime.evaluate" || !root.GetProperty("params").GetProperty("awaitPromise").GetBoolean()) throw new Exception("invalid CDP request");
            if (call == 0) {
                await Send(JsonSerializer.SerializeToUtf8Bytes(new { method = "Runtime.executionContextCreated" }));
                await Send(JsonSerializer.SerializeToUtf8Bytes(new { id = id + 500, result = new { result = new { value = new { text = "wrong" } } } }));
                await Send(JsonSerializer.SerializeToUtf8Bytes(new { id, result = new { result = new { value = new { text = "设置", large = new string('x', 20000) } } } }), true);
            } else await Send(JsonSerializer.SerializeToUtf8Bytes(new { id, result = new { exceptionDetails = new { text = "fixture error" } } }));
        }
    }, deadline.Token);
    using var client = new Cdp();
    await client.Connect($"ws://127.0.0.1:{port}/devtools/page/fixture");
    var value = await client.Evaluate("fixture");
    Assert(value.GetProperty("text").GetString() == "设置" && value.GetProperty("large").GetString()!.Length == 20000, "fragmented-response-and-event-filtering");
    bool rejected = false;
    try { await client.Evaluate("throw"); } catch { rejected = true; }
    Assert(rejected, "reject-script-exception");
    await serving; listener.Stop();
    Console.WriteLine(JsonSerializer.Serialize(new { passed = true, checks = results }));
    return 0;
} catch (Exception error) {
    Console.WriteLine(JsonSerializer.Serialize(new { passed = false, checks = results, error = error.ToString() }));
    return 1;
}
