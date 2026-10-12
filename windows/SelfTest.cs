using System.Diagnostics;

using System.IO.Pipes;

using System.Net;

using System.Net.Sockets;

using System.Reflection;

using System.Security.Principal;

using System.Text.Json;

namespace HyperFramesChinese;


internal static class SelfTest

{

    public static int Run()

    {

        var results = new List<object>();
        bool failed = false;

        void Check(string name, Action action)
        {
            try
            {
                Console.Error.WriteLine("START " + name);
                action();
                Console.Error.WriteLine("PASS " + name);
                results.Add(new
                {
                    name,
                    passed = true
                }
               );

            }
            catch (Exception e)
            {
                failed = true;
                results.Add(new
                {
                    name,
                    passed = false,
                    error = e.Message
                }
               );

            }

        }

        void Assert(bool value)
        {
            if (!value) throw new Exception("断言未通过");

        }

        void Rejected(Action action)
        {
            try
            {
                action();

            }
            catch
            {
                return;

            }
            throw new Exception("未拒绝不可信监听端口");

        }

        using var current = Process.GetCurrentProcess();
        string path = current.MainModule!.FileName!;

        Check("native-command-line", () => Assert(!string.IsNullOrWhiteSpace(Native.CommandLine(current.Id)) && Native.CommandLine(current.Id).Contains("--self-test")));

        if (Socket.OSSupportsIPv6)

        {

            Check("ipv6-loopback-owner", () =>
{
    var l = new TcpListener(IPAddress.IPv6Loopback, 0); l.Server.DualMode = false; l.Start(); try
    {
        Native.VerifyListeners(((IPEndPoint)l.LocalEndpoint).Port, current.Id, path);
    }
    finally
    {
        l.Stop();
    }

}
);

            Check("reject-ipv6-wildcard", () =>
{
    var l = new TcpListener(IPAddress.IPv6Any, 0); l.Server.DualMode = false; l.Start(); try
    {
        Rejected(() => Native.VerifyListeners(((IPEndPoint)l.LocalEndpoint).Port, current.Id, path));
    }
    finally
    {
        l.Stop();
    }

}
);


        }

        Check("loopback-owner", () =>
{
    var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); try
    {
        Native.VerifyListeners(((IPEndPoint)l.LocalEndpoint).Port, current.Id, path);
    }
    finally
    {
        l.Stop();
    }

}
);

        Check("reject-wildcard", () =>
{
    var l = new TcpListener(IPAddress.Any, 0); l.Start(); try
    {
        Rejected(() => Native.VerifyListeners(((IPEndPoint)l.LocalEndpoint).Port, current.Id, path));
    }
    finally
    {
        l.Stop();
    }

}
);

        Check("reject-other-owner", () =>
{
    var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); try
    {
        Rejected(() => Native.VerifyListeners(((IPEndPoint)l.LocalEndpoint).Port, 0, path));
    }
    finally
    {
        l.Stop();
    }

}
);

        Check("reject-wrong-executable", () =>
{
    var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); try
    {
        Rejected(() => Native.VerifyListeners(((IPEndPoint)l.LocalEndpoint).Port, current.Id, path + ".wrong"));
    }
    finally
    {
        l.Stop();
    }

}
);

        Check("page-policy", () =>
{
    Assert(TargetPolicy.Page("page", "HyperFrames", "http://127.0.0.1:51884/")); Assert(TargetPolicy.Page("page", "HyperFrames", "http://[::1]:51884/index.html")); Assert(!TargetPolicy.Page("iframe", "HyperFrames", "http://127.0.0.1:51884/")); Assert(!TargetPolicy.Page("page", "HyperFrames", "http://192.168.1.1/")); Assert(!TargetPolicy.Page("page", "HyperFrames", "https://127.0.0.1/")); Assert(!TargetPolicy.Page("page", "HyperFrames", "http://127.0.0.1/projects")); Assert(TargetPolicy.WebSocket("ws://127.0.0.1:61360/devtools/page/abc", 61360)); Assert(!TargetPolicy.WebSocket("ws://127.0.0.1:51884/devtools/page/abc", 61360));
}
);

        Check("embedded-resources", () =>
{
    string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new Exception("资源缺失：" + name); using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    Assert(Read("translator.js").Contains("1.1.0")); using var dict = JsonDocument.Parse(Read("translations.zh-CN.json")); Assert(dict.RootElement.ValueKind == JsonValueKind.Object && dict.RootElement.EnumerateObject().Any()); using var templates = JsonDocument.Parse(Read("dynamic.zh-CN.json")); Assert(templates.RootElement.ValueKind == JsonValueKind.Array && templates.RootElement.GetArrayLength() > 0);
}
);

        Check("current-user-mutex-pipe", () =>
{
    string name = "HyperFramesChinese_selftest_" + WindowsIdentity.GetCurrent().User!.Value.Replace('-', '_') + "_" + Guid.NewGuid().ToString("N"); using var first = new Mutex(true, @"Local\" + name, out bool created); Assert(created); using var second = new Mutex(false, @"Local\" + name, out bool duplicate); Assert(!duplicate); using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); var wait = server.WaitForConnectionAsync(); using var client = new NamedPipeClientStream(".", name, PipeDirection.Out); client.Connect(2000); client.WriteByte(1); Assert(wait.Wait(2000)); Assert(server.ReadByte() == 1);
}
);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            version = "1.1.0",
            passed = !failed,
            results
        }
));
        return failed ? 1 : 0;


    }


}
