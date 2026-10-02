using CodeSpawner.Manifest;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The diffTruthSha canonical form is LOCKED with CodeDiffer (code-spawner chat, 2026-10-02): a reason-flip,
/// sha-flip, hunk shift, or rename change must trip it, and both tools reproduce it byte-for-byte.
/// digest-selftest pins the golden vector inside the exe; these add the surrounding invariants — order
/// independence, all-four-sections-always-present, and that each digested field actually moves the hash.
/// </summary>
public class DiffDigestTests
{
    private const string Golden = "66c7e62566ee105e63dce7e770d47e1a9fbe71b86d50249e209f5bf41f03d542";

    // The exact vector digest-selftest freezes: out-of-order files, coalesced + multi explicit hunks, a
    // giant-file run-rule, empty renames.
    private static List<DiffFile> GoldenFiles() => new()
    {
        new() { Path = "z/last.c",  OldSha = "o1", NewSha = "n1", OldSize = 100, NewSize = 110,
                Hunks = { new Hunk(HunkOp.Replace, 5, 2, 5, 2) } },
        new() { Path = "a/first.c", OldSha = "o2", NewSha = "n2", OldSize = 200, NewSize = 205,
                Hunks = { new Hunk(HunkOp.Replace, 1, 1, 1, 1), new Hunk(HunkOp.Replace, 9, 3, 9, 3) } },
        new() { Path = "big.h",     OldSha = "o3", NewSha = "n3", OldSize = 1048576, NewSize = 1050000,
                Run = new RunHunk(HunkOp.Replace, 20, 1, 5000, 1) },
    };

    [Fact]
    public void ReproducesGoldenVector()
        => Assert.Equal(Golden, DiffDigest.Compute(GoldenFiles(), Array.Empty<Rename>()));

    [Fact]
    public void IsLowercaseHex64()
        => Assert.Matches("^[0-9a-f]{64}$", DiffDigest.Compute(GoldenFiles(), Array.Empty<Rename>()));

    [Fact]
    public void IndependentOfFileInsertionOrder()
    {
        var reordered = new List<DiffFile>
        {
            new() { Path = "big.h",     OldSha = "o3", NewSha = "n3", OldSize = 1048576, NewSize = 1050000,
                    Run = new RunHunk(HunkOp.Replace, 20, 1, 5000, 1) },
            new() { Path = "a/first.c", OldSha = "o2", NewSha = "n2", OldSize = 200, NewSize = 205,
                    Hunks = { new Hunk(HunkOp.Replace, 9, 3, 9, 3), new Hunk(HunkOp.Replace, 1, 1, 1, 1) } },
            new() { Path = "z/last.c",  OldSha = "o1", NewSha = "n1", OldSize = 100, NewSize = 110,
                    Hunks = { new Hunk(HunkOp.Replace, 5, 2, 5, 2) } },
        };
        Assert.Equal(Golden, DiffDigest.Compute(reordered, Array.Empty<Rename>()));
    }

    [Fact]
    public void ReasonFlip_ChangesHash()
    {
        var a = new List<DiffFile> { new() { Path = "x.c", Reason = "content", OldSha = "a", NewSha = "b", OldSize = 1, NewSize = 2 } };
        var b = new List<DiffFile> { new() { Path = "x.c", Reason = "eol",     OldSha = "a", NewSha = "b", OldSize = 1, NewSize = 2 } };
        Assert.NotEqual(DiffDigest.Compute(a, Array.Empty<Rename>()), DiffDigest.Compute(b, Array.Empty<Rename>()));
    }

    [Fact]
    public void ShaFlip_ChangesHash()
    {
        var a = new List<DiffFile> { new() { Path = "x.c", OldSha = "a", NewSha = "b", OldSize = 1, NewSize = 2 } };
        var b = new List<DiffFile> { new() { Path = "x.c", OldSha = "a", NewSha = "c", OldSize = 1, NewSize = 2 } };
        Assert.NotEqual(DiffDigest.Compute(a, Array.Empty<Rename>()), DiffDigest.Compute(b, Array.Empty<Rename>()));
    }

    [Fact]
    public void HunkShift_ChangesHash()
    {
        var a = new List<DiffFile> { new() { Path = "x.c", OldSha = "a", NewSha = "b", OldSize = 1, NewSize = 2, Hunks = { new Hunk(HunkOp.Replace, 5, 1, 5, 1) } } };
        var b = new List<DiffFile> { new() { Path = "x.c", OldSha = "a", NewSha = "b", OldSize = 1, NewSize = 2, Hunks = { new Hunk(HunkOp.Replace, 6, 1, 6, 1) } } };
        Assert.NotEqual(DiffDigest.Compute(a, Array.Empty<Rename>()), DiffDigest.Compute(b, Array.Empty<Rename>()));
    }

    [Fact]
    public void RunRuleStride_ChangesHash()
    {
        var a = new List<DiffFile> { new() { Path = "g.h", OldSha = "a", NewSha = "b", OldSize = 9, NewSize = 9, Run = new RunHunk(HunkOp.Replace, 10, 1, 100, 1) } };
        var b = new List<DiffFile> { new() { Path = "g.h", OldSha = "a", NewSha = "b", OldSize = 9, NewSize = 9, Run = new RunHunk(HunkOp.Replace, 20, 1, 100, 1) } };
        Assert.NotEqual(DiffDigest.Compute(a, Array.Empty<Rename>()), DiffDigest.Compute(b, Array.Empty<Rename>()));
    }

    [Fact]
    public void Rename_ParticipatesInTheDigest()
    {
        var files = new List<DiffFile>();
        string none = DiffDigest.Compute(files, Array.Empty<Rename>());
        string withR = DiffDigest.Compute(files, new[] { new Rename("a.c", "b.c", 900) });
        Assert.NotEqual(none, withR);
        // similarity is digested too — a different similarity on the same {from,to} is a different hash.
        string withR2 = DiffDigest.Compute(files, new[] { new Rename("a.c", "b.c", 600) });
        Assert.NotEqual(withR, withR2);
    }

    [Fact]
    public void EmptyDelta_IsStableHashOfAllEmptySections()
    {
        // No files, no renames: all four sections present but empty ⇒ a fixed, reproducible digest.
        string a = DiffDigest.Compute(new List<DiffFile>(), Array.Empty<Rename>());
        string b = DiffDigest.Compute(new List<DiffFile>(), Array.Empty<Rename>());
        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }
}
