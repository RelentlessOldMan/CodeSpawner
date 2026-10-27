using System.Diagnostics;
using CodeSpawner.Cli;
using CodeSpawner.Manifest;
using CodeSpawner.Firmware;

namespace CodeSpawner.Generation;

/// <summary>
/// Orchestrates a full corpus generation: directory skeleton, the bimodal register headers, ordinary
/// headers, data blobs, the cross-file source call graph, the unresolved-include negative case, tiny
/// files, an optional compile database, and the v1 ground-truth manifest.
/// </summary>
public sealed class CorpusGenerator
{
    private readonly GenOptions _o;

    // Dropped at the corpus root to mark a directory as CodeSpawner-generated, so a re-run may safely
    // clear it without --force while a directory we did NOT create is protected from accidental deletion.
    private const string MarkerName = OutputDir.MarkerName;

    public CorpusGenerator(GenOptions o) => _o = o;

    public void Run()
    {
        var sw = Stopwatch.StartNew();

        string outFull = new DirectoryInfo(_o.Out).FullName;
        // Fail FAST if the sibling manifest location isn't writable — before the old corpus is wiped, not after
        // emitting GBs.
        string mpath = ManifestPath(outFull);
        if (_o.Manifest) ProbeManifestWritable(mpath);

        PrepareOutputDir();
        ResolveIoParallelism(outFull);

        Console.WriteLine(
            $"Fabricating corpus at {outFull} (scale {_o.Scale}, seed {_o.Seed}, compileDb={_o.CompileDb}) ...");

        var tree = DirTree.Build(_o, outFull);
        var stats = new PopulationStats();

        // Effective counts (explicit knobs literal; else default * scale). Giants keep >=1 while enabled.
        int nGiant = _o.Eff("GiantHeaders", _o.GiantHeaders, keepOne: true);
        int nBig = _o.Eff("BigHeaders", _o.BigHeaders);
        int nMed = _o.Eff("MedHeaders", _o.MedHeaders);
        int nSmallH = _o.Eff("OrdinaryHeaders", _o.OrdinaryHeaders);
        int nC = Math.Max(2, _o.Eff("CFiles", _o.CFiles)); // need >=2 for a cross-file reference edge
        int nCsv = _o.Eff("TinyFiles", _o.TinyFiles);
        int nBlob = _o.Eff("BlobFiles", _o.BlobFiles);
        int nDense = _o.Eff("DenseHeaders", _o.DenseHeaders);
        int nBroad = _o.Eff("BroadTokenFiles", _o.BroadTokenFiles);
        int nLong = _o.Eff("LongLineFiles", _o.LongLineFiles);
        int nEnc = _o.Eff("EncodingMix", _o.EncodingMix);
        int nPatho = _o.Eff("PathologicalSymbols", _o.PathologicalSymbols);
        int nDup = _o.Eff("DupGroups", _o.DupGroups);

        var giantPaths = Timed("register headers", () => EmitHeaders(tree, nGiant, nBig, nMed, nDense, stats));
        Console.WriteLine($"    ^ {nGiant} giant (<={_o.MaxHeaderMB}MB) + {nBig} big + {nMed} medium + {nDense} dense (<{_o.DenseUnderMb}MB)");

        Timed("ordinary headers", () => EmitOrdinaryHeaders(tree, nSmallH));
        Timed("data blobs", () => EmitBlobs(tree, nBlob));

        var src = Timed("source call graph", () => SourceEmitter.Emit(_o, tree, giantPaths, nC, nSmallH));
        Console.WriteLine(
            $"    ^ {src.GiantIncluders} .c co-located with + including a giant header, all calling hot_shared()");

        var unres = Timed("unresolved includes", () => UnresolvedIncludeEmitter.Emit(_o, tree, nC));
        if (unres is not null)
            Console.WriteLine(
                $"    ^ {unres.UnreachableRefs.Count} UNREACHABLE ref(s) to vendor_gated()");

        BroadTokenEmitResult? broad = null;
        if (nBroad > 0)
        {
            broad = Timed("broad-token", () => BroadTokenEmitter.Emit(_o, tree, nBroad, stats));
            Console.WriteLine($"    ^ {broad.Refs.Count} hot-token ref(s) across {nBroad} carrier(s) (2-8MB)");
        }
        if (nLong > 0)
        {
            Timed("long-lines", () => LongLineEmitter.Emit(_o, tree, nLong, stats));
            Console.WriteLine($"    ^ {nLong} file(s), {_o.MaxLineBytes / (1024 * 1024)}MB/line{(_o.NoNewline ? ", no newline" : ", alt newline")}");
        }
        if (nEnc > 0)
        {
            Timed("encoding-mix", () => EncodingMixEmitter.Emit(_o, tree, nEnc, stats));
            Console.WriteLine($"    ^ {nEnc} file(s): UTF-16LE/BE, UTF-8-BOM, invalid bytes, non-ASCII idents");
        }

        List<PathoSym>? patho = null;
        if (nPatho > 0)
        {
            patho = Timed("pathological-symbols", () => PathologicalSymbolEmitter.Emit(_o, tree, nPatho, stats));
            int miss = patho.Count(s => s.ExpectedMiss);
            Console.WriteLine($"    ^ {patho.Count} symbols across {nPatho} file(s) ({miss} expected-miss token-paste)");
        }
        List<DupGroupResult>? dups = null;
        if (nDup > 0)
        {
            dups = Timed("dup-content", () => DupContentEmitter.Emit(_o, tree, nDup, _o.DupCopies, stats));
            Console.WriteLine($"    ^ {nDup} group(s) x {Math.Max(2, _o.DupCopies)} identical + 1 near-variant");
        }

        if (_o.ShrinkSeeds > 0 || _o.RestreamSeeds > 0)
        {
            Timed("mutate seeds", () => EmitMutateSeeds(tree, _o.ShrinkSeeds, _o.RestreamSeeds, stats));
            Console.WriteLine($"    ^ {_o.ShrinkSeeds} shrink seed(s) (~9MB) + {_o.RestreamSeeds} restream seed(s) (~129MB) for mutate");
        }

        Timed("tiny files", () => EmitTinyFiles(tree, nCsv));

        WebEmitResult? web = null;
        if (_o.WebUnits > 0)
        {
            web = Timed("include web", () => WebEmitter.Emit(_o, outFull, stats, HeaderParallel));
            Console.WriteLine($"    ^ {WebSummary(web)}");
        }

        Timed("compile_commands", () => CompileDbEmitter.Write(_o, outFull, src.Files, web?.Units));

        FirmwareResult? fw = null;
        if (_o.Firmware)
        {
            fw = Timed("firmware", () => FirmwareEmitter.Emit(_o, outFull, HeaderParallel));
            Console.WriteLine($"    ^ {fw.Units} units, {fw.Truth.BigHeadersKept} big + {fw.Truth.DenseHeaders} dense headers; must keep {fw.Truth.MustKeepCount:N0}, must drop {fw.Truth.MustDropCount:N0}, open {fw.Truth.OpenCount:N0}");
        }

        if (_o.Manifest)
        {
            var model = Timed("manifest", () =>
            {
                var m = BuildManifest(outFull, src, unres, broad, patho, dups, web, stats);
                m.Firmware = fw;
                ManifestWriter.Write(m, mpath);
                return m;
            });
            Console.WriteLine($"    ^ {mpath} ({model.Symbols.Count} symbols, {model.Populations.Count} populations)");
        }

        Report(tree, outFull, sw.Elapsed);
    }

