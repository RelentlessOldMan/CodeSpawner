namespace CodeSpawner.Manifest;

/// <summary>How an indirect edge takes its target's address (C profile; vtable/override are the v2 C++ set).</summary>
public enum IndirectVia { FnPtr, VectorTable, InitArray }

public static class IndirectViaExtensions
{
    /// <summary>Stable wire label — part of the indirectTruthSha canonical form; never renumber/rename.</summary>
    public static string Label(this IndirectVia v) => v switch
    {
        IndirectVia.FnPtr => "fnptr",
        IndirectVia.VectorTable => "vector-table",
        IndirectVia.InitArray => "init_array",
        _ => "fnptr",
    };

    public static bool TryParse(string s, out IndirectVia v)
    {
        v = s switch
        {
            "fnptr" => IndirectVia.FnPtr,
            "vector-table" => IndirectVia.VectorTable,
            "init_array" => IndirectVia.InitArray,
            _ => (IndirectVia)(-1),
        };
        return (int)v >= 0;
    }
}

/// <summary>
/// An indirect call edge (function pointer / dispatch table / init_array) from the owning symbol to
/// <see cref="Target"/>. <see cref="Dispatched"/> false = address-taken but never invoked (the indirection
/// tax a sound carve pays); <see cref="Resolved"/> false = external target (terminal, not a declared symbol).
/// See docs/oracle-v1-design.md.
/// </summary>
public sealed class IndirectEdge
{
    public required string Target { get; init; }
    public required IndirectVia Via { get; init; }
    public bool Dispatched { get; init; }
    public bool Resolved { get; init; } = true;
}

/// <summary>How a C++ member-function pointer's address flows (D10): stored in a table, or passed as an argument.</summary>
public enum MemberPtrVia { Table, Arg }

public static class MemberPtrViaExtensions
{
    /// <summary>Stable wire label, part of the cppTruthSha canonical form; never rename.</summary>
    public static string Label(this MemberPtrVia v) => v == MemberPtrVia.Arg ? "arg" : "table";

    public static bool TryParse(string s, out MemberPtrVia v)
    {
        v = s == "arg" ? MemberPtrVia.Arg : MemberPtrVia.Table;
        return s is "arg" or "table";
    }
}

/// <summary>
/// A C++ virtual call site on the calling symbol (manifest v2, D3). <see cref="Static"/> is the method the call
/// names (the declaration visible from the receiver's static type); <see cref="Cha"/> every non-pure
/// implementation a receiver in that type's subtree can dispatch to (the sound set); <see cref="Rta"/> the subset a
/// receiver whose class is constructed in reachable code dispatches to (the precise set). See docs/cpp-profile-design.md.
/// </summary>
public sealed class VirtualEdge
{
    public required string Static { get; init; }
    public List<string> Cha { get; init; } = new();
    public List<string> Rta { get; init; } = new();
}

/// <summary>A C++ member-function pointer taken by the owning symbol (manifest v2, D10). <see cref="Dispatched"/>
/// false = address taken, never called through (a counted over-keep, like oracle-v1 indirect edges).</summary>
public sealed class MemberPtrEdge
{
    public required string Target { get; init; }
    public required MemberPtrVia Via { get; init; }
    public bool Dispatched { get; init; }
}

/// <summary>
/// One ground-truth symbol: where it is defined, where it is referenced, its call-graph edges, indirect
/// edges, and (for the negative case) references that must NOT resolve. Paths are repo-relative to the corpus
/// root; the symbol NAME — not the path — is the seed-stable identity.
/// </summary>
public sealed class SymbolEntry
{
    public required string Def { get; set; }
    public List<string> Refs { get; init; } = new();
    public List<string> Edges { get; init; } = new();
    public List<IndirectEdge> IndirectEdges { get; init; } = new();
    public List<string>? UnreachableRefs { get; set; }

    /// <summary>
    /// Byte mass of this symbol's definition span (phase 4, opt-in via --oracle-bytes). 0 = absent. Lets a
    /// carve's reduction target be asserted in BYTES — the unit the GB-reduction goal actually uses — not just
    /// symbol count. Most useful with --oracle-scale, where body inflation makes sizes non-uniform.
    /// </summary>
    public long Bytes { get; set; }

    /// <summary>
    /// True when a correct lexical / preprocessor-blind indexer is EXPECTED not to resolve this symbol
    /// (e.g. a token-paste-generated name that never appears literally in the source). The honest-miss dual
    /// of <see cref="UnreachableRefs"/>: not finding it is correct, not a recall failure.
    /// </summary>
    public bool ExpectedMiss { get; set; }

    // --- manifest v2 (C++ only; null/empty in a v1 manifest, so v1 output is unchanged) ---

    /// <summary><c>function</c> | <c>method</c> | <c>ctor</c> | <c>dtor</c> | <c>template</c> | <c>instance</c> (D2).</summary>
    public string? Kind { get; set; }
    /// <summary>The unqualified name a name-keyed indexer keys on (<c>m_2</c> for <c>ns_1::Cls_4::m_2</c>).</summary>
    public string? SimpleName { get; set; }
    /// <summary>For an <c>instance</c>, the <c>template</c> symbol it instantiates (D5).</summary>
    public string? InstanceOf { get; set; }
    /// <summary>A pure virtual method: no body, so its def is the <c>= 0;</c> declaration line (written only when true).</summary>
    public bool Pure { get; set; }
    /// <summary>Calls the compiler adds (base/member ctors, member/base dtors, the dtor at scope exit), D4.</summary>
    public List<string> ImplicitEdges { get; init; } = new();
    public List<VirtualEdge> VirtualEdges { get; init; } = new();
    public List<MemberPtrEdge> MemberPtrEdges { get; init; } = new();

