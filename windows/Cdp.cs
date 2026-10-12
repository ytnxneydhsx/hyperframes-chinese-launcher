using System.Net.WebSockets;

using System.Text.Json;

namespace HyperFramesChinese;


sealed class Cdp : IDisposable

{

    readonly ClientWebSocket socket = new();
    int id;

    public async Task Connect(string url)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await socket.ConnectAsync(new Uri(url), timeout.Token);

    }

    public async Task<JsonElement> Evaluate(string expression)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        int request = ++id;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = request,
            method = "Runtime.evaluate",
            @params = new
            {
                expression,
                returnByValue = true,
                awaitPromise = true
            }

        }
       );
        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[8192];
        while (true)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) throw new Exception("页面连接关闭");
                stream.Write(buffer, 0, result.Count);
                if (stream.Length > 4 * 1024 * 1024) throw new Exception("调试响应过大");

            }
            while (!result.EndOfMessage);
            using var doc = JsonDocument.Parse(stream.ToArray());
            if (!doc.RootElement.TryGetProperty("id", out var reply) || reply.GetInt32() != request) continue;
            if (doc.RootElement.TryGetProperty("error", out _) || doc.RootElement.GetProperty("result").TryGetProperty("exceptionDetails", out _)) throw new Exception("页面脚本执行失败");
            return doc.RootElement.GetProperty("result").GetProperty("result").GetProperty("value").Clone();

        }

    }

    public void Dispose() => socket.Dispose();


}