    // Time a phase and print "  phase ... N.Ns". Phase timings are the profiling signal at scale.
    private static T Timed<T>(string label, Func<T> work)
    {
        var sw = Stopwatch.StartNew();
        T result = work();
        sw.Stop();
        Console.WriteLine($"  {label} ... {sw.Elapsed.TotalSeconds:N1}s");
        return result;
    }

    private static void Timed(string label, Action work) => Timed(label, () => { work(); return 0; });

    /// <summary>
    /// Pick header write concurrency from the target disk when the user didn't set <c>--io-parallelism</c>.
    /// SSD/NVMe love parallel writes; a spinning disk thrashes (measured ~5x slower), so it wants 1.
    /// </summary>
    private void ResolveIoParallelism(string outFull)
    {
        if (_o.WasSet("io-parallelism"))
        {
            Console.WriteLine($"  io-parallelism: {_o.IoParallelism} (set explicitly)");
            return;
        }
        bool? ssd = DiskProbe.IsSolidState(outFull);
        _o.IoParallelism = ssd switch
        {
            true => Environment.ProcessorCount,               // SSD/NVMe: fan out fully
            false => 1,                                        // HDD: sequential, avoid head thrash
            null => Math.Min(Environment.ProcessorCount, 4),   // unknown (network/other): safe middle
        };
        string kind = ssd switch { true => "SSD", false => "HDD", null => "unknown disk" };
        Console.WriteLine($"  io-parallelism: {_o.IoParallelism} (auto for {kind}; override with --io-parallelism)");
    }

