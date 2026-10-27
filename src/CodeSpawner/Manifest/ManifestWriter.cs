using System.Text.Json;

namespace CodeSpawner.Manifest;

/// <summary>
/// Serializes a <see cref="ManifestModel"/> to the v1 schema (v2 for a C++ corpus, whose fields are written only
/// when present, so a C manifest's bytes are unchanged) with a streaming
/// <see cref="Utf8JsonWriter"/> — AOT-safe (no reflection) and cheap even for tens of thousands of
/// symbols. Shape: <c>{ "_meta": {...}, "symbols": { name: { def, refs, edges, unreachableRefs? } } }</c>.
/// </summary>
public static class ManifestWriter
{
    public static void Write(ManifestModel m, string path) =>
        AtomicFile.Write(path, fs => WriteTo(m, fs), bufferSize: 1 << 20);   // never a truncated manifest

    private static void WriteTo(ManifestModel m, Stream fs)
    {
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });

        w.WriteStartObject();

        w.WriteStartObject("_meta");
        w.WriteNumber("manifestVersion", m.ManifestVersion);
        w.WriteString("generatorVersion", m.GeneratorVersion);
        w.WriteNumber("seed", m.Seed);
        w.WriteString("corpusRoot", m.CorpusRoot);
        if (m.Lang is not null) w.WriteString("lang", m.Lang);
        if (m.Gen.Count > 0)
        {
            // Effective generation knobs — the recipe to regen this corpus byte-identically. Additive, v1.
            w.WriteStartObject("gen");
            foreach (var k in m.Gen)
            {
                if (k.Int is { } i) w.WriteNumber(k.Name, i);
                else if (k.Real is { } r) w.WriteNumber(k.Name, r);
                else if (k.Flag is { } b) w.WriteBoolean(k.Name, b);
                else if (k.Text is { } t) w.WriteString(k.Name, t);
            }
            w.WriteEndObject();
        }
        if (m.Populations.Count > 0)
        {
            // Per-population shape so a consumer can assert the corpus is what it expects before pass/fail.
            w.WriteStartObject("populations");
            foreach (var p in m.Populations)
            {
                w.WriteStartObject(p.Name);
                w.WriteNumber("fileCount", p.FileCount);
                w.WriteNumber("totalBytes", p.TotalBytes);
                w.WriteNumber("identCount", p.IdentCount);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        if (m.Roots.Count > 0)
        {
            // Declared entry points for the reachability closure (oracle overlay). Additive, v1.
            w.WriteStartArray("roots");
            foreach (var r in m.Roots) w.WriteStringValue(r);
            w.WriteEndArray();
        }
        if (m.StaticInitRoots.Count > 0)
        {
            // C++ (v2): ctors/dtors of static-storage objects, entry points beside roots (D6).
            w.WriteStartArray("staticInitRoots");
            foreach (var r in m.StaticInitRoots) w.WriteStringValue(r);
            w.WriteEndArray();
        }
        if (m.Classes.Count > 0)
        {
            // C++ (v2): class -> direct bases, the hierarchy cha/rta are drawn from.
            w.WriteStartObject("classes");
            foreach (var (cls, bases) in m.Classes)
            {
                w.WriteStartArray(cls);
                foreach (var b in bases) w.WriteStringValue(b);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        // Component digest over indirect edges + roots — present only when indirect edges exist. Catches
        // indirect-edge / root drift without touching the frozen primary prevTruthSha. Additive, v1.
        if (IndirectDigest.Any(m.Symbols))
            w.WriteString("indirectTruthSha", IndirectDigest.Compute(m.Symbols, m.Roots));
        if (m.ManifestVersion >= 2)
            w.WriteString("cppTruthSha", CppDigest.Compute(m.Symbols, m.StaticInitRoots));
        if (m.IncludeDirs.Count > 0)
        {
            // Include web: the -I order the closures were computed with. Additive, v1.
            w.WriteStartArray("includeDirs");
            foreach (var d in m.IncludeDirs) w.WriteStringValue(d);
            w.WriteEndArray();
        }
        if (m.TotalOracleBytes > 0)
            w.WriteNumber("totalOracleBytes", m.TotalOracleBytes); // byte-mass ground truth (--oracle-bytes)
        w.WriteEndObject();

        w.WriteStartObject("symbols");
        foreach (var (name, s) in m.Symbols)
        {
            w.Spill();
            w.WriteStartObject(name);
            w.WriteString("def", s.Def);
            if (s.Kind is not null) w.WriteString("kind", s.Kind);
            if (s.SimpleName is not null) w.WriteString("simpleName", s.SimpleName);
            if (s.InstanceOf is not null) w.WriteString("instanceOf", s.InstanceOf);
            if (s.Pure) w.WriteBoolean("pure", true);

            w.WriteStartArray("refs");
            foreach (var r in s.Refs) w.WriteStringValue(r);
            w.WriteEndArray();

            w.WriteStartArray("edges");
            foreach (var e in s.Edges) w.WriteStringValue(e);
            w.WriteEndArray();

            if (s.ImplicitEdges.Count > 0)
            {
                w.WriteStartArray("implicitEdges");
                foreach (var e in s.ImplicitEdges) w.WriteStringValue(e);
                w.WriteEndArray();
            }
            if (s.VirtualEdges.Count > 0)
            {
                w.WriteStartArray("virtualEdges");
                foreach (var ve in s.VirtualEdges)
                {
                    w.WriteStartObject();
                    w.WriteString("static", ve.Static);
                    w.WriteStartArray("cha");
                    foreach (var t in ve.Cha) w.WriteStringValue(t);
                    w.WriteEndArray();
                    w.WriteStartArray("rta");
                    foreach (var t in ve.Rta) w.WriteStringValue(t);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (s.MemberPtrEdges.Count > 0)
            {
                w.WriteStartArray("memberPtrEdges");
                foreach (var me in s.MemberPtrEdges)
                {
                    w.WriteStartObject();
                    w.WriteString("target", me.Target);
                    w.WriteString("via", me.Via.Label());
                    w.WriteBoolean("dispatched", me.Dispatched);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            WriteIndirectEdges(w, s.IndirectEdges);

            if (s.UnreachableRefs is { Count: > 0 })
            {
                w.WriteStartArray("unreachableRefs");
                foreach (var u in s.UnreachableRefs) w.WriteStringValue(u);
                w.WriteEndArray();
            }
            if (s.Bytes > 0) w.WriteNumber("bytes", s.Bytes); // byte mass of the def span (--oracle-bytes)
            if (s.ExpectedMiss) w.WriteBoolean("expectedMiss", true);
            if (s.TemplateHead is not null) w.WriteString("templateHead", s.TemplateHead);
            if (s.IncludedVia is not null) w.WriteString("includedVia", s.IncludedVia);
            if (s.DefNotLiteral) w.WriteBoolean("defNotLiteral", true);
            w.WriteEndObject();
        }
        w.WriteEndObject();

        if (m.DupGroups.Count > 0)
        {
            // Top-level: byte-identical groups (must collapse) + near-variant controls (must not).
            w.WriteStartObject("dupGroups");
            foreach (var d in m.DupGroups)
            {
                w.Spill();
                w.WriteStartObject(d.Name);
                w.WriteString("sha256", d.Sha256);
                w.WriteStartArray("paths");
                foreach (var p in d.Paths) w.WriteStringValue(p);
                w.WriteEndArray();
                if (d.NearVariants.Count > 0)
                {
                    w.WriteStartArray("nearVariants");
                    foreach (var nv in d.NearVariants) w.WriteStringValue(nv);
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }

        if (m.Closures.Count > 0)
        {
            // Top-level: each include-web unit's closure (docs/include-web-design.md). Additive, v1.
            w.WriteStartObject("closures");
            foreach (var c in m.Closures)
            {
                w.Spill();
                w.WriteStartObject(c.Unit);
                w.WriteNumber("files", c.Files);
                w.WriteNumber("bytes", c.Bytes);
                w.WriteNumber("reads", c.Reads);
                w.WriteNumber("readBytes", c.ReadBytes);
                w.WriteNumber("depth", c.Depth);
                w.WriteNumber("defines", c.Defines);
                w.WriteNumber("templateInstances", c.TemplateInstances);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }

        if (m.Firmware is not null) WriteFirmware(w, m.Firmware);

        w.WriteEndObject();
        w.Flush();
    }

    // Top-level: the firmware corpus's truth (docs/firmware-design.md). Additive, v1.
    private static void WriteFirmware(Utf8JsonWriter w, CodeSpawner.Firmware.FirmwareResult r)
    {
        var t = r.Truth;
        w.WriteStartObject("firmware");
        w.WriteString("root", CodeSpawner.Firmware.FwSpec.FwDir);
        w.WriteString("compileDb", CodeSpawner.Firmware.FwSpec.DbPath);
        w.WriteNumber("maxParseBytes", CodeSpawner.Firmware.FwSpec.MaxParseBytes);
        w.WriteString("treeSha256", t.TreeSha);
        w.WriteString("cases", CodeSpawner.Firmware.FirmwareEmitter.CasesPath);
        w.WriteStartObject("counts");
        w.WriteNumber("units", r.Units);
        w.WriteNumber("families", r.Families);
        w.WriteNumber("traps", r.Traps);
        w.WriteNumber("cases", r.Cases);
        w.WriteNumber("bigHeadersKept", t.BigHeadersKept);
        w.WriteNumber("denseHeaders", t.DenseHeaders);
        w.WriteNumber("wholeIncludedFromOutside", t.WholeIncludedFromOutside);
        w.WriteNumber("pasteMacros", t.PasteMacros);
        w.WriteNumber("forwardingMacros", t.ForwardingMacros);
        w.WriteNumber("totalDefineLines", t.TotalDefineLines);
        w.WriteNumber("mustKeep", t.MustKeepCount);
        w.WriteNumber("mustDrop", t.MustDropCount);
        w.WriteNumber("open", t.OpenCount);
        w.WriteNumber("bytesBefore", t.BytesBefore);
        w.WriteNumber("definesDroppedMin", t.DefinesDroppedMin);
        w.WriteNumber("definesDroppedMax", t.DefinesDroppedMax);
        w.WriteNumber("bytesAfterMin", t.BytesAfterMin);
        w.WriteNumber("bytesAfterMax", t.BytesAfterMax);
        w.WriteEndObject();
        w.WriteStartArray("bigHeaders");
        foreach (var p in t.BigHeaders) w.WriteStringValue(p);
        w.WriteEndArray();
        w.WriteStartArray("denseHeaders");
        foreach (var d in t.Dense)
        {
            w.WriteStartObject();
            w.WriteString("path", d.Path);
            w.WriteBoolean("edge", d.Edge);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("wholeIncludedFromOutside");
        foreach (var p in t.WholeFromOutside) w.WriteStringValue(p);
        w.WriteEndArray();
        w.WriteStartArray("hostile");
        foreach (var h in r.Hostile)
        {
            w.WriteStartObject();
            w.WriteString("path", h.Path);
            w.WriteString("kind", h.Kind);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (r.Work is { } wk) WriteWork(w, wk);
        w.WriteStartObject("builds");
        foreach (var b in new[] { "main", "union" })
        {
            w.WriteStartObject(b);
            void L(string key, CodeSpawner.Firmware.FwList l)
            {
                w.WriteStartObject(key);
                w.WriteString("path", CodeSpawner.Firmware.FirmwareEmitter.TruthDir + "/" + (b == "main" ? l.Path : "union" + l.Path!["main".Length..]));
                w.WriteString("sha256", l.Sha256);
                w.WriteNumber("count", l.Count);
                w.WriteEndObject();
            }
            L("mustKeepDefines", t.MustKeepDefines);
            L("mustDropDefines", t.MustDropDefines);
            L("openDefines", t.OpenDefines);
            L("mustKeepFunctions", t.MustKeepFunctions);
            L("mustKeepFiles", t.MustKeepFiles);
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }

    // The work carve's construction counts (docs/firmware-design.md "Work shape"): the flat fields CodeCarver asked for
    // (1.4.11), then the use tree's in "tree". Verify recounts the class ones from the truth lists.
    private static void WriteWork(Utf8JsonWriter w, CodeSpawner.Firmware.FwWork k)
    {
        var wd = k.Wide;
        long tree = k.Exact + k.CrossDecoys + k.HalfDecoys + k.FillerWritten;
        w.WriteStartObject("work");
        w.WriteString("dir", CodeSpawner.Firmware.FwSpec.FwDir + "/" + CodeSpawner.Firmware.FwWork.Dir);
        w.WriteNumber("headers", k.Headers);
        w.WriteNumber("defines", tree + k.Pasters + k.Forwarders + k.Appliers + wd.Defines);
        w.WriteNumber("exactUsed", k.Exact + k.Pasters + k.Forwarders + k.Appliers + 2 * wd.Patterns + CodeSpawner.Firmware.FwWide.EdgeDefines);
        w.WriteNumber("openByPattern", wd.Matched);
        w.WriteNumber("openByBody", wd.Named + wd.UsedPasters + wd.UsedForwarders);
        w.WriteNumber("nearMissDropped", wd.Near);
        w.WriteNumber("patterns", wd.Patterns);
        w.WriteNumber("patternsTwoSided", wd.Two);
        w.WriteNumber("patternsPrefixOnly", wd.Pre);
        w.WriteNumber("patternsSuffixOnly", wd.Suf);
        w.WriteNumber("namedOnly", wd.Named);
        w.WriteNumber("pasteMacros", wd.UsedPasters + wd.UnusedPasters);
        w.WriteNumber("pasteMacrosUsed", wd.UsedPasters);
        w.WriteNumber("forwarders", wd.UsedForwarders + wd.UnusedForwarders);
        w.WriteNumber("forwardersUsed", wd.UsedForwarders);
        w.WriteNumber("uses", wd.Uses);
        w.WriteNumber("pieces", wd.Pieces);
        w.WriteNumber("piecesStart", wd.PiecesS);
        w.WriteNumber("piecesEnd", wd.PiecesE);
        w.WriteNumber("edges", CodeSpawner.Firmware.FwWide.EdgeDefines);
        w.WriteNumber("uncoveredFiles", k.Uncovered);
        w.WriteNumber("asmFiles", k.Asm);
        w.WriteStartObject("tree");
        w.WriteNumber("defines", tree);
        w.WriteNumber("exactKept", k.Exact);
        w.WriteNumber("crossDecoys", k.CrossDecoys);
        w.WriteNumber("halfDecoys", k.HalfDecoys);
        w.WriteNumber("filler", k.FillerWritten);
        w.WriteNumber("keptUnderGlobalPools", k.Exact + k.CrossDecoys);
        w.WriteNumber("pasters", k.Pasters);
        w.WriteNumber("forwarders", k.Forwarders);
        w.WriteNumber("appliers", k.Appliers);
        w.WriteStartObject("uses");
        w.WriteNumber("direct", k.UsesDirect);
        w.WriteNumber("forwarded", k.UsesForwarded);
        w.WriteNumber("higherOrder", k.UsesHigherOrder);
        w.WriteEndObject();
        w.WriteNumber("levels", k.Level.Length);
        w.WriteNumber("expectedPasses", k.Level.Length + 1);
        w.WriteStartArray("byLevel");
        long cum = 0, cross = 0;
        for (int i = 0; i < k.Level.Length; i++)
        {
            cum += k.Level[i];
            cross += k.CrossByLevel[i];
            w.WriteStartObject();
            w.WriteNumber("level", i);
            w.WriteNumber("uses", k.Level[i]);
            w.WriteNumber("startPieces", k.Level[i]);
            w.WriteNumber("endPieces", k.Level[i]);
            w.WriteNumber("piecesSoFar", cum);
            w.WriteNumber("crossDecoysLiveSoFar", cross);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    /// <summary>A symbol's <c>indirectEdges</c> array (omitted when it has none), as in the base manifest and in a
    /// symbol delta's changed entries.</summary>
    internal static void WriteIndirectEdges(Utf8JsonWriter w, List<IndirectEdge> edges)
    {
        if (edges.Count == 0) return;
        w.WriteStartArray("indirectEdges");
        foreach (var ie in edges)
        {
            w.WriteStartObject();
            w.WriteString("target", ie.Target);
            w.WriteString("via", ie.Via.Label());
            w.WriteBoolean("dispatched", ie.Dispatched);
            w.WriteBoolean("resolved", ie.Resolved);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
