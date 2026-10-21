using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Notecode.App;

/// <summary>
/// A live console session backed by a Windows pseudo console (ConPTY).
///
/// The child process gets a real console, so it renders colour, handles raw-mode
/// input, and behaves the way it would in a normal terminal window. Output is
/// decoded as UTF-8 and surfaced as text; the xterm.js pane in the UI does the
/// VT100 rendering, so we deliberately do not interpret escape sequences here.
/// </summary>
internal sealed class ConptySession : IDisposable
{
    private readonly object _writeLock = new();
    private SafeFileHandle? _inputWrite;
    private FileStream? _inputStream;
    private FileStream? _outputStream;
    private IntPtr _hPC = IntPtr.Zero;
    private IntPtr _hProcess = IntPtr.Zero;
    private IntPtr _hThread = IntPtr.Zero;
    private IntPtr _attributeList = IntPtr.Zero;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private bool _disposed;

    public int ProcessId { get; private set; }
    public bool IsRunning => _hProcess != IntPtr.Zero && !HasExited;
    public event Action<string>? OutputReceived;
    public event Action<int>? Exited;

    /// <summary>Starts <paramref name="fileName"/> with <paramref name="args"/> attached to a new pseudo console.</summary>
    public void Start(string fileName, IReadOnlyList<string> args, string workingDirectory, int cols = 120, int rows = 30)
    {
        if (_hPC != IntPtr.Zero) throw new InvalidOperationException("session already started");

        if (!Directory.Exists(workingDirectory)) Directory.CreateDirectory(workingDirectory);

        // The pipe handles must be inheritable: with bInheritHandles = FALSE the
        // only handles a child receives are the ones named in STARTF_USESTDHANDLES,
        // and the kernel only duplicates those if they were created inheritable.
        var sa = new Native.SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = IntPtr.Zero,
            bInheritHandle = 1
        };

        // Two anonymous pipes:
        //   input  : we write keystrokes -> hPipeInWrite ; the console feeds hPipeInRead
        //   output : the console writes hPipeOutWrite ; we read hPipeOutRead
        if (!CreatePipe(out var hPipeInRead, out var hPipeInWrite, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(in) failed");
        if (!CreatePipe(out var hPipeOutRead, out var hPipeOutWrite, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(out) failed");

        var size = new Native.COORD((short)Math.Max(1, cols), (short)Math.Max(1, rows));
        int hr = Native.CreatePseudoConsole(size, hPipeInRead, hPipeOutWrite, 0, out _hPC);
        if (hr != 0)
        {
            hPipeInRead.Dispose(); hPipeInWrite.Dispose();
            hPipeOutRead.Dispose(); hPipeOutWrite.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreatePseudoConsole failed (hr={hr})");
        }

        _inputWrite = hPipeInWrite;
        _inputStream = new FileStream(_inputWrite, FileAccess.Write);
        _outputStream = new FileStream(hPipeOutRead, FileAccess.Read);

        // Build the attribute list that tells CreateProcess to use the pseudo console.
        IntPtr sizeBuf = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref sizeBuf);
        _attributeList = Marshal.AllocHGlobal(sizeBuf);
        if (!Native.InitializeProcThreadAttributeList(_attributeList, 1, 0, ref sizeBuf))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");
        if (!Native.UpdateProcThreadAttribute(
                _attributeList, 0, (IntPtr)Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");
        }

        // STARTF_USESTDHANDLES is what actually wires the child's standard handles
        // to the pseudo console. Without it the child inherits the parent's
        // console instead and its output never reaches the pipe.
        var si = new Native.STARTUPINFOEX
        {
            StartupInfo = new Native.STARTUPINFO
            {
                cb = Marshal.SizeOf<Native.STARTUPINFOEX>(),
                dwFlags = (int)Native.STARTF_USESTDHANDLES,
                hStdInput = hPipeInRead.DangerousGetHandle(),
                hStdOutput = hPipeOutWrite.DangerousGetHandle(),
                hStdError = hPipeOutWrite.DangerousGetHandle()
            },
            lpAttributeList = _attributeList
        };

        string commandLine = Native.BuildCommandLine(fileName, args);
        var mutable = new StringBuilder(commandLine);

        if (!Native.CreateProcessW(
                null,
                mutable.ToString(),
                IntPtr.Zero, IntPtr.Zero,
                false,
                Native.EXTENDED_STARTUPINFO_PRESENT | Native.CREATE_UNICODE_ENVIRONMENT,
                IntPtr.Zero,
                workingDirectory,
                ref si,
                out var pi))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess failed for '{fileName}'");
        }

        // The child now owns copies of the console-side pipe ends.
        hPipeInRead.Dispose();
        hPipeOutWrite.Dispose();

        _hProcess = pi.hProcess;
        _hThread = pi.hThread;
        ProcessId = pi.dwProcessId;

        _cts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var buf = new byte[8192];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[16384];

        try
        {
            while (!ct.IsCancellationRequested && _outputStream != null)
            {
                int read = await _outputStream.ReadAsync(buf, ct).ConfigureAwait(false);
                if (read <= 0) break;

                int charCount = decoder.GetChars(buf, 0, read, chars, 0);
                if (charCount > 0)
                {
                    var text = new string(chars, 0, charCount);
                    OutputReceived?.Invoke(text);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (IOException) { /* pipe closed */ }
        catch (ObjectDisposedException) { }
        finally
        {
            int code = 0;
            if (_hProcess != IntPtr.Zero)
            {
                try { Native.GetExitCodeProcess(_hProcess, out var raw); code = unchecked((int)raw); } catch { }
            }
            Exited?.Invoke(code);
        }
    }

    public void Write(string text)
    {
        if (_inputStream == null) return;
        lock (_writeLock)
        {
            // Deliberately not swallowing failures: a silent write error looks
            // exactly like "the terminal is dead", which is much harder to
            // diagnose than an exception.
            var bytes = Encoding.UTF8.GetBytes(text);
            _inputStream.Write(bytes, 0, bytes.Length);
            _inputStream.Flush();
        }
    }

    public void WriteLine(string text) => Write(text + "\r\n");

    public void Resize(int cols, int rows)
    {
        if (_hPC == IntPtr.Zero) return;
        var size = new Native.COORD((short)Math.Max(1, cols), (short)Math.Max(1, rows));
        Native.ResizePseudoConsole(_hPC, size);
    }

    private bool HasExited
    {
        get
        {
            if (_hProcess == IntPtr.Zero) return true;
            // STILL_ACTIVE is 259 (0x103) - not 0x259.
            return Native.GetExitCodeProcess(_hProcess, out var code) && code != 259;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, ref Native.SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts?.Cancel(); } catch { }
        try { _inputStream?.Dispose(); } catch { }
        try { _outputStream?.Dispose(); } catch { }
        try { _inputWrite?.Dispose(); } catch { }

        if (_hPC != IntPtr.Zero) { try { Native.ClosePseudoConsole(_hPC); } catch { } _hPC = IntPtr.Zero; }
        if (_attributeList != IntPtr.Zero)
        {
            try { Native.DeleteProcThreadAttributeList(_attributeList); } catch { }
            try { Marshal.FreeHGlobal(_attributeList); } catch { }
            _attributeList = IntPtr.Zero;
        }
        if (_hThread != IntPtr.Zero) { try { Native.CloseHandle(_hThread); } catch { } _hThread = IntPtr.Zero; }
        if (_hProcess != IntPtr.Zero) { try { Native.CloseHandle(_hProcess); } catch { } _hProcess = IntPtr.Zero; }

        _cts?.Dispose();
        _cts = null;
    }
}
