using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Notecode.App;

/// <summary>What the scan found about the display adapter.</summary>
internal sealed class GpuInfo
{
    /// <summary>NVIDIA, AMD, Intel, Microsoft, or Unknown.</summary>
    public string Vendor { get; set; } = "Unknown";

    public string Name { get; set; } = "";

    /// <summary>
    /// The name as it should be shown. The driver usually puts the vendor in the
    /// name already, so "AMD Radeon(TM) Graphics" does not become
    /// "AMD AMD Radeon(TM) Graphics".
    /// </summary>
    public string FullName =>
        Name.StartsWith(Vendor, StringComparison.OrdinalIgnoreCase) ? Name : $"{Vendor} {Name}".Trim();

    /// <summary>Dedicated video memory in bytes. Zero when the adapter has none.</summary>
    public long VramBytes { get; set; }

    /// <summary>
    /// True when the adapter has no memory of its own and borrows system RAM.
    /// An integrated GPU is the usual case, and the NPU shares the same pool.
    /// </summary>
    public bool SharedMemory { get; set; }

    public string DriverVersion { get; set; } = "";

    /// <summary>How the numbers were obtained: nvidia-smi, dxdiag or none.</summary>
    public string Source { get; set; } = "";

    public double VramGiB => VramBytes / 1024.0 / 1024.0 / 1024.0;
}

/// <summary>Everything the scan learned about this machine.</summary>
internal sealed class HardwareFacts
{
    public string CpuName { get; set; } = "";
    public int PhysicalCores { get; set; }
    public int LogicalProcessors { get; set; }
    public long RamBytes { get; set; }
    public string OsName { get; set; } = "";
    public string Architecture { get; set; } = "";
    public GpuInfo? Gpu { get; set; }
    public bool HasNpu { get; set; }
    public string NpuName { get; set; } = "";

    public double RamGiB => RamBytes / 1024.0 / 1024.0 / 1024.0;

    /// <summary>
    /// A fingerprint of the machine.
    ///
    /// It is what a saved profile is matched against, so it has to change when the
    /// machine changes and stay the same when it does not. The RAM is rounded to a
    /// gigabyte and the VRAM to a quarter of one, because those are the numbers a
    /// driver reports with a little jitter attached.
    /// </summary>
    public string Signature
    {
        get
        {
            var gpu = Gpu;
            var gpuPart = gpu is null
                ? "none"
                : $"{gpu.Vendor}|{gpu.Name}|{Math.Round(gpu.VramBytes / 268435456.0) * 268435456}";
            return string.Join('|',
                CpuName.Trim().ToLowerInvariant(),
                PhysicalCores.ToString(),
                LogicalProcessors.ToString(),
                Math.Round(RamBytes / 1073741824.0).ToString(),
                gpuPart,
                Architecture);
        }
    }

    /// <summary>The signature without the display adapter, for a cheaper match.</summary>
    public string HostSignature
    {
        get
        {
            return string.Join('|',
                CpuName.Trim().ToLowerInvariant(),
                PhysicalCores.ToString(),
                LogicalProcessors.ToString(),
                Math.Round(RamBytes / 1073741824.0).ToString(),
                Architecture);
        }
    }
}

/// <summary>
/// Reads the hardware of the machine notecode is running on.
///
/// The numbers decide which llama.cpp build can run here, so they have to be right
/// rather than merely present. Two of them are traps on Windows:
///
/// - **VRAM through WMI is capped at 4 GiB.** `Win32_VideoController.AdapterRAM` is a
///   32-bit field, so an 8 GiB card reports 4 GiB and a 16 GiB card reports the same
///   4 GiB. A rule that says "4 GiB or more, use the GPU" would then be decided by a
///   number Windows invented. `nvidia-smi` and `dxdiag` both report the real number.
/// - **An integrated GPU shares system RAM.** Its "VRAM" is a slice of the same pool
///   the model has to live in, so it is counted as shared and never treated as
///   dedicated memory.
///
/// The scan is deliberately allowed to take a few seconds: it runs once, on the
/// splash screen, and the result is cached against the machine signature.
/// </summary>
internal static class HardwareScanner
{
    // ------------------------------------------------------------------ public

    /// <summary>
    /// Scans the machine. Safe to call from a background thread; it does no UI work.
    /// </summary>
    public static HardwareFacts Scan(CancellationToken ct = default)
    {
        var facts = new HardwareFacts
        {
            CpuName = ReadCpuName(),
            LogicalProcessors = Environment.ProcessorCount,
            RamBytes = ReadTotalPhysicalMemory(),
            Architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            OsName = DescribeOs()
        };
        facts.PhysicalCores = ReadPhysicalCores();

        ct.ThrowIfCancellationRequested();
        facts.Gpu = DetectGpu(ct);

        ct.ThrowIfCancellationRequested();
        var npu = DetectNpu();
        facts.HasNpu = npu.Length > 0;
        facts.NpuName = npu;

        return facts;
    }

