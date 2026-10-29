using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Notecode.App;

/// <summary>
/// Makes sure nothing notecode starts outlives it.
///
/// The app puts itself in a Windows job object at start-up, set to kill
/// everything in it when the job's last handle closes. Every process it starts
/// after that - llama-server through its wrapper, the MCP servers and whatever
/// they start in turn (cmd.exe /c npx starts node, which starts node), the
/// shell in the terminal and the commands run in it - is in the job too. The
/// only handle is this process's, so when notecode ends, however it ends (Exit
/// in the tray menu, a crash, Task Manager), Windows ends all of them.
///
/// Cleanup code cannot promise that: it does not run when the process is
/// killed, and a process two levels down is easy to miss.
///
/// Nothing may break away from the job. That matters: PowerShell 7's
/// Start-Process - which the llama wrapper uses - breaks away from any job
/// that allows it, so a job that allowed it would let llama-server go free.
///
/// A few things are started for the user rather than for the app - a browser
/// showing a model, the copy of notecode that replaces this one on a restart -
/// and those must stay open. They are started by Windows' own process service
/// (WMI, <see cref="StartOutside"/>), so they are never in the job at all.
/// </summary>
internal static class ProcessJob
{
    private static IntPtr _job;

    /// <summary>True once this process is in its job.</summary>
    public static bool Active => _job != IntPtr.Zero;

    /// <summary>Puts this process, and so everything it starts from now on, in the job.</summary>
    public static void AdoptCurrentProcess()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception();

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    // Kill everything when the job closes, and let nothing break away.
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                    throw new Win32Exception();
            }
            finally { Marshal.FreeHGlobal(ptr); }

            if (!AssignProcessToJobObject(job, Process.GetCurrentProcess().Handle))
                throw new Win32Exception();

            // The handle is kept open for the life of the process on purpose:
            // closing it is what ends the job.
            _job = job;
            AppLog.Write("job", "notecode and the processes it starts are in one job; they end with it");
        }
        catch (Exception ex)
        {
            // Without the job the app still works; only the guarantee is gone.
            AppLog.Warn($"the process job could not be set up, so child processes may outlive the app: {ex.Message}");
        }
    }

    /// <summary>
    /// Puts a process that left the job back in it. PowerShell's Start-Process
    /// takes what it starts out of the job even when the job forbids it, which
    /// is how the llama wrapper starts llama-server; the app adopts it by pid
    /// once the wrapper has written it down.
    /// </summary>
    public static void Adopt(int pid)
    {
        if (!Active) return;
        var handle = OpenProcess(PROCESS_SET_QUOTA | PROCESS_TERMINATE, false, pid);
        if (handle == IntPtr.Zero)
        {
            AppLog.Warn($"process {pid} could not be opened to join the job: {new Win32Exception().Message}");
            return;
        }
        try
        {
            if (AssignProcessToJobObject(_job, handle)) AppLog.Write("job", $"process {pid} joined the job");
            else AppLog.Warn($"process {pid} could not join the job: {new Win32Exception().Message}");
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>
    /// Starts a program outside the job, so it stays open when notecode exits.
    ///
    /// WMI's Win32_Process.Create starts it: the process is created by the WMI
    /// service, so it is not a child of notecode and not in its job. A short
    /// Windows PowerShell asks for it (that one is in the job, and ends at once).
    /// When the app is not in a job, the program is simply started.
    /// </summary>
    public static void StartOutside(string exe, IEnumerable<string> args, string? workingDirectory = null, bool hidden = false)
    {
        var commandLine = new StringBuilder(Quote(exe));
        foreach (var a in args) commandLine.Append(' ').Append(Quote(a));

        if (Active)
        {
            static string Ps(string text) => "'" + text.Replace("'", "''") + "'";
            var script =
                "$a = @{ CommandLine = " + Ps(commandLine.ToString()) +
                (workingDirectory is null ? "" : "; CurrentDirectory = " + Ps(workingDirectory)) + " }; " +
                "$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments $a; " +
                "exit $r.ReturnValue";
            try
            {
                var psi = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) psi.ArgumentList.Add(a);
                using var ps = Process.Start(psi);
                if (ps is not null && ps.WaitForExit(20000) && ps.ExitCode == 0) return;
                AppLog.Warn($"could not start {exe} outside the job (WMI answered {(ps?.HasExited == true ? ps.ExitCode : -1)}); starting it normally");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"could not start {exe} outside the job ({ex.Message}); starting it normally");
            }
        }

        var direct = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = hidden };
        foreach (var a in args) direct.ArgumentList.Add(a);
        if (workingDirectory is not null) direct.WorkingDirectory = workingDirectory;
        Process.Start(direct)?.Dispose();
    }

    /// <summary>Opens a file or address with its default program, outside the job.</summary>
    public static void OpenOutside(string target) =>
        StartOutside("cmd.exe", new[] { "/d", "/c", "start", "", target }, hidden: true);

    /// <summary>Quotes one argument the way CommandLineToArgvW reads it back.</summary>
    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
            else sb.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Win32

    private const int JobObjectExtendedLimitInformation = 9;
    private const int PROCESS_SET_QUOTA = 0x0100;
    private const int PROCESS_TERMINATE = 0x0001;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