    // Large multi-GB sequential writes: bounded concurrency so we don't thrash the disk.
    private ParallelOptions HeaderParallel => new() { MaxDegreeOfParallelism = Math.Max(1, _o.IoParallelism) };
    // Tens of thousands of small files: fan out across all cores (syscall-bound).
    private static ParallelOptions SmallFileParallel => new() { MaxDegreeOfParallelism = Environment.ProcessorCount };

    /// <summary>
    /// Clear and recreate the output directory (the guards against a mistyped <c>--out</c> wiping real data
    /// live in <see cref="OutputDir"/>, shared with the profile generator).
    /// </summary>
    private void PrepareOutputDir() => OutputDir.Prepare(_o.Out, _o.Force, _o.LinkedRoots);

    /// <summary>
    /// The first block-family number of each reg-header band, woven into every macro name (<c>HWIO_BLK{fam}_...</c>).
    /// The historical bases (giant 0, big 100, med 1000, dense 100000, shrink 200000, restream 300000) are kept
    /// while the bands fit; a band that would run into the next pushes the next one up, so no two headers ever
    /// share a family (1.1.13: before, more than 100 giants or 900 big+med headers reused family numbers).
    /// </summary>
    internal static (int Big, int Med, int Dense, int Shrink, int Restream) FamilyBases(
        int nGiant, int nBig, int nMed, int nDense, int nShrink)
    {
        int big = Math.Max(100, nGiant);
        int med = Math.Max(1000, big + nBig);
        int dense = Math.Max(100_000, med + nMed);
        int shrink = Math.Max(200_000, dense + nDense);
        int restream = Math.Max(300_000, shrink + nShrink);
        return (big, med, dense, shrink, restream);
    }

    private (int Big, int Med, int Dense, int Shrink, int Restream) _fam;

    private List<string> EmitHeaders(DirTree tree, int nGiant, int nBig, int nMed, int nDense, PopulationStats stats)
    {
        _fam = FamilyBases(nGiant, nBig, nMed, nDense, _o.ShrinkSeeds);
        // Giants fill to MaxHeaderMB by default; an explicit -macro-density caps the define count instead
        // (the "many macros, few bytes" memory axis). Either way a giant carries ~1M+ #defines.
        long giantDefineCap = _o.WasSet("MacroDensity") ? _o.MacroDensity : long.MaxValue;
        long giantMaxBytes = (long)_o.MaxHeaderMB * 1024 * 1024;

        var giantPaths = new string[nGiant]; // assigned by index -> order-stable under parallelism
        Parallel.For(0, nGiant, HeaderParallel, i =>
        {
            var rng = Rng.For(_o.Seed, Category.GiantHeader, i);
            string p = Path.Combine(tree.PickDir(ref rng), $"regmap_block{i}.h");
            var s = RegHeaderEmitter.Write(p, giantDefineCap, giantMaxBytes, i);
            stats.Add("giant-headers", 1, s.Bytes, s.Idents);
            giantPaths[i] = p;
        });
        Parallel.For(0, nBig, HeaderParallel, i =>
        {
            var rng = Rng.For(_o.Seed, Category.BigHeader, i);
            long bytes = (long)rng.Next(10, 100) * 1024 * 1024;
            var s = RegHeaderEmitter.Write(Path.Combine(tree.PickDir(ref rng), $"regbig_{i}.h"), 2_000_000, bytes, _fam.Big + i);
            stats.Add("big-headers", 1, s.Bytes, s.Idents);
        });
        Parallel.For(0, nMed, HeaderParallel, i =>
        {
            var rng = Rng.For(_o.Seed, Category.MedHeader, i);
            long bytes = (long)rng.Next(1, 10) * 1024 * 1024;
            var s = RegHeaderEmitter.Write(Path.Combine(tree.PickDir(ref rng), $"regmed_{i}.h"), 200_000, bytes, _fam.Med + i);
            stats.Add("med-headers", 1, s.Bytes, s.Idents);
        });
        // dense-band: headers parked just under DenseUnderMb with maximally-unique idents (their own family band,
        // see FamilyBases). Fill to the byte ceiling (defines uncapped).
        long denseMaxBytes = (long)_o.DenseUnderMb * 1024 * 1024;
        Parallel.For(0, nDense, HeaderParallel, i =>
        {
            var rng = Rng.For(_o.Seed, Category.DenseHeader, i);
            var s = RegHeaderEmitter.Write(Path.Combine(tree.PickDir(ref rng), $"dense_{i}.h"), long.MaxValue, denseMaxBytes, _fam.Dense + i);
            stats.Add("dense-band", 1, s.Bytes, s.Idents);
        });
        return [.. giantPaths];
    }

