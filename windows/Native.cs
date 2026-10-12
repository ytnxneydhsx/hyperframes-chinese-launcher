using System.Diagnostics;

using System.Net;

using System.Runtime.InteropServices;

using System.Text;

namespace HyperFramesChinese;


static class Native

{

    [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] static extern bool Process32First(IntPtr snapshot, ref Entry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] static extern bool Process32Next(IntPtr snapshot, ref Entry entry);

    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct Entry
    {
        public uint size, usage, pid;
        public UIntPtr heap;
        public uint module, threads, parent;
        public int priority;
        public uint flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string name;

    }

    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr data, int length, out int returned);

    [StructLayout(LayoutKind.Sequential)]
    struct UnicodeString
    {
        public ushort length, maxLength;
        public IntPtr buffer;

    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);

    public static void Show(Process p)
    {
        p.Refresh();
        if (p.MainWindowHandle != IntPtr.Zero)
        {
            ShowWindow(p.MainWindowHandle, 9);
            SetForegroundWindow(p.MainWindowHandle);

        }

    }

    public static string CommandLine(int pid)
    {
        using var p = Process.GetProcessById(pid);
        NtQueryInformationProcess(p.Handle, 60, IntPtr.Zero, 0, out int size);
        if (size <= 0 || size > 1024 * 1024) throw new Exception("无法读取应用命令行");
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryInformationProcess(p.Handle, 60, ptr, size, out _) != 0) throw new Exception("无法验证应用进程");
            var s = Marshal.PtrToStructure<UnicodeString>(ptr);
            return Marshal.PtrToStringUni(s.buffer, s.length / 2) ?? "";

        }
        finally
        {
            Marshal.FreeHGlobal(ptr);

        }

    }

    public static bool IsMainProcess(Process process)

    {

        string command = CommandLine(process.Id);

        if (System.Text.RegularExpressions.Regex.IsMatch(command, @"(?:^|\s)--type(?:=|\s)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;

        process.Refresh();

        if (process.MainWindowHandle == IntPtr.Zero && System.Text.RegularExpressions.Regex.IsMatch(command, "(?:^|\\s)(?:\"[^\"]*\\.(?:js|cjs|mjs)\"|[^\\s\"]*\\.(?:js|cjs|mjs))(?=\\s|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;

        return true;


    }

    static Dictionary<int, int> Parents()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new Exception("无法验证进程关系");
        try
        {
            var e = new Entry
            {
                size = (uint)Marshal.SizeOf<Entry>(),
                name = ""
            }
           ;
            if (Process32First(snapshot, ref e)) do
            {
                map[(int)e.pid] = (int)e.parent;

            }
            while (Process32Next(snapshot, ref e));

        }
        finally
        {
            CloseHandle(snapshot);

        }
        return map;

    }

    public static void VerifyListeners(int port, int mainPid, string executable)

    {

        var parents = Parents();
        bool found = false;

        bool Owned(int pid)
        {
            var seen = new HashSet<int>();
            int cursor = pid;
            while (seen.Add(cursor))
            {
                if (cursor == mainPid)
                {
                    using var p = Process.GetProcessById(pid);
                    using var main = Process.GetProcessById(mainPid);
                    return string.Equals(p.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase) && string.Equals(main.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase);

                }
                if (!parents.TryGetValue(cursor, out cursor)) break;

            }
            return false;

        }

        foreach (int family in new[]
{
 2, 23
}
)

        {

            int size = 0;
            uint error = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            if (error != 122 && error != 0) throw new Exception("无法读取调试端口状态");
            if (size < 4) continue;
            var ptr = Marshal.AllocHGlobal(size);

            try

            {

                if (GetExtendedTcpTable(ptr, ref size, false, family, 3, 0) != 0) throw new Exception("无法验证调试端口");
                int rows = Marshal.ReadInt32(ptr), stride = family == 2 ? 24 : 56;
                if (rows < 0 || 4L + (long)rows * stride > size) throw new Exception("TCP 状态表无效");

                for (int i = 0; i < rows; i++)
                {
                    var row = IntPtr.Add(ptr, 4 + i * stride);
                    int localOffset = family == 2 ? 8 : 20;
                    int raw = Marshal.ReadInt32(row, localOffset);
                    int localPort = ((raw & 255) << 8) | ((raw >> 8) & 255);
                    if (localPort != port) continue;
                    int state = Marshal.ReadInt32(row, family == 2 ? 0 : 48);
                    if (state != 2) continue;
                    byte[] address = new byte[family == 2 ? 4 : 16];
                    Marshal.Copy(IntPtr.Add(row, family == 2 ? 4 : 0), address, 0, address.Length);
                    if (!IPAddress.IsLoopback(new IPAddress(address)) || !Owned(Marshal.ReadInt32(row, family == 2 ? 20 : 52))) throw new Exception("调试端口未通过本机地址和进程归属校验");
                    found = true;

                }


            }

            finally
            {
                Marshal.FreeHGlobal(ptr);

            }


        }

        if (!found) throw new Exception("尚未检测到受信任的本机调试端口");


    }


}
