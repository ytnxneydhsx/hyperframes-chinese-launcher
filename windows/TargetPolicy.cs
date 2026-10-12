using System.Net;

namespace HyperFramesChinese;


internal static class TargetPolicy

{

    static bool Loopback(string? text, string scheme, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Scheme != scheme || u.UserInfo.Length != 0 || !IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) || !IPAddress.IsLoopback(ip) || u.Port < 1 || u.Port > 65535) return false;
        uri = u;
        return true;

    }

    public static bool Page(string? type, string? title, string? url) => type == "page" && title == "HyperFrames" && Loopback(url, "http", out var u) && (u!.AbsolutePath is "/" or "/index.html");

    public static bool WebSocket(string? url, int port) => Loopback(url, "ws", out var u) && u!.Port == port && u.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal) && u.AbsolutePath.Length > "/devtools/page/".Length;


}
