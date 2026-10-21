using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

// A deliberately minimal, self-contained ConPTY implementation used ONLY to
// establish ground truth. It follows the documented pattern exactly and uses
// IntPtr everywhere (no string fields in STARTUPINFO), so it can be diffed
// against Notecode.App's ConptySession to find the behavioural difference.
//
// Usage: RefConpty <exe> [args...]

internal static class RefConpty
{
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X, Y;
        public COORD(short x, short y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr attrs, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint flags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr cbSize, IntPtr prev, IntPtr ret);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? appName, string cmdLine, IntPtr procAttr, IntPtr threadAttr, bool inherit,
        uint flags, IntPtr env, string? curDir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr h, byte[] buf, int n, out int read, IntPtr ovl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr h, byte[] buf, int n, out int written, IntPtr ovl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ClosePseudoConsole(IntPtr hPC);

    public static int Run(string[] args)
    {
        var exe = args.Length > 0 ? args[0] : "cmd.exe";
        var variant = args.Length > 1 ? args[1] : "v1";
        Console.WriteLine("Reference ConPTY");
        Console.WriteLine($"  exe: {exe}   variant: {variant}");

        if (!CreatePipe(out var hInRead, out var hInWrite, IntPtr.Zero, 0))
            { Console.WriteLine("CreatePipe(in) failed"); return 1; }
        if (!CreatePipe(out var hOutRead, out var hOutWrite, IntPtr.Zero, 0))
            { Console.WriteLine("CreatePipe(out) failed"); return 1; }

        var hr = CreatePseudoConsole(new COORD(120, 30), hInRead, hOutWrite, 0, out var hPC);
        Console.WriteLine($"  CreatePseudoConsole hr={hr} hPC=0x{hPC:X}");
        if (hr != 0) return 1;

        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attrList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
            { Console.WriteLine("InitializeProcThreadAttributeList failed"); return 1; }
        if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            { Console.WriteLine($"UpdateProcThreadAttribute failed err={Marshal.GetLastWin32Error()}"); return 1; }

        var si = new STARTUPINFOEX
        {
            StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() },
            lpAttributeList = attrList
        };

        bool inheritHandles = false;
        uint flags = EXTENDED_STARTUPINFO_PRESENT;

        switch (variant)
        {
            case "v1": // baseline
                break;
            case "v2": // inherit handles
                inheritHandles = true;
                break;
            case "v3": // STARTF_USESTDHANDLES onto the PTY pipes
                si.StartupInfo.dwFlags = 0x00000100; // STARTF_USESTDHANDLES
                si.StartupInfo.hStdInput = hInRead;
                si.StartupInfo.hStdOutput = hOutWrite;
                si.StartupInfo.hStdError = hOutWrite;
                break;
            case "v4": // v3 + unicode env
                si.StartupInfo.dwFlags = 0x00000100;
                si.StartupInfo.hStdInput = hInRead;
                si.StartupInfo.hStdOutput = hOutWrite;
                si.StartupInfo.hStdError = hOutWrite;
                flags |= 0x00000400;
                break;
            case "v5": // inherit handles + STARTF_USESTDHANDLES
                inheritHandles = true;
                si.StartupInfo.dwFlags = 0x00000100;
                si.StartupInfo.hStdInput = hInRead;
                si.StartupInfo.hStdOutput = hOutWrite;
                si.StartupInfo.hStdError = hOutWrite;
                break;
        }

        Console.WriteLine($"  STARTUPINFOEX size={Marshal.SizeOf<STARTUPINFOEX>()} cb={si.StartupInfo.cb} flags=0x{flags:X} inherit={inheritHandles} stdFlags=0x{si.StartupInfo.dwFlags:X}");

        var cmd = $"\"{exe}\"";
        var ok = CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, inheritHandles,
            flags, IntPtr.Zero, null, ref si, out var pi);
        if (!ok)
        {
            Console.WriteLine($"  CreateProcessW FAILED err={Marshal.GetLastWin32Error()}");
            return 1;
        }
        Console.WriteLine($"  child pid={pi.dwProcessId}");

        // Read the PTY output on a background thread.
        var cts = new CancellationTokenSource();
        var total = 0;
        var pump = Task.Run(() =>
        {
            var buf = new byte[4096];
            while (!cts.IsCancellationRequested)
            {
                if (!ReadFile(hOutRead, buf, buf.Length, out var n, IntPtr.Zero)) break;
                if (n <= 0) break;
                Interlocked.Add(ref total, n);
                var s = Encoding.UTF8.GetString(buf, 0, n);
                Console.WriteLine($"  [pty {n} bytes] {s.Replace("\r\n", "\n").TrimEnd()}");
            }
        });

        Thread.Sleep(2000);
        var w = Encoding.UTF8.GetBytes("echo __ref_ok__\r");
        WriteFile(hInWrite, w, w.Length, out _, IntPtr.Zero);
        Thread.Sleep(1500);

        Console.WriteLine($"  TOTAL bytes through the pipe: {total}");

        cts.Cancel();
        try { ClosePseudoConsole(hPC); } catch { }
        CloseHandle(hInWrite);
        CloseHandle(hOutRead);
        DeleteProcThreadAttributeList(attrList);
        Marshal.FreeHGlobal(attrList);
        return 0;
    }
}

