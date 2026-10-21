namespace Notecode.App;

/// <summary>
/// The rule that picks a llama.cpp build for a machine.
///
/// The rule the app follows:
///
/// - **No display adapter, or less than 3 GiB of video memory: CPU only.** A GPU with
///   a megabyte of memory is not a GPU for this workload, and asking Vulkan to place
///   a model there costs memory and buys nothing.
/// - **4 GiB or more: the GPU path.** On an NVIDIA card that is CUDA, which is the
///   fastest backend llama.cpp has. On everything else it is Vulkan, which runs on
///   NVIDIA, AMD and Intel alike and needs one driver rather than a toolkit.
/// - **Apple silicon: Metal.** There is no other backend there.
///
/// An adapter that borrows system RAM is not treated as a GPU with its own memory.
/// The memory it would use is the memory the model has to live in.
/// </summary>
internal static class BackendSelector
{
    /// <summary>Below this much video memory, the CPU does the work.</summary>
    public const long VramCpuOnlyCeiling = 3L * 1024 * 1024 * 1024;

    /// <summary>From this much video memory up, the GPU path is worth it.</summary>
    public const long VramGpuFloor = 4L * 1024 * 1024 * 1024;

    /// <summary>The key a build is filed under in the config.</summary>
    public static string Key(LlamaBackend backend) => backend switch
    {
        LlamaBackend.Cpu => "cpu",
        LlamaBackend.Vulkan => "vulkan",
        LlamaBackend.Cuda => "cuda",
        LlamaBackend.Metal => "metal",
        _ => "cpu"
    };

    /// <summary>The build key for a backend, read back from a key.</summary>
    public static LlamaBackend FromKey(string key) => key.ToLowerInvariant() switch
    {
        "vulkan" => LlamaBackend.Vulkan,
        "cuda" => LlamaBackend.Cuda,
        "metal" => LlamaBackend.Metal,
        _ => LlamaBackend.Cpu
    };

    /// <summary>True when the machine is an Apple one, where Metal is the only option.</summary>
    public static bool IsApple => OperatingSystem.IsMacOS();

    /// <summary>
    /// Picks a build for the machine.
    ///
    /// The preference is only used when the build is actually there: a CUDA build
    /// that was never compiled cannot be selected, so the choice falls to Vulkan,
    /// and then to the CPU.
    /// </summary>
    public static LlamaBackend Choose(HardwareFacts facts, IReadOnlyDictionary<string, LlamaBuildDef>? builds = null)
    {
        if (IsApple)
        {
            if (Has(builds, LlamaBackend.Metal)) return LlamaBackend.Metal;
            return LlamaBackend.Cpu;
        }

        var gpu = facts.Gpu;
        if (gpu is null || gpu.VramBytes < VramGpuFloor)
        {
            return LlamaBackend.Cpu;
        }

        if (gpu.Vendor == "NVIDIA")
        {
            if (Has(builds, LlamaBackend.Cuda)) return LlamaBackend.Cuda;
            if (Has(builds, LlamaBackend.Vulkan)) return LlamaBackend.Vulkan;
            return LlamaBackend.Cpu;
        }

        // AMD, Intel and everything else: Vulkan is the portable GPU backend.
        if (Has(builds, LlamaBackend.Vulkan)) return LlamaBackend.Vulkan;
        if (Has(builds, LlamaBackend.Cuda)) return LlamaBackend.Cuda;
        return LlamaBackend.Cpu;
    }

    /// <summary>The backend a profile will actually run under.</summary>
    public static LlamaBackend Effective(HardwareProfile profile, IReadOnlyDictionary<string, LlamaBuildDef>? builds = null)
    {
        if (profile.Backend != LlamaBackend.Auto) return profile.Backend;
        return Choose(profile.Facts, builds);
    }

    private static bool Has(IReadOnlyDictionary<string, LlamaBuildDef>? builds, LlamaBackend backend)
    {
        if (builds is null) return true;   // nothing to check against, assume it is there
        return builds.TryGetValue(Key(backend), out var build) && build.Exists;
    }

