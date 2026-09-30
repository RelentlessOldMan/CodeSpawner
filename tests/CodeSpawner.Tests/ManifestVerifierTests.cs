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
}