    private void EmitOrdinaryHeaders(DirTree tree, int n) =>
        Parallel.For(0, n, SmallFileParallel, i =>
        {
            var rng = Rng.For(_o.Seed, Category.OrdinaryHeader, i);
            OrdinaryHeaderEmitter.Write(tree.PickDir(ref rng), i);
        });

    // Sidecar-straddle seeds for mutate's threshold edits: ~9 MB (4-shrink target) and ~129 MB (4-restream).
    // Reg-header shaped (unique idents) so CodeCompass sidecars them; not tracked as manifest symbols.
    private void EmitMutateSeeds(DirTree tree, int nShrink, int nRestream, PopulationStats stats)
    {
        Parallel.For(0, nShrink, HeaderParallel, i =>
        {
            var rng = Rng.For(_o.Seed, Category.MutateSeed, i);
            var s = RegHeaderEmitter.Write(Path.Combine(tree.PickDir(ref rng), $"mut_shrink_{i}.h"), long.MaxValue, 9L * 1024 * 1024, _fam.Shrink + i);
            stats.Add("mutate-shrink-seed", 1, s.Bytes, s.Idents);
        });
        Parallel.For(0, nRestream, HeaderParallel, i =>
        {
            var rng = Rng.For(_o.Seed, Category.MutateSeed, 1_000_000 + i);
            var s = RegHeaderEmitter.Write(Path.Combine(tree.PickDir(ref rng), $"mut_restream_{i}.h"), long.MaxValue, 129L * 1024 * 1024, _fam.Restream + i);
            stats.Add("mutate-restream-seed", 1, s.Bytes, s.Idents);
        });
    }

    private void EmitBlobs(DirTree tree, int n) =>
        Parallel.For(0, n, SmallFileParallel, i =>
        {
            var rng = Rng.For(_o.Seed, Category.Blob, i);
            string dir = tree.PickDir(ref rng);
            BlobEmitter.Write(dir, i, ref rng);
        });

    private void EmitTinyFiles(DirTree tree, int n) =>
        Parallel.For(0, n, SmallFileParallel, i =>
        {
            var rng = Rng.For(_o.Seed, Category.TinyFile, i);
            string dir = tree.PickDir(ref rng);
            TinyFileEmitter.Write(dir, i, ref rng);
        });

    /// <summary>The include web's knobs, recorded only when it is on, so a manifest without one keeps its bytes.</summary>
    internal static void AddWebKnobs(GenOptions o, List<GenKnob> g)
    {
        if (o.WebUnits > 0)
        {
            g.Add(GenKnob.Of("webUnits", (long)o.WebUnits));
            g.Add(GenKnob.Of("webHeaders", (long)o.WebHeaders));
            g.Add(GenKnob.Of("webDepth", (long)o.WebDepth));
            g.Add(GenKnob.Of("webWindow", o.WebWindow));
            g.Add(GenKnob.Of("webAggregators", (long)o.WebAggregators));
            g.Add(GenKnob.Of("webHeaderLines", (long)o.WebHeaderLines));
            g.Add(GenKnob.Of("webTail", (long)o.WebTail));
            g.Add(GenKnob.Of("webTailMb", (long)o.WebTailMb));
            g.Add(GenKnob.Of("webTemplates", (long)o.WebTemplates));
            g.Add(GenKnob.Of("webTemplateSites", (long)o.WebTemplateSites));
            g.Add(GenKnob.Of("webTemplateHeads", (long)o.WebTemplateHeads));
            g.Add(GenKnob.Of("webTemplateReachFrac", o.WebTemplateReachFrac));
            g.Add(GenKnob.Of("webTemplateHiddenFrac", o.WebTemplateHiddenFrac));
            g.Add(GenKnob.Of("webCycleFrac", o.WebCycleFrac));
            g.Add(GenKnob.Of("webIfdefFrac", o.WebIfdefFrac));
            g.Add(GenKnob.Of("webContinuationFrac", o.WebContinuationFrac));
            g.Add(GenKnob.Of("webCommentFrac", o.WebCommentFrac));
            g.Add(GenKnob.Of("webUndefFrac", o.WebUndefFrac));
            g.Add(GenKnob.Of("webComputedFrac", o.WebComputedFrac));
            g.Add(GenKnob.Of("webNextFrac", o.WebNextFrac));
            g.Add(GenKnob.Of("webShadow", (long)o.WebShadow));
            g.Add(GenKnob.Of("webRelativeFrac", o.WebRelativeFrac));
            g.Add(GenKnob.Of("webCrlfFrac", o.WebCrlfFrac));
            g.Add(GenKnob.Of("webMixedEolFrac", o.WebMixedEolFrac));
        }
    }