    /// <summary>One line saying why this backend was chosen.</summary>
    public static string Reason(HardwareFacts facts, LlamaBackend backend)
    {
        var gpu = facts.Gpu;
        if (backend == LlamaBackend.Cpu)
        {
            if (gpu is null) return "no display adapter was found";
            if (gpu.VramBytes < VramGpuFloor)
                return $"{gpu.VramGiB:0.0} GiB of video memory is under the 4 GiB floor";
            return "the CPU build was chosen by hand";
        }

        if (backend == LlamaBackend.Metal) return "Apple silicon runs on Metal";

        if (gpu is null) return "no display adapter was found";
        return $"{gpu.Vendor} {gpu.Name} has {gpu.VramGiB:0.0} GiB of video memory";
    }

    /// <summary>
    /// Whether a backend can be used at all on this machine, and why not when it cannot.
    ///
    /// Metal is macOS only and CUDA is NVIDIA only, so both are refused with a
    /// reason rather than silently ignored.
    /// </summary>
    public static string? UnavailableReason(LlamaBackend backend, HardwareFacts facts,
        IReadOnlyDictionary<string, LlamaBuildDef>? builds)
    {
        if (backend == LlamaBackend.Metal && !IsApple)
            return "Metal only exists on macOS.";

        if (backend == LlamaBackend.Cuda)
        {
            if (IsApple) return "CUDA is NVIDIA only, and this is an Apple machine.";
            if (facts.Gpu is null) return "No display adapter was found.";
            if (facts.Gpu.Vendor != "NVIDIA")
                return $"CUDA is NVIDIA only. This machine has {facts.Gpu.Vendor}.";
        }

        if (backend == LlamaBackend.Vulkan && facts.Gpu is null)
            return "No display adapter was found.";

        if (builds is not null)
        {
            if (!builds.TryGetValue(Key(backend), out var build))
                return $"There is no {Key(backend)} build in the config.";

            if (!build.Exists)
                return $"The {Key(backend)} build has not been compiled yet:\n{build.Exe}";
        }

        return null;
    }
}

/// <summary>
/// The build that is actually run, resolved for one machine.
///
/// This is what the app hands to the launcher: which executable, which device, how
/// many layers on the GPU. It is a separate object from the profile because the
/// profile is what the user chose, and this is what that choice came to.
/// </summary>
internal sealed class LlamaRuntimeOptions
{
    /// <summary>The llama-server.exe to run.</summary>
    public string Exe { get; set; } = "";

    /// <summary>The --device value: none, vulkan, cuda or metal.</summary>
    public string Device { get; set; } = "none";

    /// <summary>How many model layers go on the GPU. Zero means the CPU does all of it.</summary>
    public int NGpuLayers { get; set; }

    /// <summary>The context size, when the profile overrides it.</summary>
    public int? Ctx { get; set; }

    /// <summary>The build key this came from: cpu, vulkan, cuda or metal.</summary>
    public string BackendKey { get; set; } = "cpu";

    /// <summary>A one-line description for the status bar.</summary>
    public string Describe()
    {
        var backend = BackendKey switch
        {
            "vulkan" => "Vulkan",
            "cuda" => "CUDA",
            "metal" => "Metal",
            _ => "CPU"
        };
        var layers = NGpuLayers > 0 ? $", {NGpuLayers} layers on the GPU" : "";
        return $"{backend}{layers}";
    }

    /// <summary>
    /// Works out what to run for a profile.
    ///
    /// A profile that names a build gets that build, **even when the executable is
    /// not on this machine**: a missing build has to fail loudly, with the path in
    /// the log, rather than quietly running on the CPU while the user believes
    /// they are testing the GPU. The settings page says so before Apply.
    ///
    /// A profile that says "auto" gets whatever the scan picks, and the scan only
    /// picks a build that is actually here.
    /// </summary>
    public static LlamaRuntimeOptions Resolve(HardwareProfile profile,
        IReadOnlyDictionary<string, LlamaBuildDef> builds, string fallbackExe,
        string fallbackDevice = "none")
    {
        var requested = profile.Backend == LlamaBackend.Auto
            ? BackendSelector.Choose(profile.Facts, builds)
            : profile.Backend;
        var key = BackendSelector.Key(requested);

        if (builds.TryGetValue(key, out var build))
        {
            return new LlamaRuntimeOptions
            {
                Exe = build.Exe,
                Device = build.Device,
                NGpuLayers = LayersFor(profile, build, requested),
                Ctx = profile.CtxSize > 0 ? profile.CtxSize : null,
                BackendKey = key
            };
        }

        // The key is not in the map at all, so there is nothing to run for it.
        // The CPU build is the one that is always there.
        if (builds.TryGetValue("cpu", out var cpu))
        {
            return new LlamaRuntimeOptions
            {
                Exe = cpu.Exe,
                Device = cpu.Device,
                NGpuLayers = 0,
                Ctx = profile.CtxSize > 0 ? profile.CtxSize : null,
                BackendKey = "cpu"
            };
        }

        return new LlamaRuntimeOptions
        {
            Exe = fallbackExe,
            Device = fallbackDevice,
            NGpuLayers = 0,
            Ctx = profile.CtxSize > 0 ? profile.CtxSize : null,
            BackendKey = "cpu"
        };
    }

