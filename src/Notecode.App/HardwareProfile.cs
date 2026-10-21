using System.Text.Json;
using System.Text.Json.Serialization;

namespace Notecode.App;

/// <summary>Which llama.cpp build to run the local model with.</summary>
internal enum LlamaBackend
{
    /// <summary>Let the scan decide.</summary>
    Auto,
    Cpu,
    Vulkan,
    Cuda,
    Metal
}

/// <summary>
/// One machine, one saved configuration.
///
/// A profile records what the scan found on a machine and how that machine should
/// run the model: which build, which model file, how much context. The store keeps
/// them side by side, so the same notecode checkout can be a CPU box on Monday and
/// a CUDA box on Tuesday without anyone editing a path by hand.
/// </summary>
internal sealed class HardwareProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("o");

    /// <summary>A short human name for the machine: the CPU, or the GPU if there is one.</summary>
    public string MachineLabel { get; set; } = "";

    /// <summary>The full fingerprint of the machine this profile was made on.</summary>
    public string Signature { get; set; } = "";

    /// <summary>The fingerprint without the display adapter.</summary>
    public string HostSignature { get; set; } = "";

    /// <summary>What the scan found.</summary>
    public HardwareFacts Facts { get; set; } = new();

    /// <summary>The build to use. <see cref="LlamaBackend.Auto"/> follows the scan.</summary>
    public LlamaBackend Backend { get; set; } = LlamaBackend.Auto;

    /// <summary>The llama-server to run. Empty means "take it from the build map".</summary>
    public string LlamaExePath { get; set; } = "";

    /// <summary>The model file. Empty means "take it from the app config".</summary>
    public string Model { get; set; } = "";

    /// <summary>The vision projector that goes with the model, if it has one.</summary>
    public string Mmproj { get; set; } = "";

    public int CtxSize { get; set; } = 32768;

    /// <summary>How many model layers go on the GPU. Zero means the CPU does all of it.</summary>
    public int NGpuLayers { get; set; }

    /// <summary>Zero means "let the app work it out from the core count".</summary>
    public int Threads { get; set; }

    /// <summary>True for the profile this machine is currently running under.</summary>
    public bool IsActive { get; set; }

    /// <summary>What the user typed, so a profile can carry a note for themselves.</summary>
    public string Note { get; set; } = "";

    public HardwareProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        CreatedUtc = CreatedUtc,
        MachineLabel = MachineLabel,
        Signature = Signature,
        HostSignature = HostSignature,
        Facts = Facts,
        Backend = Backend,
        LlamaExePath = LlamaExePath,
        Model = Model,
        Mmproj = Mmproj,
        CtxSize = CtxSize,
        NGpuLayers = NGpuLayers,
        Threads = Threads,
        IsActive = IsActive,
        Note = Note
    };
}

/// <summary>
/// A machine that has not run notecode yet, described well enough to set up.
///
/// The seed file exists because a profile is usually written on one machine for
/// another one: the RTX desktop is set up from the laptop, before anyone boots
/// notecode on it. A seed says "a machine like this runs the model like that", and
/// the scan decides whether this machine is one of those.
/// </summary>
internal sealed class HardwareProfileSeed
{
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";
    public SeedMatch Match { get; set; } = new();

    /// <summary>auto, cpu, vulkan, cuda or metal.</summary>
    public string Backend { get; set; } = "Auto";
    public string Model { get; set; } = "";
    public string Mmproj { get; set; } = "";
    public int CtxSize { get; set; }
    public int NGpuLayers { get; set; }