    /// <summary>The firmware corpus's knobs, recorded only when it is on, so a manifest without one keeps its bytes.</summary>
    internal static void AddFirmwareKnobs(GenOptions o, List<GenKnob> g)
    {
        if (!o.Firmware) return;
        g.Add(GenKnob.Of("firmware", true));
        g.Add(GenKnob.Of("fwBigHeaders", (long)o.Eff("fw-big-headers", o.FwBigHeaders, keepOne: true)));
        g.Add(GenKnob.Of("fwFiles", (long)o.Eff("fw-files", o.FwFiles)));
        g.Add(GenKnob.Of("fwFamilies", (long)o.Eff("fw-families", o.FwFamilies)));
        g.Add(GenKnob.Of("fwPasters", (long)o.Eff("fw-pasters", o.FwPasters)));
        g.Add(GenKnob.Of("fwDenseHeaders", (long)o.Eff("fw-dense-headers", o.FwDenseHeaders)));
        g.Add(GenKnob.Of("fwBigMb", (long)o.FwBigMb));
        g.Add(GenKnob.Of("fwChainMax", (long)o.FwChainMax));
        g.Add(GenKnob.Of("fwDbPaths", o.FwDbPaths));
        if (!o.FwWork) return;
        g.Add(GenKnob.Of("fwWork", true));
        g.Add(GenKnob.Of("fwWorkDefines", (long)o.Eff("fw-work-defines", o.FwWorkDefines)));
        g.Add(GenKnob.Of("fwWorkPasters", (long)o.Eff("fw-work-pasters", o.FwWorkPasters, keepOne: true)));
        g.Add(GenKnob.Of("fwWorkForwarders", (long)o.Eff("fw-work-forwarders", o.FwWorkForwarders)));
        g.Add(GenKnob.Of("fwWorkDepth", (long)o.FwWorkDepth));
        g.Add(GenKnob.Of("fwWorkGrowth", (long)o.FwWorkGrowth));
        g.Add(GenKnob.Of("fwWorkKeepPct", o.FwWorkKeepPct));
        g.Add(GenKnob.Of("fwWorkDecoyPct", o.FwWorkDecoyPct));
        g.Add(GenKnob.Of("fwWorkHalfPct", o.FwWorkHalfPct));
        g.Add(GenKnob.Of("fwWorkPatterns", (long)o.Eff("fw-work-patterns", o.FwWorkPatterns)));
        g.Add(GenKnob.Of("fwWorkNearPct", o.FwWorkNearPct));
        g.Add(GenKnob.Of("fwWorkNamedPct", o.FwWorkNamedPct));
        g.Add(GenKnob.Of("fwWorkBodyPasters", (long)o.Eff("fw-work-body-pasters", o.FwWorkBodyPasters)));
        g.Add(GenKnob.Of("fwWorkBodyForwarders", (long)o.Eff("fw-work-body-forwarders", o.FwWorkBodyForwarders)));
        g.Add(GenKnob.Of("fwWorkBodyUses", (long)o.Eff("fw-work-body-uses", o.FwWorkBodyUses)));
        g.Add(GenKnob.Of("fwWorkPieces", (long)o.Eff("fw-work-pieces", o.FwWorkPieces)));
        g.Add(GenKnob.Of("fwWorkUncovered", (long)o.Eff("fw-work-uncovered", o.FwWorkUncovered)));
        g.Add(GenKnob.Of("fwWorkAsm", (long)Math.Max(2, o.Eff("fw-work-asm", o.FwWorkAsm))));
    }

    /// <summary>The include web's truth: its -I order, the template instances as symbols, and the closures.</summary>
    internal static void AddWeb(ManifestModel model, WebEmitResult web)
    {
        model.IncludeDirs.AddRange(WebEmitter.IncludeDirs);
        foreach (var x in web.Instances)
        {
            var e = new SymbolEntry { Def = x.Def, TemplateHead = x.TemplateHead, IncludedVia = x.IncludedVia, DefNotLiteral = true };
            e.Refs.AddRange(x.Refs);
            model.Symbols[x.Name] = e;
        }
        foreach (var c in web.Closures)
            model.Closures.Add(new ClosureRecord(c.Unit, c.Files, c.Bytes, c.Reads, c.ReadBytes, c.Depth, c.Defines, c.TemplateInstances));
    }