    // --- include web (v1, additive; docs/include-web-design.md) ---

    /// <summary>A template instance: the template header's primary head line (<c>inc/tmpl_3.h:3</c>). The def is the
    /// unit's own #include line that leads to the instantiation, which does not name the symbol.</summary>
    public string? TemplateHead { get; set; }
    /// <summary>A template instance: the template header it was stamped from (<c>inc/tmpl_3.h</c>).</summary>
    public string? IncludedVia { get; set; }
    /// <summary>The def is not the name's literal text (a template instance's def is an #include line), but the refs
    /// are real call sites an indexer should resolve. Unlike <see cref="ExpectedMiss"/>, not part of the truth digest.</summary>
    public bool DefNotLiteral { get; set; }
}

/// <summary>An include-web unit's closure (docs/include-web-design.md), under the top-level <c>closures</c> key.</summary>
public sealed record ClosureRecord(string Unit, long Files, long Bytes, long Reads, long ReadBytes, int Depth, long Defines, int TemplateInstances);

/// <summary>
/// A set of byte-identical files (the dedup target — they MUST collapse to one posting set) plus optional
/// near-identical controls (1 line different — they must NOT collapse). Paths are repo-relative.
/// </summary>
public sealed class DupGroup
{
    public required string Name { get; init; }
    public required string Sha256 { get; init; }
    public List<string> Paths { get; init; } = new();
    public List<string> NearVariants { get; init; } = new();
}

/// <summary>Per-population shape stats surfaced under <c>_meta.populations</c> (additive, v1).</summary>
public sealed record PopulationStat(string Name, long FileCount, long TotalBytes, long IdentCount);

/// <summary>
/// One effective generation knob recorded under <c>_meta.gen</c> (reproducibility). The value is exactly one of
/// int / real / flag / text — tagged so the AOT JSON writer emits the right JSON token without reflection.
/// </summary>
public sealed class GenKnob
{
    public required string Name { get; init; }
    public long? Int { get; init; }
    public double? Real { get; init; }
    public bool? Flag { get; init; }
    public string? Text { get; init; }

    public static GenKnob Of(string name, long v) => new() { Name = name, Int = v };
    public static GenKnob Of(string name, double v) => new() { Name = name, Real = v };
    public static GenKnob Of(string name, bool v) => new() { Name = name, Flag = v };
    public static GenKnob Of(string name, string v) => new() { Name = name, Text = v };
}

/// <summary>The full v1 manifest: <c>_meta</c> + a symbol table keyed by symbol name.</summary>
public sealed class ManifestModel
{
    public int ManifestVersion { get; init; } = 1;
    public required string GeneratorVersion { get; init; }
    public required int Seed { get; init; }
    public required string CorpusRoot { get; init; }

    /// <summary>Optional per-population shape stats for consumer "is this the corpus I expect?" assertions.</summary>
    public List<PopulationStat> Populations { get; } = new();

    /// <summary>
    /// Effective generation knobs (post-scale counts + literal sizes/flags) surfaced under <c>_meta.gen</c> for
    /// reproducibility — the self-contained recipe to regen this corpus byte-identically. Empty ⇒ no block (so a
    /// mutate- or test-built manifest stays unchanged). Additive, v1.
    /// </summary>
    public List<GenKnob> Gen { get; } = new();

    /// <summary>
    /// Declared entry-point symbol names (oracle overlay). Reachability = closure over (edges ∪ indirectEdges)
    /// from these. Emitted even for the linear default so a consumer never guesses the chain entry.
    /// </summary>
    public List<string> Roots { get; } = new();

    /// <summary>Sum of per-symbol <see cref="SymbolEntry.Bytes"/> (phase 4, --oracle-bytes). 0 = absent.</summary>
    public long TotalOracleBytes { get; set; }

    // Insertion order preserved for stable, diffable output.
    public Dictionary<string, SymbolEntry> Symbols { get; } = new();

    /// <summary>Source language when not C (<c>cpp</c> for a v2 manifest); null = C, and no <c>_meta.lang</c>.</summary>
    public string? Lang { get; init; }

    /// <summary>C++ (v2): ctors and dtors of static-storage objects, run before/after main (D6).</summary>
    public List<string> StaticInitRoots { get; } = new();

    /// <summary>C++ (v2): each class's direct bases, in declaration order, so <c>cha</c>/<c>rta</c> can be checked
    /// against the hierarchy. Empty in a v1 manifest.</summary>
    public List<KeyValuePair<string, List<string>>> Classes { get; } = new();

    /// <summary>Optional duplicate-content groups (dedup / posting-collapse oracle). Empty when unused.</summary>
    public List<DupGroup> DupGroups { get; } = new();

    /// <summary>Include web: the -I search order (corpus-relative) under <c>_meta.includeDirs</c>. Empty = no web.</summary>
    public List<string> IncludeDirs { get; } = new();

    /// <summary>Include web: each unit's closure, the top-level <c>closures</c> object. Empty = no web.</summary>
    public List<ClosureRecord> Closures { get; } = new();
}
