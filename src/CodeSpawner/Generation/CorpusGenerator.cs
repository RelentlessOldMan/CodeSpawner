using System.Diagnostics;
using CodeSpawner.Cli;
using CodeSpawner.Manifest;

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
    private const string MarkerName = ".codespawner";

    public CorpusGenerator(GenOptions o) => _o = o;

    public void Run()
    {
        var sw = Stopwatch.StartNew();

        PrepareOutputDir();
        string outFull = new DirectoryInfo(_o.Out).FullName;

        ResolveIoParallelism(outFull);

        // Fail FAST if the sibling manifest location isn't writable — do not discover it after emitting GBs.
        string mpath = ManifestPath(outFull);
        if (_o.Manifest)
        {
            try { using (File.Create(mpath)) { } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ArgException(
                    $"cannot write the manifest at '{mpath}': {ex.Message}. The manifest is a sibling of " +
                    "--out, so choose an --out nested at least one level below a drive root, or pass --manifest=false.");
            }
        }

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

        Timed("compile_commands", () => CompileDbEmitter.Write(_o, outFull, src.Files));

        if (_o.Manifest)
        {
            var model = Timed("manifest", () =>
            {
                var m = BuildManifest(outFull, src, unres, broad, patho, dups, stats);
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
    /// Clear and recreate the output directory, refusing to delete a non-empty directory we did not
    /// create unless <c>--force</c> is given. Guards against a mistyped <c>--out</c> wiping real data.
    /// </summary>
    private void PrepareOutputDir()
    {
        if (Directory.Exists(_o.Out))
        {
            bool empty = !Directory.EnumerateFileSystemEntries(_o.Out).Any();
            bool ours = File.Exists(Path.Combine(_o.Out, MarkerName));
            if (!empty && !ours && !_o.Force)
                throw new ArgException(
                    $"--out '{_o.Out}' already exists, is not empty, and was not created by CodeSpawner. " +
                    "Refusing to delete it. Pass --force to overwrite, or choose a different --out.");
            Directory.Delete(_o.Out, recursive: true);
        }
        else if (File.Exists(_o.Out))
        {
            throw new ArgException($"--out '{_o.Out}' is a file, not a directory.");
        }

        Directory.CreateDirectory(_o.Out);
        File.WriteAllText(Path.Combine(_o.Out, MarkerName), $"CodeSpawner {Program.Version}\n");
    }

    private List<string> EmitHeaders(DirTree tree, int nGiant, int nBig, int nMed, int nDense, PopulationStats stats)
    {
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
            var s = RegHeaderEmitter.Write(Path.Combine(tree.PickDir(ref rng), $"regbig_{i}.h"), 2_000_000, bytes, 100 + i);
            stats.Add("big-headers", 1, s.Bytes, s.Idents);
        });
        Parallel.For(0, nMed, HeaderParallel, i =>
        {
            var rng = Rng.For(_o.Seed, Category.MedHeader, i);
            long bytes = (long)rng.Next(1, 10) * 1024 * 1024;
            var s = RegHeaderEmitter.Write(Path.Combine(tree.PickDir(ref rng), $"regmed_{i}.h"), 200_000, bytes, 1000 + i);
            stats.Add("med-headers", 1, s.Bytes, s.Idents);
        });
        // dense-band: headers parked just under DenseUnderMb with maximally-unique idents (fam base 100000
        // keeps them distinct from the giant/big/med bands). Fill to the byte ceiling (defines uncapped).
        long denseMaxBytes = (long)_o.DenseUnderMb * 1024 * 1024;
        Parallel.For(0, nDense, HeaderParallel, i =>
        {
            var rng = Rng.For(_o.Seed, Category.DenseHeader, i);
            var s = RegHeaderEmitter.Write(Path.Combine(tree.PickDir(ref rng), $"dense_{i}.h"), long.MaxValue, denseMaxBytes, 100_000 + i);
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
            var s = RegHeaderEmitter.Write(Path.Combine(tree.PickDir(ref rng), $"mut_shrink_{i}.h"), long.MaxValue, 9L * 1024 * 1024, 200_000 + i);
            stats.Add("mutate-shrink-seed", 1, s.Bytes, s.Idents);
        });
        Parallel.For(0, nRestream, HeaderParallel, i =>
        {
            var rng = Rng.For(_o.Seed, Category.MutateSeed, 1_000_000 + i);
            var s = RegHeaderEmitter.Write(Path.Combine(tree.PickDir(ref rng), $"mut_restream_{i}.h"), long.MaxValue, 129L * 1024 * 1024, 300_000 + i);
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
        g.Add(GenKnob.Of("dirs", (long)_o.Dirs));
        g.Add(GenKnob.Of("depth", (long)_o.Depth));
        g.Add(GenKnob.Of("linkedRoots", (long)_o.LinkedRoots));
        g.Add(GenKnob.Of("unresolvedIncludes", (long)_o.UnresolvedIncludes));
        // per-file sizes / scalars (never scaled)
        g.Add(GenKnob.Of("maxHeaderMB", (long)_o.MaxHeaderMB));
        g.Add(GenKnob.Of("macroDensity", (long)_o.MacroDensity));
        g.Add(GenKnob.Of("denseUnderMb", (long)_o.DenseUnderMb));
        g.Add(GenKnob.Of("maxLineBytes", (long)_o.MaxLineBytes));
        g.Add(GenKnob.Of("dupCopies", (long)_o.DupCopies));
        g.Add(GenKnob.Of("hotTokenShare", _o.HotTokenShare));
        g.Add(GenKnob.Of("noNewline", _o.NoNewline));
        g.Add(GenKnob.Of("compileDb", _o.CompileDb.ToString().ToLowerInvariant()));
        g.Add(GenKnob.Of("buildOutput", _o.BuildOutput));
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
        // (no fromProfile knob here: `gen --from-profile` is dispatched to ProfileGenerator, not this path, so
        //  _o.FromProfile is always null here. A profile-genned corpus's reproduction recipe IS the profile file.)
    }

    private ManifestModel BuildManifest(string outFull, SourceEmitResult src, UnresolvedEmitResult? unres,
        BroadTokenEmitResult? broad, List<PathoSym>? patho, List<DupGroupResult>? dups, PopulationStats stats)
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

        return model;
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