    /// <summary>
    /// Record the EFFECTIVE generation knobs under <c>_meta.gen</c> (reproducibility). Counts mirror the exact
    /// <see cref="GenOptions.Eff"/> expressions used to emit each population (explicit → literal, else default ×
    /// scale), so re-running gen with these values (all explicit ⇒ scaling bypassed) reproduces the corpus tree
    /// byte-identically. Sizes / structure / flags are literal. Machine-independent — no absolute paths.
    /// </summary>
    private void AddGenKnobs(ManifestModel m)
    {
        var g = m.Gen;
        g.Add(GenKnob.Of("scale", _o.Scale));
        g.Add(GenKnob.Of("seed", (long)_o.Seed));
        // effective population counts — exactly what was emitted
        g.Add(GenKnob.Of("giantHeaders", (long)_o.Eff("GiantHeaders", _o.GiantHeaders, keepOne: true)));
        g.Add(GenKnob.Of("bigHeaders", (long)_o.Eff("BigHeaders", _o.BigHeaders)));
        g.Add(GenKnob.Of("medHeaders", (long)_o.Eff("MedHeaders", _o.MedHeaders)));
        g.Add(GenKnob.Of("ordinaryHeaders", (long)_o.Eff("OrdinaryHeaders", _o.OrdinaryHeaders)));
        g.Add(GenKnob.Of("cfiles", (long)Math.Max(2, _o.Eff("CFiles", _o.CFiles))));
        g.Add(GenKnob.Of("tinyFiles", (long)_o.Eff("TinyFiles", _o.TinyFiles)));
        g.Add(GenKnob.Of("blobFiles", (long)_o.Eff("BlobFiles", _o.BlobFiles)));
        g.Add(GenKnob.Of("denseHeaders", (long)_o.Eff("DenseHeaders", _o.DenseHeaders)));
        g.Add(GenKnob.Of("broadTokenFiles", (long)_o.Eff("BroadTokenFiles", _o.BroadTokenFiles)));
        g.Add(GenKnob.Of("longLineFiles", (long)_o.Eff("LongLineFiles", _o.LongLineFiles)));
        g.Add(GenKnob.Of("encodingMix", (long)_o.Eff("EncodingMix", _o.EncodingMix)));
        g.Add(GenKnob.Of("pathologicalSymbols", (long)_o.Eff("PathologicalSymbols", _o.PathologicalSymbols)));
        g.Add(GenKnob.Of("dupGroups", (long)_o.Eff("DupGroups", _o.DupGroups)));
        // seed files + tree structure (never scaled)
        g.Add(GenKnob.Of("shrinkSeeds", (long)_o.ShrinkSeeds));
        g.Add(GenKnob.Of("restreamSeeds", (long)_o.RestreamSeeds));
        g.Add(GenKnob.Of("giantIncluders", (long)_o.GiantIncluders));
        g.Add(GenKnob.Of("dirs", (long)Math.Max(1, _o.Eff("Dirs", _o.Dirs))));   // scaled like the counts; 0 builds as 1 (DirTree) and replays as --dirs 1
        g.Add(GenKnob.Of("depth", (long)_o.Depth));
        g.Add(GenKnob.Of("linkedRoots", (long)_o.LinkedRoots));
        g.Add(GenKnob.Of("unresolvedIncludes", (long)_o.UnresolvedIncludes));
        // per-file sizes / scalars (never scaled)
        g.Add(GenKnob.Of("maxHeaderMB", (long)_o.MaxHeaderMB));
        // Only when set: an explicit --macro-density caps giants at that many defines, while the default leaves them
        // uncapped (filled to maxHeaderMB). Recording the default would cap a replay at 1M defines.
        if (_o.WasSet("MacroDensity")) g.Add(GenKnob.Of("macroDensity", (long)_o.MacroDensity));
        g.Add(GenKnob.Of("denseUnderMb", (long)_o.DenseUnderMb));
        g.Add(GenKnob.Of("maxLineBytes", (long)_o.MaxLineBytes));
        g.Add(GenKnob.Of("dupCopies", (long)_o.DupCopies));
        g.Add(GenKnob.Of("hotTokenShare", _o.HotTokenShare));
        g.Add(GenKnob.Of("noNewline", _o.NoNewline));
        g.Add(GenKnob.Of("compileDb", _o.CompileDb.ToString().ToLowerInvariant()));
        g.Add(GenKnob.Of("buildOutput", _o.BuildOutput));
        // Only when varied: recording the default would change every existing manifest's bytes (and baseManifestSha).
        if (_o.VariedSourceBodies) g.Add(GenKnob.Of("sourceBodies", "varied"));
        // oracle overlay
        g.Add(GenKnob.Of("withOracle", _o.WithOracle));
        g.Add(GenKnob.Of("oracleScale", _o.OracleScale));
        g.Add(GenKnob.Of("oracleBytes", _o.OracleBytes));
        g.Add(GenKnob.Of("oracleChain", (long)_o.OracleChain));
        g.Add(GenKnob.Of("oracleFanout", (long)_o.OracleFanout));
        g.Add(GenKnob.Of("oracleDepth", (long)_o.OracleDepth));
        g.Add(GenKnob.Of("oracleSharedLeaves", (long)_o.OracleSharedLeaves));
        g.Add(GenKnob.Of("oracleReachableFrac", _o.OracleReachableFrac));
        g.Add(GenKnob.Of("oracleIndirect", (long)_o.OracleIndirect));
        AddWebKnobs(_o, g);
        AddFirmwareKnobs(_o, g);
        // (no fromProfile knob here: `gen --from-profile` is dispatched to ProfileGenerator, not this path, so
        //  _o.FromProfile is always null here. A profile-genned corpus's reproduction recipe IS the profile file.)
    }

