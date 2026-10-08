using System.Text.RegularExpressions;
using CodeSpawner.Cli;
using CodeSpawner.Generation;
using CodeSpawner.Manifest;
using CodeSpawner.Profile;
using CodeSpawner.Verify;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The verifier is the contract's guardian — a generator change that breaks the manifest must fail HERE, once,
/// not silently downstream. These drive it end-to-end over a real oracle corpus (the only way to exercise its
/// file-reading logic) and, crucially, prove the NEW oracle-v1 checks actually catch tampering: an undeclared
/// root, a resolved indirect target that isn't a symbol, and a drifted indirectTruthSha.
/// </summary>
public class ManifestVerifierTests
{
    private static (string corpus, string manifest, ManifestModel model) Build(TempDir tmp, GenOptions o)
    {
        string corpus = tmp.Path;
        var m = new OracleOverlay(o, new ProfileModel(), corpus, new List<string> { corpus }).Emit();
        string mp = Path.Combine(corpus, "manifest.json");
        ManifestWriter.Write(m, mp);
        return (corpus, mp, m);
    }

    private static int Verify(string corpus, string manifest)
        => ManifestVerifier.Run(new VerifyOptions { Corpus = corpus, Manifest = manifest });

    private static GenOptions FullOracle() => new()
    {
        Out = "x", Seed = 1337, WithOracle = true,
        OracleChain = 8, OracleFanout = 2, OracleReachableFrac = 0.5, OracleIndirect = 6, OracleBytes = true,
    };

    [Fact]
    public void CleanOracleCorpus_Passes()
    {
        using var tmp = new TempDir();
        var (c, mp, _) = Build(tmp, FullOracle());
        Assert.Equal(0, Verify(c, mp));
    }

    [Fact]
    public void LinearDefaultCorpus_Passes()
    {
        using var tmp = new TempDir();
        var (c, mp, _) = Build(tmp, new GenOptions { Out = "x", Seed = 1, WithOracle = true, OracleChain = 5 });
        Assert.Equal(0, Verify(c, mp));
    }

    [Fact]
    public void UndeclaredRoot_Fails()
    {
        using var tmp = new TempDir();
        var m = new OracleOverlay(new GenOptions { Out = "x", Seed = 1, WithOracle = true, OracleChain = 4 },
            new ProfileModel(), tmp.Path, new List<string> { tmp.Path }).Emit();
        m.Roots.Add("func_does_not_exist");                     // inject a bogus entry point post-validation
        string mp = Path.Combine(tmp.Path, "manifest.json");
        ManifestWriter.Write(m, mp);
        Assert.NotEqual(0, Verify(tmp.Path, mp));
    }

    [Fact]
    public void ResolvedIndirectTarget_ThatIsNotASymbol_Fails()
    {
        using var tmp = new TempDir();
        var m = new OracleOverlay(new GenOptions { Out = "x", Seed = 1, WithOracle = true, OracleChain = 4 },
            new ProfileModel(), tmp.Path, new List<string> { tmp.Path }).Emit();
        m.Symbols["func_0"].IndirectEdges.Add(
            new IndirectEdge { Target = "ghost_symbol", Via = IndirectVia.FnPtr, Dispatched = false, Resolved = true });
        string mp = Path.Combine(tmp.Path, "manifest.json");
        ManifestWriter.Write(m, mp);
        Assert.NotEqual(0, Verify(tmp.Path, mp));
    }

    [Fact]
    public void DriftedIndirectTruthSha_Fails()
    {
        using var tmp = new TempDir();
        var (c, mp, _) = Build(tmp, FullOracle());
        string tampered = Regex.Replace(File.ReadAllText(mp),
            "\"indirectTruthSha\": \"[0-9a-f]{64}\"",
            "\"indirectTruthSha\": \"" + new string('0', 64) + "\"");
        File.WriteAllText(mp, tampered);
        Assert.NotEqual(0, Verify(c, mp));
    }

    [Fact]
    public void TamperedDefLine_Fails()
    {
        // Sanity that the harness detects a genuine spine break, not just the new checks.
        using var tmp = new TempDir();
        var (c, mp, _) = Build(tmp, new GenOptions { Out = "x", Seed = 3, WithOracle = true, OracleChain = 5 });
        string bad = File.ReadAllText(mp).Replace("src_1.c:", "src_1.c:9999");
        File.WriteAllText(mp, bad);
        Assert.NotEqual(0, Verify(c, mp));
    }

    // --- crafted-manifest failure modes (each proves the oracle CATCHES a specific defect) ---

