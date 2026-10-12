using System.Diagnostics;

using System.IO.Pipes;

using System.Net;

using System.Net.Sockets;

using System.Net.WebSockets;

using System.Reflection;

using System.Runtime.InteropServices;

using System.Security.Principal;

using System.Text;

using System.Text.Json;

using Microsoft.Win32;

namespace HyperFramesChinese;

static class Program

{

    [STAThread]
    static int Main(string[] args)

    {

        if (args.Contains("--self-test")) return SelfTest.Run();

        ApplicationConfiguration.Initialize();

        var suffix = WindowsIdentity.GetCurrent().User!.Value.Replace('-', '_');

        using var mutex = new Mutex(true, @"Local\HyperFramesChinese_" + suffix, out bool owner);

        if (!owner)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", "HyperFramesChinese_" + suffix, PipeDirection.Out);
                pipe.Connect(1500);
                pipe.WriteByte(args.Contains("--quit") ? (byte)2 : (byte)1);
                pipe.Flush();

            }
            catch
            {

            }
            return 0;

        }

        if (args.Contains("--quit")) return 0;

        using var context = new Launcher("HyperFramesChinese_" + suffix);
        Application.Run(context);
        return 0;

    }

}

sealed class Launcher : ApplicationContext

{

    const string Version = "1.1.0";

    readonly NotifyIcon tray;
    readonly Form dispatcher = new()
    {
        ShowInTaskbar = false,
        WindowState = FormWindowState.Minimized
    }
   ;

    readonly System.Windows.Forms.Timer timer = new()
    {
        Interval = 3000
    }
;

    readonly CancellationTokenSource lifetime = new();
    readonly HttpClient http = new(new HttpClientHandler
    {
        UseProxy = false
    }
   )
    {
        Timeout = TimeSpan.FromSeconds(2)
    }
   ;

    readonly string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperFramesChinese");

    readonly Dictionary<string, Cdp> sessions = new();
    string? executable;
    Process? app;
    int port;
    bool enabled = true, exiting, connected;
    readonly SemaphoreSlim commands = new(1, 1);
    string status = "正在查找 HyperFrames";
    int count;

    readonly ToolStripMenuItem state = new("状态：正在启动")
    {
        Enabled = false
    }
, toggle = new("中文已开启（点击恢复英文）");

    public Launcher(string pipeName)

    {

        _ = dispatcher.Handle;

        using var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HyperFrames.ico");

        tray = new NotifyIcon
        {
            Icon = iconStream != null ? new Icon(iconStream) : SystemIcons.Application,
            Text = "HyperFrames 中文启动器",
            Visible = true
        }
;

        var menu = new ContextMenuStrip();
        menu.Items.Add(state);
        menu.Items.Add("打开 HyperFrames", null, async (_, _) => await Guard(Open));
        menu.Items.Add("重新连接", null, async (_, _) => await Guard(Open));
        menu.Items.Add("选择 HyperFrames.exe", null, async (_, _) => await Guard(async () =>
       {
           if (Choose() && (app == null || app.HasExited)) await Open();
       }
       ));
        menu.Items.Add(toggle);
        toggle.Click += async (_, _) => await Guard(async () =>
       {
           enabled = !enabled; await Poll();
       }
       );
        menu.Items.Add("普通模式重开", null, async (_, _) => await Guard(() => Normal(false)));
        menu.Items.Add("退出中文启动器", null, async (_, _) => await Guard(() => Normal(true)));
        tray.ContextMenuStrip = menu;

        tray.DoubleClick += async (_, _) => await Guard(Open);
        timer.Tick += async (_, _) => await Guard(Poll, true);
        timer.Start();

        _ = Listen(pipeName);
        dispatcher.BeginInvoke(new Action(async () => await Guard(Open)));

    }