    private ManifestModel BuildManifest(string outFull, SourceEmitResult src, UnresolvedEmitResult? unres,
        BroadTokenEmitResult? broad, List<PathoSym>? patho, List<DupGroupResult>? dups, WebEmitResult? web, PopulationStats stats)
    {
        var model = new ManifestModel
        {
            GeneratorVersion = Program.Version,
            Seed = _o.Seed,
            CorpusRoot = outFull,
        };
        AddGenKnobs(model);
        foreach (var p in stats.Snapshot())
            model.Populations.Add(new PopulationStat(p.Name, p.Files, p.Bytes, p.Idents));

        var byIndex = new Dictionary<int, CFileInfo>(src.Files.Count);
        foreach (var ci in src.Files) byIndex[ci.Index] = ci;

        foreach (var ci in src.Files)
        {
            var entry = new SymbolEntry { Def = $"{PathUtil.Rel(outFull, ci.Path)}:{ci.DefLine}" };
            if (ci.Index > 0) entry.Edges.Add($"func_{ci.Index - 1}");
            // Giant-includer .c files (the first N by index) also call hot_shared() in their body, so that
            // is a real call-graph edge too — omitting it would make hot_shared.c look like carve over-keep.
            if (ci.Index < src.GiantIncluders) entry.Edges.Add("hot_shared");
            // func_i is referenced by func_{i+1}'s call site (in src_{i+1}.c).
            if (byIndex.TryGetValue(ci.Index + 1, out var next) && next.CallLine > 0)
                entry.Refs.Add($"{PathUtil.Rel(outFull, next.Path)}:{next.CallLine}");
            model.Symbols[$"func_{ci.Index}"] = entry;
        }

        if (src.GiantIncluders > 0)
        {
            var hot = new SymbolEntry { Def = $"{PathUtil.Rel(outFull, src.HotPath)}:1" };
            foreach (var r in src.HotRefs) hot.Refs.Add(RelSite(outFull, r));
            model.Symbols["hot_shared"] = hot;
        }

        if (unres is not null)
        {
            var vg = new SymbolEntry { Def = $"{PathUtil.Rel(outFull, unres.VendorGatedPath)}:1" };
            vg.UnreachableRefs = new List<string>(unres.UnreachableRefs.Count);
            foreach (var u in unres.UnreachableRefs) vg.UnreachableRefs.Add(RelSite(outFull, u));
            model.Symbols["vendor_gated"] = vg;
        }

        if (broad is not null)
        {
            // broad_hot: one def, a large expected ref-set (every hot-token call site across the carriers).
            var bh = new SymbolEntry { Def = $"{PathUtil.Rel(outFull, broad.DefPath)}:1" };
            foreach (var r in broad.Refs) bh.Refs.Add(RelSite(outFull, r));
            model.Symbols["broad_hot"] = bh;
        }

        if (patho is not null)
            foreach (var s in patho)
                model.Symbols[s.Name] = new SymbolEntry
                {
                    Def = $"{PathUtil.Rel(outFull, s.Path)}:{s.Line}",
                    ExpectedMiss = s.ExpectedMiss,
                };

        if (dups is not null)
            foreach (var d in dups)
            {
                var g = new DupGroup { Name = d.Name, Sha256 = d.Sha256 };
                foreach (var p in d.Paths) g.Paths.Add(PathUtil.Rel(outFull, p));
                foreach (var nv in d.NearVariants) g.NearVariants.Add(PathUtil.Rel(outFull, nv));
                model.DupGroups.Add(g);
            }

        if (web is not null) AddWeb(model, web);

        return model;
    }