    // Write a raw manifest string + an optional tiny corpus, then verify.
    private static int VerifyRaw(TempDir tmp, string manifestJson, Action<string>? corpus = null)
    {
        string c = Path.Combine(tmp.Path, "c");
        Directory.CreateDirectory(c);
        corpus?.Invoke(c);
        string mp = Path.Combine(tmp.Path, "m.json");
        File.WriteAllText(mp, manifestJson);
        return ManifestVerifier.Run(new VerifyOptions { Corpus = c, Manifest = mp });
    }

    private static string Sha(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    [Fact]
    public void InvalidJsonManifest_Returns2()
    {
        using var tmp = new TempDir();
        Assert.Equal(2, VerifyRaw(tmp, "{ this is not json "));
    }

    [Fact]
    public void MissingMetaOrSymbols_Fails()
    {
        using var tmp = new TempDir();
        Assert.Equal(1, VerifyRaw(tmp, @"{""symbols"":{}}"));                         // no _meta
        Assert.Equal(1, VerifyRaw(tmp, @"{""_meta"":{""manifestVersion"":1}}"));      // no symbols
        Assert.NotEqual(0, VerifyRaw(tmp, @"{""_meta"":{""manifestVersion"":2},""symbols"":{}}"));  // wrong version
    }

    [Fact]
    public void EdgeToMissingSymbol_Fails()
    {
        using var tmp = new TempDir();
        int rc = VerifyRaw(tmp,
            @"{""_meta"":{""manifestVersion"":1},""symbols"":{""foo"":{""def"":""a.c:1"",""edges"":[""ghost""]}}}",
            c => File.WriteAllText(Path.Combine(c, "a.c"), "int foo(void){return 0;}\n"));
        Assert.NotEqual(0, rc);   // def resolves, but edge target 'ghost' is not a declared symbol
    }

    [Fact]
    public void GatedRef_NotBehindIfdef_Fails()
    {
        using var tmp = new TempDir();
        int rc = VerifyRaw(tmp,
            @"{""_meta"":{""manifestVersion"":1},""symbols"":{""vendor_gated"":{""def"":""a.c:1"",""unreachableRefs"":[""a.c:2""]}}}",
            c => File.WriteAllText(Path.Combine(c, "a.c"), "int vendor_gated(void);\n  vendor_gated(1);\n"));
        Assert.NotEqual(0, rc);   // the gated call is NOT behind #ifdef VENDOR_OK
    }

    [Fact]
    public void PathEscapingSite_Fails()
    {
        using var tmp = new TempDir();
        int rc = VerifyRaw(tmp,
            @"{""_meta"":{""manifestVersion"":1},""symbols"":{""foo"":{""def"":""../evil.c:1""}}}");
        Assert.NotEqual(0, rc);   // a '..' site escaping the corpus root is rejected
    }

    [Fact]
    public void ExpectedMiss_ButSymbolAppearsLiterally_Fails()
    {
        using var tmp = new TempDir();
        int rc = VerifyRaw(tmp,
            @"{""_meta"":{""manifestVersion"":1},""symbols"":{""patho_x"":{""def"":""a.c:1"",""expectedMiss"":true}}}",
            c => File.WriteAllText(Path.Combine(c, "a.c"), "int patho_x(void){return 0;}\n"));
        Assert.NotEqual(0, rc);   // an expected-miss symbol that is lexically present is NOT an honest miss
    }

    [Fact]
    public void DupGroup_HashMismatch_Fails()
    {
        using var tmp = new TempDir();
        int rc = VerifyRaw(tmp,
            @"{""_meta"":{""manifestVersion"":1},""symbols"":{},""dupGroups"":{""g0"":{""sha256"":""" + new string('0', 64) + @""",""paths"":[""d0.c"",""d1.c""]}}}",
            c => { File.WriteAllText(Path.Combine(c, "d0.c"), "AAAA"); File.WriteAllText(Path.Combine(c, "d1.c"), "AAAA"); });
        Assert.NotEqual(0, rc);   // files don't hash to the recorded sha256
    }

    [Fact]
    public void DupGroup_NearVariantIdentical_Fails()
    {
        using var tmp = new TempDir();
        string sha = Sha("AAAA");
        int rc = VerifyRaw(tmp,
            @"{""_meta"":{""manifestVersion"":1},""symbols"":{},""dupGroups"":{""g0"":{""sha256"":""" + sha + @""",""paths"":[""d0.c"",""d1.c""],""nearVariants"":[""near.c""]}}}",
            c => { File.WriteAllText(Path.Combine(c, "d0.c"), "AAAA"); File.WriteAllText(Path.Combine(c, "d1.c"), "AAAA"); File.WriteAllText(Path.Combine(c, "near.c"), "AAAA"); });
        Assert.NotEqual(0, rc);   // the near-variant must DIFFER from the group, but here it's identical
    }

    [Fact]
    public void DupGroup_CleanGroupAndDistinctNearVariant_Passes()
    {
        using var tmp = new TempDir();
        string sha = Sha("AAAA");
        int rc = VerifyRaw(tmp,
            @"{""_meta"":{""manifestVersion"":1},""symbols"":{},""dupGroups"":{""g0"":{""sha256"":""" + sha + @""",""paths"":[""d0.c"",""d1.c""],""nearVariants"":[""near.c""]}}}",
            c => { File.WriteAllText(Path.Combine(c, "d0.c"), "AAAA"); File.WriteAllText(Path.Combine(c, "d1.c"), "AAAA"); File.WriteAllText(Path.Combine(c, "near.c"), "AAAB"); });
        Assert.Equal(0, rc);   // identical group + a genuinely different near-variant ⇒ clean
    }

    [Fact]
    public void ExpectedMiss_WithNoDefSite_Fails()
    {
        using var tmp = new TempDir();
        int rc = VerifyRaw(tmp,
            @"{""_meta"":{""manifestVersion"":1},""symbols"":{""patho_x"":{""expectedMiss"":true}}}");
        Assert.NotEqual(0, rc);   // an expectedMiss entry carrying no def site is malformed
    }

    [Fact]
    public void Indirect_UnresolvedTarget_ThatIsADeclaredSymbol_Fails()
    {
        using var tmp = new TempDir();
        // Closed-world rule: an UNRESOLVED (external) indirect target must NOT be a declared symbol.
        int rc = VerifyRaw(tmp,
            @"{""_meta"":{""manifestVersion"":1},""symbols"":{" +
            @"""foo"":{""def"":""a.c:1"",""indirectEdges"":[{""target"":""bar"",""resolved"":false}]}," +
            @"""bar"":{""def"":""a.c:2""}}}",
            c => File.WriteAllText(Path.Combine(c, "a.c"), "int foo(void);\nint bar(void);\n"));
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public void GatedRef_SiteMissingTheSymbolToken_Fails()
    {
        using var tmp = new TempDir();
        // The gated site must at least contain the symbol token; here line 2 does not mention vendor_gated.
        int rc = VerifyRaw(tmp,
            @"{""_meta"":{""manifestVersion"":1},""symbols"":{""vendor_gated"":{""def"":""a.c:1"",""unreachableRefs"":[""a.c:2""]}}}",
            c => File.WriteAllText(Path.Combine(c, "a.c"), "int vendor_gated(void);\n  other_call();\n"));
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public void GatedRef_WellFormed_ButVendorHeaderPresent_Fails()
    {
        using var tmp = new TempDir();
        // The gated ref is behind #ifdef VENDOR_OK and names the symbol, but a VENDOR_missing_*.h exists in
        // the tree — which breaks the unresolved-include premise the gating depends on.
        int rc = VerifyRaw(tmp,
            @"{""_meta"":{""manifestVersion"":1},""symbols"":{""vendor_gated"":{""def"":""a.c:1"",""unreachableRefs"":[""a.c:3""]}}}",
            c =>
            {
                File.WriteAllText(Path.Combine(c, "a.c"),
                    "int vendor_gated(void);\n#ifdef VENDOR_OK\n  vendor_gated(1);\n#endif\n");
                File.WriteAllText(Path.Combine(c, "VENDOR_missing_zlib.h"), "// premise says this must not exist\n");
            });
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public void DupGroup_MissingSha256_IsMalformed_Fails()
    {
        using var tmp = new TempDir();
        int rc = VerifyRaw(tmp,
            @"{""_meta"":{""manifestVersion"":1},""symbols"":{},""dupGroups"":{""g0"":{""paths"":[""d0.c""]}}}",
            c => File.WriteAllText(Path.Combine(c, "d0.c"), "AAAA"));
        Assert.NotEqual(0, rc);   // a dup group with no sha256 is malformed
    }

    [Fact]
    public void DupGroup_NearVariantFileMissing_Fails()
    {
        using var tmp = new TempDir();
        string sha = Sha("AAAA");
        int rc = VerifyRaw(tmp,
            @"{""_meta"":{""manifestVersion"":1},""symbols"":{},""dupGroups"":{""g0"":{""sha256"":""" + sha + @""",""paths"":[""d0.c"",""d1.c""],""nearVariants"":[""ghost.c""]}}}",
            c => { File.WriteAllText(Path.Combine(c, "d0.c"), "AAAA"); File.WriteAllText(Path.Combine(c, "d1.c"), "AAAA"); });
        Assert.NotEqual(0, rc);   // the declared near-variant file does not exist on disk
    }
}
