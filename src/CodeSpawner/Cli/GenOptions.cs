namespace CodeSpawner.Cli;

public enum CompileDbMode { None, Partial, Full }

/// <summary>
/// All generation knobs. Defaults are full-repo targets; <see cref="Scale"/> multiplies the COUNT
/// knobs (never per-file sizes). A knob passed explicitly on the command line is taken literally
/// (see <see cref="Eff"/>) — a one-off run means what it says rather than being silently scaled.
/// </summary>
public sealed class GenOptions
{
    public required string Out { get; set; }
    public double Scale { get; set; } = 0.01;
    public int Seed { get; set; } = 1337;

    public int MacroDensity { get; set; } = 1_000_000;
    public int GiantHeaders { get; set; } = 178;
    public int BigHeaders { get; set; } = 655;
    public int MedHeaders { get; set; } = 1957;
    public int OrdinaryHeaders { get; set; } = 7400;
    public int CFiles { get; set; } = 5123;
    public int GiantIncluders { get; set; } = 3;
    public int TinyFiles { get; set; } = 20586;
    public int BlobFiles { get; set; } = 50;
    public int MaxHeaderMB { get; set; } = 110;
    public CompileDbMode CompileDb { get; set; } = CompileDbMode.None;
    public bool BuildOutput { get; set; } = true;
    public int Dirs { get; set; } = 5700;
    public int Depth { get; set; } = 8;
    public int LinkedRoots { get; set; } = 1;
    public int UnresolvedIncludes { get; set; } = 0;
    public bool Manifest { get; set; } = true;

    /// <summary>Overwrite <see cref="Out"/> even if it exists and was not created by CodeSpawner.</summary>
    public bool Force { get; set; } = false;

    /// <summary>
    /// Max concurrent large-header writes. When not set explicitly this is auto-tuned from the target disk
    /// (SSD/NVMe → all cores; spinning disk → 1, since concurrent multi-GB writes thrash the heads ~5x).
    /// The initializer value is only a fallback; the generator resolves it per-run against <see cref="Out"/>.
    /// </summary>
    public int IoParallelism { get; set; } = Environment.ProcessorCount;

    /// <summary>Names of knobs the caller set explicitly, so <see cref="Eff"/> can skip scaling them.</summary>
    public HashSet<string> Explicit { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool WasSet(string knob) => Explicit.Contains(knob);

    /// <summary>
    /// Effective count for a count knob. Explicit → literal. Otherwise default × Scale.
    /// A "pathology" knob (<paramref name="keepOne"/>) never rounds to 0 while enabled, so even a
    /// tiny scale keeps at least one giant header. A knob set to 0 disables that population.
    /// </summary>
    public int Eff(string knob, int value, bool keepOne = false)
    {
        if (value <= 0) return 0;
        if (WasSet(knob)) return value;
        int n = (int)Math.Round(value * Scale);
        if (keepOne && n < 1) n = 1;
        return n;
    }
}