    /// <summary>
    /// How many layers go on the GPU.
    ///
    /// The profile wins when it says a number, because that is the number the user
    /// chose on the settings page. The build's own default is used otherwise.
    /// </summary>
    private static int LayersFor(HardwareProfile profile, LlamaBuildDef build, LlamaBackend backend)
    {
        var isGpu = backend is LlamaBackend.Vulkan or LlamaBackend.Cuda or LlamaBackend.Metal;
        if (isGpu && profile.NGpuLayers > 0) return profile.NGpuLayers;
        return build.NGpuLayers;
    }
}

/// <summary>Whether the model and the context fit in the memory this machine has.</summary>
internal sealed class MemoryVerdict
{
    /// <summary>Enough room, with room to spare.</summary>
    public bool Fits { get; init; }

    /// <summary>It fits, but the margin is thin.</summary>
    public bool Tight { get; init; }

    /// <summary>One line for the status bar.</summary>
    public string Summary { get; init; } = "";

    /// <summary>The numbers behind the summary.</summary>
    public string Detail { get; init; } = "";
}

/// <summary>Works out whether the model is going to fit on this machine.</summary>
internal static class MemoryPlanner
{
    /// <summary>What llama.cpp needs on top of the weights themselves.</summary>
    private const double RuntimeGiB = 1.3;

    /// <summary>What the GPU needs for its compute buffers, on top of the weights.</summary>
    private const double GpuOverheadGiB = 0.8;

    /// <summary>What the machine keeps for itself.</summary>
    private const double ReserveGiB = 1.0;

    public static MemoryVerdict Assess(HardwareFacts facts, long modelBytes, int ctxSize,
        LlamaBackend backend, double kvBytesPerToken)
    {
        var kvBytes = kvBytesPerToken * ctxSize;
        var ram = facts.RamBytes;
        var gpu = facts.Gpu;
        var usingGpu = backend is LlamaBackend.Vulkan or LlamaBackend.Cuda or LlamaBackend.Metal;

        var vramUsable = 0.0;
        if (usingGpu && gpu is not null && gpu.VramBytes > 0)
        {
            vramUsable = Math.Max(0, gpu.VramBytes / 1073741824.0 - GpuOverheadGiB);
        }

        var modelGiB = modelBytes / 1073741824.0;
        var modelOnGpu = Math.Min(modelGiB, vramUsable);
        var modelInRam = modelGiB - modelOnGpu;

        var neededRam = modelInRam + kvBytes / 1073741824.0 + RuntimeGiB;
        var availableRam = ram / 1073741824.0 - ReserveGiB;

        var detail =
            $"model {modelGiB:0.0} GiB (" +
            (modelOnGpu > 0.05 ? $"{modelOnGpu:0.0} on the GPU, {modelInRam:0.0} in RAM" : $"{modelInRam:0.0} in RAM") +
            $"), kv {kvBytes / 1073741824.0:0.0} GiB, runtime {RuntimeGiB:0.0} GiB, " +
            $"needs {neededRam:0.0} GiB of the {availableRam:0.0} GiB free";

        if (neededRam > availableRam)
        {
            return new MemoryVerdict
            {
                Fits = false,
                Tight = false,
                Summary = "does not fit",
                Detail = detail + ". It will page, which is slower than it looks."
            };
        }

        var margin = availableRam - neededRam;
        if (margin < 2.0)
        {
            return new MemoryVerdict
            {
                Fits = true,
                Tight = true,
                Summary = "fits, but barely",
                Detail = detail + $". Only {margin:0.0} GiB to spare."
            };
        }

        return new MemoryVerdict
        {
            Fits = true,
            Tight = false,
            Summary = "fits",
            Detail = detail + $". {margin:0.0} GiB to spare."
        };
    }
}
