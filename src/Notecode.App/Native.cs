using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Notecode.App;

/// <summary>
/// Minimal Windows ConPTY (Pseudo Console) bindings.
///
/// ConPTY gives a real console host to a child process. That matters here because
/// kimi.exe and every other CLI we want to drive are console programs that change
/// their behaviour based on whether stdout is a TTY (colour, progress bars, raw mode).
/// A piped stdout would give us a degraded, non-interactive shell.
/// </summary>
internal static class Native
{
    internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    internal const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    internal const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    internal const uint STARTF_USESTDHANDLES = 0x00000100;

    [StructLayout(LayoutKind.Sequential)]
    internal struct COORD
    {
        public short X;
        public short Y;
        public COORD(short x, short y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int CreatePseudoConsole(
        COORD size,
        SafeFileHandle hInput,
        SafeFileHandle hOutput,
        uint dwFlags,
        out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue,
        IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        [In] ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    /// <summary>
    /// Builds a NUL-terminated command line. CreateProcessW mutates the buffer it is
    /// given, so it must be a fresh mutable copy every time.
    /// </summary>
    internal static string BuildCommandLine(string? fileName, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(fileName))
        {
            sb.Append('"').Append(fileName).Append('"');
        }
        foreach (var a in args)
        {
            sb.Append(' ');
            if (a.Length > 0 && !a.Contains(' ') && !a.Contains('\t'))
            {
                sb.Append(a);
            }
            else
            {
                // Quote and escape embedded quotes the way CommandLineToArgvW expects.
                sb.Append('"');
                for (int i = 0; i < a.Length; i++)
                {
                    int backslashes = 0;
                    while (i < a.Length && a[i] == '\\') { backslashes++; i++; }
                    if (i == a.Length)
                    {
                        sb.Append('\\', backslashes * 2);
                        break;
                    }
                    if (a[i] == '"')
                    {
                        sb.Append('\\', backslashes * 2 + 1).Append('"');
                    }
                    else
                    {
                        sb.Append('\\', backslashes).Append(a[i]);
                    }
                }
                sb.Append('"');
            }
        }
        return sb.ToString();
    }
}