    // ------------------------------------------------------------------- cpu

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var value = key?.GetValue("ProcessorNameString")?.ToString();
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        catch { /* the registry is not always readable */ }

        try
        {
            return RuntimeInformation.ProcessArchitecture.ToString() + " processor";
        }
        catch { return "unknown processor"; }
    }

    /// <summary>
    /// The number of physical cores.
    ///
    /// <see cref="Environment.ProcessorCount"/> counts logical processors, and an SMT
    /// chip reports two of them for every core. The difference matters when the answer
    /// decides how many threads to give a model: 8 logical processors on 4 cores is a
    /// very different machine from 8 logical processors on 8 cores.
    /// </summary>
    private static int ReadPhysicalCores()
    {
        var logical = Environment.ProcessorCount;
        try
        {
            var count = 0;
            uint size = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref size);
            if (size == 0) return logical;

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref size)) return logical;
                var offset = 0;
                while (offset < (int)size)
                {
                    var relationship = Marshal.ReadInt32(buffer, offset);
                    var entrySize = Marshal.ReadInt32(buffer, offset + 4);
                    if (entrySize <= 0) break;
                    if (relationship == 0) count++;  // RelationProcessorCore
                    offset += entrySize;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }

            return count > 0 ? count : logical;
        }
        catch
        {
            return logical;
        }
    }

    private static string DescribeOs()
    {
        // The version API reports 10.0 for every Windows since 10, and the product
        // name in the registry still says "Windows 10" on Windows 11. The build
        // number is what tells them apart: 22000 and up is Windows 11.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName")?.ToString()?.Trim();
            var buildText = key?.GetValue("CurrentBuildNumber")?.ToString() ?? "";

            if (!string.IsNullOrWhiteSpace(product))
            {
                if (int.TryParse(buildText, out var build) && build >= 22000)
                {
                    product = product.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
                }
                return buildText.Length > 0 ? $"{product} (build {buildText})" : product;
            }
        }
        catch { /* the registry is not always readable */ }

        try
        {
            var v = Environment.OSVersion.Version;
            return $"Windows {v.Major}.{v.Minor}.{v.Build}";
        }
        catch { return RuntimeInformation.OSDescription; }
    }

    // ------------------------------------------------------------------- ram

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static long ReadTotalPhysicalMemory()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status)) return (long)status.ullTotalPhys;
        }
        catch { /* fall through to the GC value */ }
        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    }

    // ------------------------------------------------------------------- gpu

    private static GpuInfo? DetectGpu(CancellationToken ct)
    {
        // DXGI is the fast path: it asks the driver directly and answers in about
        // a millisecond.
        var dxgi = DetectDxgiGpu();
        if (dxgi is not null)
        {
            // nvidia-smi knows the real size of the card's memory, and DXGI does
            // not: for an NVIDIA card the dedicated number is authoritative, and
            // it is also the one that tells a discrete card from an integrated
            // one. It costs a second, and only NVIDIA machines pay it.
            if (dxgi.Vendor == "NVIDIA")
            {
                var nv = DetectNvidiaGpu(ct);
                if (nv is not null)
                {
                    nv.Name = dxgi.Name.Length > 0 ? dxgi.Name : nv.Name;
                    nv.Source = "dxgi + nvidia-smi";
                    return nv;
                }
            }

            return dxgi;
        }

        // nvidia-smi knows the real VRAM and the driver version, and it answers in
        // about a second. It is the second choice because it only knows NVIDIA.
        var nvidia = DetectNvidiaGpu(ct);
        if (nvidia is not null) return nvidia;

        // Everything else: dxdiag writes a report with the display memory in
        // megabytes. It is slower, and it cannot tell a card's own memory from
        // borrowed system memory, so it is the last resort.
        return DetectDxDiagGpu(ct);
    }

    // ------------------------------------------------------------------- dxgi

    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIObject
        void SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        void SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        void GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        void GetParent(ref Guid riid, out IntPtr parent);
        // IDXGIFactory
        void EnumAdapters(uint adapter, out IntPtr adapterOut);
        void MakeWindowAssociation(IntPtr windowHandle, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
        void CreateSwapChain(IntPtr device, IntPtr desc, IntPtr restrictToOutput, IntPtr hwnd, out IntPtr swapChain);
        void CreateSoftwareAdapter(IntPtr module, out IntPtr adapterOut);
        // IDXGIFactory1
        [PreserveSig] int EnumAdapters1(uint adapter, out IDXGIAdapter1 adapterOut);
        [PreserveSig] int IsCurrent();
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        void SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        void GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        void GetParent(ref Guid riid, out IntPtr parent);
        void EnumOutputs(uint adapter, out IntPtr output);
        [PreserveSig] int GetDesc(out IntPtr desc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid guid, out long version);
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 factory);

    /// <summary>
    /// Reads the display adapters through DXGI.
    ///
    /// This is the source of truth for video memory. It is the only one of the
    /// three that is both exact and instant: WMI caps the number at 4 GiB
    /// because the field is 32 bits, and dxdiag takes several seconds because it
    /// writes a report file.
    /// </summary>
    private static GpuInfo? DetectDxgiGpu()
    {
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            if (CreateDXGIFactory1(ref iid, out var factory) != 0 || factory is null) return null;

            GpuInfo? best = null;
            for (uint i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out var adapter) != 0 || adapter is null) break;

                DXGI_ADAPTER_DESC1 desc;
                try
                {
                    if (adapter.GetDesc1(out desc) != 0) continue;
                }
                catch { continue; }

                var vendor = VendorName(desc.VendorId);

                // Microsoft's adapter is the software rasteriser, not a GPU.
                if (vendor == "Microsoft") continue;

                var vram = (long)desc.DedicatedVideoMemory.ToUInt64();
                var shared = (long)desc.SharedSystemMemory.ToUInt64();

                // An integrated adapter has no memory of its own: it borrows from
                // the system pool, and Windows reports that borrow as "dedicated"
                // memory as well. The tell is that the shared pool is at least as
                // big as the dedicated one.
                //
                // The rule only applies below the 4 GiB floor, because above it the
                // answer does not change: a card with 4 GiB or more of its own is
                // used on the GPU path either way. Without that limit a discrete
                // card on a machine with a lot of RAM would be called shared,
                // because the system can lend it more memory than it owns.
                var isShared = vram < 4L * 1024 * 1024 * 1024 && shared > 0 && shared >= vram;
                var dedicated = isShared ? 0L : vram;

                // The adapter with the most dedicated memory is the one the model
                // would run on. A laptop with a small integrated GPU and a big
                // discrete card has both.
                if (best is null || dedicated > best.VramBytes)
                {
                    best = new GpuInfo
                    {
                        Vendor = vendor,
                        Name = desc.Description?.Trim() ?? "",
                        VramBytes = dedicated,
                        SharedMemory = isShared,
                        Source = "dxgi",
                        DriverVersion = shared > 0 ? $"shares {shared / 1024 / 1024 / 1024.0:0.0} GiB of system memory" : ""
                    };
                }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    private static string VendorName(uint vendorId) => vendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        _ => "Unknown"
    };

    private static GpuInfo? DetectNvidiaGpu(CancellationToken ct)
    {
        try
        {
            var output = RunProcess("nvidia-smi",
                "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits",
                ct, TimeSpan.FromSeconds(8));

            if (output is null) return null;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var first = lines[0].Trim();
            if (first.Length == 0) return null;

            var parts = first.Split(',');
            if (parts.Length < 2) return null;

            var name = parts[0].Trim();
            if (!long.TryParse(parts[1].Trim(), out var mib)) return null;

            return new GpuInfo
            {
                Vendor = "NVIDIA",
                Name = name,
                VramBytes = mib * 1024L * 1024L,
                SharedMemory = false,
                DriverVersion = parts.Length > 2 ? parts[2].Trim() : "",
                Source = "nvidia-smi"
            };
        }
        catch { return null; }
    }

    private static GpuInfo? DetectDxDiagGpu(CancellationToken ct)
    {
        var report = Path.Combine(Path.GetTempPath(), $"notecode-dxdiag-{Environment.ProcessId}.txt");
        try
        {
            if (File.Exists(report)) File.Delete(report);

            // dxdiag writes the file itself and exits when it is done.
            var psi = new ProcessStartInfo
            {
                FileName = "dxdiag.exe",
                Arguments = $"/whql:off /t \"{report}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            using var registration = ct.Register(static state => { try { ((Process)state!).Kill(true); } catch { } }, proc);

            // dxdiag writes the file and exits when it is done. It can take a
            // while on a machine with a lot of display adapters.
            proc.WaitForExit(45000);
            if (!proc.HasExited)
            {
                try { proc.Kill(true); } catch { }
            }

            if (!File.Exists(report)) return null;

            var text = File.ReadAllText(report);
            var (vendor, name, vramMiB, driver) = ParseDxDiag(text);
            if (name.Length == 0 && vramMiB <= 0) return null;

            // "Display Memory" is the dedicated VRAM. "Dedicated Memory" is what the
            // driver reserves for the display adapter, which on an integrated GPU is
            // a slice of system RAM rather than a separate pool.
            var shared = vendor is "Intel" or "Microsoft" or "AMD";

            // dxdiag cannot tell a card's own memory from borrowed system memory,
            // and on an integrated GPU it reports the whole shared pool. A number
            // above 4 GiB is therefore treated as shared: it is system RAM, and
            // pretending it is video memory would send the model to the wrong
            // place. A discrete card with more than 4 GiB is read by DXGI or by
            // nvidia-smi, both of which can tell the difference.
            var looksBorrowed = vramMiB > 4096;

            return new GpuInfo
            {
                Vendor = vendor,
                Name = name,
                VramBytes = looksBorrowed ? 0 : Math.Max(0, vramMiB) * 1024L * 1024L,
                SharedMemory = shared && (vramMiB < 1024 || looksBorrowed),
                DriverVersion = driver,
                Source = "dxdiag"
            };
        }
        catch { return null; }
        finally
        {
            try { if (File.Exists(report)) File.Delete(report); } catch { }
        }
    }

    /// <summary>Pulls the display adapter out of a dxdiag report.</summary>
    private static (string Vendor, string Name, long VramMiB, string Driver) ParseDxDiag(string text)
    {
        string vendor = "Unknown", name = "", driver = "";
        long vramMiB = 0;

        var lines = text.Split('\n');
        var inDisplay = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.StartsWith("Display Devices", StringComparison.OrdinalIgnoreCase))
            {
                inDisplay = true;
                continue;
            }

            if (inDisplay && line.StartsWith("Display Memory", StringComparison.OrdinalIgnoreCase))
            {
                // "Display Memory: 3072 MB"
                var colon = line.IndexOf(':');
                if (colon >= 0)
                {
                    var value = line[(colon + 1)..].Replace("MB", "", StringComparison.OrdinalIgnoreCase).Trim();
                    if (long.TryParse(value, out var parsed)) vramMiB = parsed;
                }
                continue;
            }

            if (line.StartsWith("Card name:", StringComparison.OrdinalIgnoreCase))
            {
                var colon = line.IndexOf(':');
                if (colon >= 0)
                {
                    var value = line[(colon + 1)..].Trim();
                    if (value.Length > 0 && value != "Display Memory" && name.Length == 0) name = value;
                }
                continue;
            }

            if (line.StartsWith("Driver Version:", StringComparison.OrdinalIgnoreCase))
            {
                var colon = line.IndexOf(':');
                if (colon >= 0) driver = line[(colon + 1)..].Trim();
                continue;
            }

            if (line.StartsWith("Manufacturer:", StringComparison.OrdinalIgnoreCase))
            {
                var colon = line.IndexOf(':');
                if (colon >= 0)
                {
                    var value = line[(colon + 1)..].Trim();
                    if (value.Length > 0) vendor = value;
                }
                continue;
            }
        }

        vendor = vendor.ToUpperInvariant() switch
        {
            "NVIDIA" => "NVIDIA",
            "AMD" => "AMD",
            "INTEL" => "Intel",
            "MICROSOFT" => "Microsoft",
            _ => "Unknown"
        };

        // "Microsoft Basic Render Driver" is not a GPU.
        if (name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)) name = "";

        return (vendor, name, vramMiB, driver);
    }

    // ------------------------------------------------------------------- npu

    /// <summary>
    /// Looks for a neural accelerator.
    ///
    /// A CPU with an NPU on it (Intel's Core Ultra, for one) can run models on that
    /// NPU through DirectML or OpenVINO, but llama.cpp has no backend for it, so the
    /// scan records it for the profile and the backend choice ignores it. The NPU is
    /// reported because a user who asks about it deserves to know what was found.
    /// </summary>
    private static string DetectNpu()
    {
        try
        {
            var output = RunProcess("pnputil.exe", "/enum-devices /connected /class SoftwareComponent", null, TimeSpan.FromSeconds(10));
            if (output is null) return "";

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.Contains("NPU", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Neural", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("AI Boost", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Compute Engine", StringComparison.OrdinalIgnoreCase))
                {
                    return line;
                }
            }
        }
        catch { /* optional */ }
        return "";
    }

    // --------------------------------------------------------------- process

    /// <summary>
    /// Runs a command and returns its output, or null when it could not be run.
    /// </summary>
    private static string? RunProcess(string file, string arguments, CancellationToken? ct, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var output = proc.StandardOutput.ReadToEndAsync();
            var error = proc.StandardError.ReadToEndAsync();

            // Kill the child if the caller cancels while it is still running.
            using var registration = ct.HasValue
                ? ct.Value.Register(static state => { try { ((Process)state!).Kill(true); } catch { } }, proc)
                : default;

            proc.WaitForExit((int)timeout.TotalMilliseconds);
            if (!proc.HasExited)
            {
                try { proc.Kill(true); } catch { }
                return null;
            }

            return output.GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------- p/invoke

    private const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(
        int infoType, IntPtr buffer, ref uint bufferLength);
}
