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
                    "--out, so choose an --out nested at least one level below a drive root, or pass --manifest false.");
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

        Timed("tiny files", () => EmitTinyFiles(tree, nCsv));

        Timed("compile_commands", () => CompileDbEmitter.Write(_o, outFull, src.Files));

        if (_o.Manifest)
        {
            var model = Timed("manifest", () =>
            {
                var m = BuildManifest(outFull, src, unres, broad, stats);
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

    private ManifestModel BuildManifest(string outFull, SourceEmitResult src, UnresolvedEmitResult? unres,
        BroadTokenEmitResult? broad, PopulationStats stats)
    {
        var model = new ManifestModel
        {
            GeneratorVersion = Program.Version,
            Seed = _o.Seed,
            CorpusRoot = outFull,
        };
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
