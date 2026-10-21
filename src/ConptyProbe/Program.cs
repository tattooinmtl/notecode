using System.Text;
using Notecode.App;

// Focused harness for ConptySession. It links the shipping Native.cs and
// ConptySession.cs, so what it proves is what the app actually does.
//
//   ConptyProbe ref <exe> [args...]   -> minimal reference implementation
//   ConptyProbe <exe>                 -> the shipping ConptySession

internal static class Probe
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "ref")
        {
            return RefConpty.Run(args[1..]);
        }
        return RunShipping(args);
    }

    private static int RunShipping(string[] args)
    {
        Console.WriteLine("ConPTY probe (shipping ConptySession)");
        Console.WriteLine("=======================================");

        var exe = args.Length > 0 ? args[0] : "pwsh.exe";
        var workDir = args.Length > 1 ? args[1] : Environment.CurrentDirectory;

        Console.WriteLine($"  exe     : {exe}");
        Console.WriteLine($"  workdir : {workDir}");
        Console.WriteLine("");

        using var session = new ConptySession();
        var done = new ManualResetEventSlim(false);
        var sb = new StringBuilder();

        session.OutputReceived += text =>
        {
            lock (sb) sb.Append(text);
            Console.WriteLine($"  [pty {text.Length} bytes] {text.Replace("\r\n", "\n").TrimEnd()}");
        };
        session.Exited += code =>
        {
            Console.WriteLine($"  [exit] code={code}");
            done.Set();
        };

        try
        {
            session.Start(exe, Array.Empty<string>(), workDir, cols: 120, rows: 30);
            Console.WriteLine($"  started pid={session.ProcessId} IsRunning={session.IsRunning}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  START FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        Thread.Sleep(2000);

        if (session.IsRunning)
        {
            try
            {
                session.Write("echo __notecode_probe_ok__\r");
                Thread.Sleep(1200);
                session.Write("exit\r");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  WRITE FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        }

        done.Wait(TimeSpan.FromSeconds(10));
        Thread.Sleep(600);

        string captured;
        lock (sb) captured = sb.ToString();

        Console.WriteLine("");
        Console.WriteLine($"  captured {captured.Length} chars");
        Console.WriteLine("  ---- raw captured ----");
        Console.WriteLine(captured.Replace("\u001b", "<ESC>"));
        Console.WriteLine("  --------------------");
        var marker = captured.Contains("__notecode_probe_ok__");
        Console.WriteLine($"  marker present : {marker}");
        Console.WriteLine("");
        Console.WriteLine(marker ? "PROBE OK" : "PROBE FAILED");
        return marker ? 0 : 1;
    }
}