    /// <summary>True when this machine is the one the seed describes.</summary>
    public bool Matches(HardwareFacts facts)
    {
        if (Match.GpuVendor.Length > 0)
        {
            if (facts.Gpu is null) return false;
            if (!string.Equals(facts.Gpu.Vendor, Match.GpuVendor, StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (Match.MinVramGiB > 0)
        {
            if (facts.Gpu is null) return false;
            if (facts.Gpu.VramGiB < Match.MinVramGiB) return false;
        }

        if (Match.CpuContains.Length > 0 &&
            !facts.CpuName.Contains(Match.CpuContains, StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    /// <summary>
    /// Puts the seed's choices onto a profile.
    ///
    /// The model and the projector are set even when they are empty, because a
    /// seed describes a whole machine: a model with no vision must not keep the
    /// projector that belongs to a different model, or the server refuses to start.
    /// </summary>
    public void ApplyTo(HardwareProfile profile)
    {
        if (Name.Length > 0) profile.Name = Name;
        if (Note.Length > 0) profile.Note = Note;
        profile.Model = Model;
        profile.Mmproj = Mmproj;
        if (CtxSize > 0) profile.CtxSize = CtxSize;
        if (NGpuLayers > 0) profile.NGpuLayers = NGpuLayers;
        if (Backend.Length > 0) profile.Backend = Backend.ToLowerInvariant() switch
        {
            "vulkan" => LlamaBackend.Vulkan,
            "cuda" => LlamaBackend.Cuda,
            "metal" => LlamaBackend.Metal,
            "cpu" => LlamaBackend.Cpu,
            _ => LlamaBackend.Auto
        };
    }

    /// <summary>Reads the seed file. A missing file is simply no seeds.</summary>
    public static List<HardwareProfileSeed> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<HardwareProfileSeed>();
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<HardwareProfileSeedFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            return loaded?.Profiles ?? new List<HardwareProfileSeed>();
        }
        catch
        {
            return new List<HardwareProfileSeed>();
        }
    }
}

/// <summary>How a seed decides whether it is about this machine.</summary>
internal sealed class SeedMatch
{
    /// <summary>NVIDIA, AMD, Intel, Microsoft, or empty for any.</summary>
    public string GpuVendor { get; set; } = "";

    /// <summary>The adapter needs at least this much video memory.</summary>
    public double MinVramGiB { get; set; }

    /// <summary>The processor name has to contain this.</summary>
    public string CpuContains { get; set; } = "";
}

/// <summary>The shape of the seed file.</summary>
internal sealed class HardwareProfileSeedFile
{
    public List<HardwareProfileSeed> Profiles { get; set; } = new();
}

/// <summary>
/// The saved profiles, on disk.
///
/// They live under %APPDATA%\notecode, not next to the executable, because they are
/// a property of the *machine*, not of the checkout. A repository that is synced or
/// copied between a CPU laptop and a CUDA desktop must not carry one machine's GPU
/// path to the other.
/// </summary>
internal sealed class HardwareProfileStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public List<HardwareProfile> Profiles { get; set; } = new();

    /// <summary>The id of the profile in use, if any.</summary>
    public string? ActiveId { get; set; }

    public string FilePath { get; set; } = DefaultPath();

    public static string DefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
        return Path.Combine(root, "notecode", "hardware-profiles.json");
    }

    public HardwareProfile? Active =>
        Profiles.FirstOrDefault(p => p.Id == ActiveId) ??
        Profiles.FirstOrDefault(p => p.IsActive);

    /// <summary>Reads the store. A missing or broken file is an empty store, not a crash.</summary>
    public static HardwareProfileStore Load(string? path = null)
    {
        var store = new HardwareProfileStore { FilePath = path ?? DefaultPath() };
        try
        {
            if (!File.Exists(store.FilePath)) return store;
            var json = File.ReadAllText(store.FilePath);
            var loaded = JsonSerializer.Deserialize<HardwareProfileStore>(json, Json);
            if (loaded is null) return store;
            loaded.FilePath = store.FilePath;
            return loaded;
        }
        catch
        {
            // A store we cannot read must not stop the app from starting. The user
            // gets an empty list and can scan again.
            return store;
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* the profile is a convenience; failing to write it is not fatal */ }
    }

    /// <summary>The profile that matches this machine exactly, if there is one.</summary>
    public HardwareProfile? FindBySignature(string signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        return Profiles.FirstOrDefault(p =>
            string.Equals(p.Signature, signature, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The profile for this machine when the display adapter has changed.
    ///
    /// The cheap parts of the fingerprint are the CPU, the core counts and the RAM.
    /// When those all match a saved profile but the adapter does not - a different
    /// GPU, or a GPU that was not there before - the saved profile is still the right
    /// one to start from, because the machine itself has not changed.
    /// </summary>
    public HardwareProfile? FindByHostSignature(string hostSignature)
    {
        if (string.IsNullOrEmpty(hostSignature)) return null;
        return Profiles.FirstOrDefault(p =>
            string.Equals(p.HostSignature, hostSignature, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds the profile, or replaces the one with the same id.</summary>
    public void Upsert(HardwareProfile profile)
    {
        var index = Profiles.FindIndex(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) Profiles[index] = profile;
        else Profiles.Add(profile);
    }
}