    /// <summary>One line on the web's shape: closure files and MB at p50/p95/max, and how deep templates are reached.</summary>
    internal static string WebSummary(WebEmitResult web)
    {
        static T Pct<T>(List<T> sorted, double p) => sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
        var files = web.Closures.Select(c => c.Files).Order().ToList();
        var mb = web.Closures.Select(c => c.Bytes / (1024.0 * 1024)).Order().ToList();
        int deep = web.Instances.Count(i => i.Depth > 8);
        int maxDepth = web.Instances.Count == 0 ? 0 : web.Instances.Max(i => i.Depth);
        return $"{web.Units.Count} unit(s), {web.TotalBytes / (1024.0 * 1024):N0} MB of headers; closure files p50 {Pct(files, 0.5)} " +
               $"p95 {Pct(files, 0.95)} max {files[^1]}, MB p50 {Pct(mb, 0.5):N1} p95 {Pct(mb, 0.95):N1} max {mb[^1]:N1}; " +
               $"{web.Instances.Count} template instance(s), {deep} deeper than 8 (max depth {maxDepth})";
    }

    // Convert an absolute "path:line" site to a repo-relative one (the last ':' separates the line).
    private static string RelSite(string outFull, string absSite)
    {
        int c = absSite.LastIndexOf(':');
        string abs = absSite[..c];
        string line = absSite[(c + 1)..];
        return $"{PathUtil.Rel(outFull, abs)}:{line}";
    }

    /// <summary>Manifest lands beside the corpus dir: <c>&lt;out&gt;-manifest.json</c>.</summary>
    public static string ManifestPath(string outFull)
    {
        string parent = Path.GetDirectoryName(outFull.TrimEnd(Path.DirectorySeparatorChar))
                        ?? Directory.GetCurrentDirectory();
        string leaf = Path.GetFileName(outFull.TrimEnd(Path.DirectorySeparatorChar));
        return Path.Combine(parent, $"{leaf}-manifest.json");
    }

    /// <summary>Fail fast when the sibling manifest can't be written, before any generation work. Probes with the
    /// writer's own temp name (not the manifest itself), so the probe never truncates a manifest and the real file
    /// only ever appears complete (ManifestWriter writes then renames).</summary>
    internal static void ProbeManifestWritable(string mpath, string optOut = "pass --manifest=false")
    {
        // A directory where the manifest goes would only fail the final rename, after --out was wiped and rewritten.
        if (Directory.Exists(mpath))
            throw new ArgException($"cannot write the manifest at '{mpath}': a directory is in the way; move it" +
                (optOut == "choose another --out" ? " or choose another --out." : $", choose another --out, or {optOut}."));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(mpath)!);   // a new --out's parent; preparing --out makes it anyway
            using (File.Create(mpath + ".tmp", 1, FileOptions.DeleteOnClose)) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgException(
                $"cannot write the manifest at '{mpath}': {ex.Message}. The manifest is a sibling of " +
                "--out, so choose an --out nested at least one level below a drive root, or " + optOut + ".");
        }
    }

    private static void Report(DirTree tree, string outFull, TimeSpan elapsed)
    {
        long total = 0;
        int files = 0;
        foreach (var f in Directory.EnumerateFiles(outFull, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(f) == MarkerName) continue; // internal bookkeeping, not corpus content
            files++;
            try { total += new FileInfo(f).Length; } catch { /* transient; ignore for the tally */ }
        }
        Console.WriteLine(
            $"Done: {files:N0} files, {total / (1024.0 * 1024 * 1024):N2} GB, " +
            $"{tree.Dirs.Count:N0} dirs, {tree.Roots.Count} root(s) in {elapsed.TotalSeconds:N1}s.");
    }
}