    async Task Listen(string name)
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var p = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await p.WaitForConnectionAsync(lifetime.Token);
                var b = new byte[1];
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                await p.ReadExactlyAsync(b, readTimeout.Token);
                if (b[0] == 1) dispatcher.BeginInvoke(new Action(async () => await Guard(Open)));
                else if (b[0] == 2) dispatcher.BeginInvoke(new Action(async () => await Guard(() => Normal(true))));

            }
            catch (OperationCanceledException)
            {
                if (lifetime.IsCancellationRequested) break;

            }
            catch
            {
                await Task.Delay(500);

            }

        }

    }

    async Task Guard(Func<Task> action, bool timerRequest = false)
    {
        if (timerRequest)
        {
            if (!await commands.WaitAsync(0)) return;

        }
        else await commands.WaitAsync();
        try
        {
            if (exiting) return;
            await action();

        }
        catch (Exception e)
        {
            connected = false;
            count = 0;
            status = "连接未完成：" + e.Message;

        }
        finally
        {
            commands.Release();
            if (!exiting) Update();

        }

    }

    void Update()
    {
        state.Text = "状态：" + status;
        toggle.Text = enabled ? "中文已开启（点击恢复英文）" : "中文已关闭（点击启用中文）";
        tray.Text = enabled ? "HyperFrames 中文启动器 · 中文" : "HyperFrames 中文启动器 · 英文";
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "state.json"), JsonSerializer.Serialize(new
            {
                version = Version,
                enabled,
                status = connected ? "connected" : "disconnected",
                translatedCount = count,
                launcherPID = Environment.ProcessId,
                targetPID = app?.Id,
                pid = app?.Id,
                port
            }
           ));

        }
        catch
        {

        }

    }

    bool Valid(string? path)
    {
        try
        {
            if (path == null || !File.Exists(path) || !Path.GetFileName(path).Equals("HyperFrames.exe", StringComparison.OrdinalIgnoreCase)) return false;
            var f = FileVersionInfo.GetVersionInfo(path);
            string label = f.ProductName + " " + f.CompanyName;
            return label.Contains("HyperFrames", StringComparison.OrdinalIgnoreCase) || label.Contains("HeyGen", StringComparison.OrdinalIgnoreCase);

        }
        catch
        {
            return false;

        }

    }

    bool Choose()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择已安装的 HyperFrames.exe",
            Filter = "HyperFrames.exe|HyperFrames.exe",
            CheckFileExists = true
        }
       ;
        if (dialog.ShowDialog() != DialogResult.OK) return false;
        if (!Valid(dialog.FileName))
        {
            MessageBox.Show("文件名和产品信息未通过 HyperFrames 校验。", "中文启动器");
            return false;

        }
        string selected = dialog.FileName;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "config.json"), JsonSerializer.Serialize(new
        {
            executable = selected
        }
       ));
        if (app == null || app.HasExited) executable = selected;
        else status = "已保存下次应用位置，当前连接继续保留";
        return true;

    }

    string? Discover()

    {

        foreach (var p in Process.GetProcessesByName("HyperFrames"))
        {
            try
            {
                if (Native.IsMainProcess(p) && Valid(p.MainModule?.FileName)) return p.MainModule!.FileName;

            }
            catch
            {

            }

        }

        try
        {
            var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "config.json"))).RootElement.GetProperty("executable").GetString();
            if (Valid(saved)) return saved;

        }
        catch
        {

        }

        foreach (var hive in new[]
{
 RegistryHive.CurrentUser, RegistryHive.LocalMachine
}
) foreach (var view in new[]
{
 RegistryView.Registry64, RegistryView.Registry32
}
)

        {

            try

            {

                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var paths = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\HyperFrames.exe");
                var path = paths?.GetValue("") as string;
                if (Valid(path)) return path;

                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall != null) foreach (var key in uninstall.GetSubKeyNames())
                {
                    using var item = uninstall.OpenSubKey(key);
                    if ((item?.GetValue("DisplayName") as string ?? "").Contains("HyperFrames", StringComparison.OrdinalIgnoreCase))
                    {
                        path = Path.Combine(item?.GetValue("InstallLocation") as string ?? "", "HyperFrames.exe");
                        if (Valid(path)) return path;

                    }

                }

            }

            catch
            {

            }

        }

        foreach (var basePath in new[]
{
 Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
}
) foreach (var sub in new[]
{
 @"Programs\HyperFrames\HyperFrames.exe", @"HyperFrames\HyperFrames.exe"
}
)
        {
            var path = Path.Combine(basePath, sub);
            if (Valid(path)) return path;

        }

        return null;

    }

    Process? Running()
    {
        var mains = new List<Process>();
        foreach (var p in Process.GetProcessesByName("HyperFrames"))
        {
            try
            {
                if (Native.IsMainProcess(p)) mains.Add(p);

            }
            catch
            {
                throw new Exception("已有 HyperFrames 进程无法验证，请先正常退出它后重试");

            }

        }
        if (mains.Count > 1) throw new Exception("检测到多个 HyperFrames 主进程，请先保留一个所需副本后重试");
        if (mains.Count == 0) return null;
        if (!string.Equals(mains[0].MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) throw new Exception("正在运行的 HyperFrames 与所选位置不同，请先正常退出当前应用；不会启动第二副本");
        return mains[0];

    }

    async Task<bool> CloseApp()
    {
        if (app == null || app.HasExited) return true;
        if (!app.CloseMainWindow())
        {
            status = "原应用未响应关闭请求，已保留";
            return false;

        }
        for (int i = 0; i < 60; i++)
        {
            if (app.HasExited) return true;
            await Task.Delay(500);

        }
        status = "关闭取消或等待保存超时，已保留原应用";
        return false;

    }

    async Task Open()

    {

        if (app != null && !app.HasExited && port > 0)
        {
            await Poll();
            Native.Show(app);
            return;

        }

        if (!Valid(executable)) executable = Discover();
        if (executable == null && !Choose())
        {
            status = "请选择已安装的 HyperFrames.exe";
            return;

        }

        app = Running();
        if (app != null)

        {

            var cmd = Native.CommandLine(app.Id);
            var m = System.Text.RegularExpressions.Regex.Match(cmd, @"--remote-debugging-port(?:=|\s+)(\d+)");

            if (m.Success && int.TryParse(m.Groups[1].Value, out var existing) && existing > 0 && existing < 65536)
            {
                port = existing;
                await Poll();
                Native.Show(app);
                return;

            }

            if (MessageBox.Show("启用中文需要正常关闭并重新打开 HyperFrames。请先保存工作；原应用的保存提示仍需处理。是否继续？", "启用中文", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                status = "保留原应用，尚未启用中文";
                Native.Show(app);
                return;

            }

            if (!await CloseApp()) return;

        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        app = Process.Start(new ProcessStartInfo(executable!)
        {
            UseShellExecute = false,
            ArgumentList =
{
 "--remote-debugging-address=127.0.0.1", $"--remote-debugging-port={port}"
}

        }
);
        status = "等待应用启动";
        await Task.Delay(1200);
        await Poll();

    }

    async Task Poll()

    {

        connected = false;
        count = 0;
        if (app == null || app.HasExited)
        {
            foreach (var connection in sessions.Values) connection.Dispose();
            sessions.Clear();
            app = null;
            port = 0;
            if (Valid(executable)) app = Running();
            if (app == null)
            {
                status = "等待打开 HyperFrames";
                return;
            }
        }
        if (port == 0)
        {
            var match = System.Text.RegularExpressions.Regex.Match(Native.CommandLine(app.Id), @"--remote-debugging-port(?:=|\s+)(\d+)");
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int candidate) || candidate < 1 || candidate > 65535)
            {
                status = "原应用未启用中文连接，请点击重新连接";
                return;
            }
            port = candidate;
        }

        Native.VerifyListeners(port, app.Id, executable!);

        using var list = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list"));
        var active = new HashSet<string>();
        count = 0;

        foreach (var target in list.RootElement.EnumerateArray())

        {

            if (!TargetPolicy.Page(target.GetProperty("type").GetString(), target.GetProperty("title").GetString(), target.GetProperty("url").GetString())) continue;

            var ws = target.GetProperty("webSocketDebuggerUrl").GetString();
            if (!TargetPolicy.WebSocket(ws, port)) continue;

            active.Add(ws!);
            if (!sessions.TryGetValue(ws!, out var c))
            {
                c = new Cdp();
                try
                {
                    await c.Connect(ws!);
                    sessions[ws!] = c;

                }
                catch
                {
                    c.Dispose();
                    throw;

                }

            }

            try
            {
                var s = await c.Evaluate("window.__HFZH__ ? window.__HFZH__.status() : ({})");
                if (!s.TryGetProperty("version", out var v) || v.GetString() != Version)
                {
                    s = await c.Evaluate("if(window.__HFZH__) {window.__HFZH__.disable();delete window.__HFZH__;} window.__HFZH_TRANSLATIONS__=" + Resource("translations.zh-CN.json") + ";window.__HFZH_PATTERNS__=" + Resource("dynamic.zh-CN.json") + ";" + Resource("translator.js") + ";window.__HFZH__." + (enabled ? "enable" : "disable") + "()");

                }
                else s = await c.Evaluate("window.__HFZH__." + (enabled ? "enable" : "disable") + "()");
                int translated = 0;
                if (s.TryGetProperty("translatedTextNodes", out var n)) translated += n.GetInt32();
                if (s.TryGetProperty("translatedAttributes", out var a)) translated += a.GetInt32();
                count += translated;

            }
            catch
            {
                c.Dispose();
                sessions.Remove(ws!);
                throw;

            }

        }

        foreach (var key in sessions.Keys.Where(k => !active.Contains(k)).ToArray())
        {
            sessions[key].Dispose();
            sessions.Remove(key);

        }

        connected = sessions.Count > 0;
        status = connected ? (enabled ? "中文已连接" : "英文已恢复") : "等待受信任的 HyperFrames 页面";

    }

    static string Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new Exception("缺少内置资源");
        using var r = new StreamReader(s);
        return r.ReadToEnd();

    }

    async Task Normal(bool quit)

    {

        foreach (var c in sessions.Values) try
            {
                await c.Evaluate("window.__HFZH__ ? window.__HFZH__.disable() : ({})");

            }
            catch
            {

            }

        foreach (var c in sessions.Values) c.Dispose();
        sessions.Clear();
        connected = false;
        count = 0;
        enabled = false;

        if (!quit && app != null && !app.HasExited)
        {
            if (!await CloseApp())
            {
                status = "应用窗口已收到关闭请求，但进程仍在运行（可能驻留托盘或等待保存）；已保留，未强制结束";
                return;

            }
            if (Valid(executable)) Process.Start(new ProcessStartInfo(executable!)
            {
                UseShellExecute = true
            }
           );

        }

        status = quit ? "中文启动器已退出" : "普通模式已重开";
        Update();
        exiting = true;
        lifetime.Cancel();
        timer.Stop();
        tray.Visible = false;
        tray.Dispose();
        dispatcher.Dispose();
        http.Dispose();
        ExitThread();

    }

}
