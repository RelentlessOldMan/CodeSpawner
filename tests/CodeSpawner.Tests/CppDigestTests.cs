using CodeSpawner.Manifest;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The cppTruthSha canonical form (docs/cpp-profile-design.md D7) is shared with CodeCarver + CodeCompass, which
/// reproduce it byte-for-byte. The golden vector was computed by an independent Python implementation of the
/// spec text; digest-selftest pins the same vector inside the exe. The rest are the invariants: order and
/// duplicate independence, and that each field the form includes changes the hash.
/// </summary>
public class CppDigestTests
{
    public const string Golden = "28b4e1de981d282a6918de91846e990955f5b8220c88efc993d2a984c0bfed08";
    private const string Empty = "ef46daaadb109c0b6983c0208db641275e4914a4354d0a2c1a588ac79c045a5b";   // sha256(GS x4)

    public static Dictionary<string, SymbolEntry> GoldenSyms() => new(StringComparer.Ordinal)
    {
        ["ns::f"] = new()
        {
            Def = "a:1", Kind = "function",
            VirtualEdges = { new() { Static = "ns::A::m_0", Cha = { "ns::B::m_0", "ns::A::m_0" }, Rta = { "ns::A::m_0" } } },
            ImplicitEdges = { "ns::A::~A" },
            MemberPtrEdges = { new() { Target = "ns::A::m_1", Via = MemberPtrVia.Arg, Dispatched = false } },
        },
        ["ns::g"] = new()
        {
            Def = "a:2", Kind = "function",
            MemberPtrEdges = { new() { Target = "ns::B::m_1", Via = MemberPtrVia.Table, Dispatched = true } },
        },
        ["ns::A::m_0"] = new() { Def = "b:1", Kind = "method" },
        ["ns::B::m_0"] = new() { Def = "b:2", Kind = "method" },
        ["ns::A::m_1"] = new() { Def = "b:3", Kind = "method" },
        ["ns::B::m_1"] = new() { Def = "b:4", Kind = "method" },
        ["ns::A::A"] = new() { Def = "b:5", Kind = "ctor" },
        ["ns::A::~A"] = new() { Def = "b:6", Kind = "dtor" },
        ["ns::B::B"] = new() { Def = "b:7", Kind = "ctor", ImplicitEdges = { "ns::A::A" } },
        ["ns::tf"] = new() { Def = "c:1", Kind = "template" },
        ["ns::tf<int>"] = new() { Def = "c:1", Kind = "instance", InstanceOf = "ns::tf" },
    };

    public static readonly string[] GoldenStaticInit = { "ns::A::A", "ns::A::~A" };

    private static string Of(Dictionary<string, SymbolEntry> s, IEnumerable<string>? si = null) => CppDigest.Compute(s, si ?? GoldenStaticInit);

    [Fact]
    public void ReproducesGoldenVector() => Assert.Equal(Golden, Of(GoldenSyms()));

    [Fact]
    public void Empty_IsFourSeparators() => Assert.Equal(Empty, CppDigest.Compute(new Dictionary<string, SymbolEntry>(), Array.Empty<string>()));

    [Fact]
    public void IgnoresOrderAndDuplicates()
    {
        var s = GoldenSyms();
        var v = s["ns::f"].VirtualEdges[0];
        v.Cha.Reverse();
        v.Cha.Add("ns::A::m_0");
        v.Rta.Add("ns::A::m_0");
        s["ns::f"].ImplicitEdges.Add("ns::A::~A");
        s["ns::f"].VirtualEdges.Add(new() { Static = v.Static, Cha = new(v.Cha), Rta = new(v.Rta) });
        var rev = new Dictionary<string, SymbolEntry>(s.Reverse(), StringComparer.Ordinal);
        Assert.Equal(Golden, Of(rev, GoldenStaticInit.Reverse().Append("ns::A::A")));
    }

    // Each field the form covers must move the hash; a field the form leaves out (def, refs, edges) must not.
    [Theory]
    [InlineData("static")]
    [InlineData("cha")]
    [InlineData("rta")]
    [InlineData("implicit")]
    [InlineData("mpTarget")]
    [InlineData("mpVia")]
    [InlineData("mpDispatched")]
    [InlineData("kind")]
    [InlineData("instanceOf")]
    [InlineData("staticInit")]
    [InlineData("kindDropped")]
    public void EachCoveredField_ChangesHash(string field)
    {
        var s = GoldenSyms();
        var si = GoldenStaticInit.ToList();
        var f = s["ns::f"];
        switch (field)
        {
            case "static": f.VirtualEdges[0] = new() { Static = "ns::B::m_0", Cha = f.VirtualEdges[0].Cha, Rta = f.VirtualEdges[0].Rta }; break;
            case "cha": f.VirtualEdges[0].Cha.Remove("ns::B::m_0"); break;
            case "rta": f.VirtualEdges[0].Rta.Add("ns::B::m_0"); break;
            case "implicit": f.ImplicitEdges[0] = "ns::A::A"; break;
            case "mpTarget": f.MemberPtrEdges[0] = new() { Target = "ns::B::m_1", Via = MemberPtrVia.Arg }; break;
            case "mpVia": f.MemberPtrEdges[0] = new() { Target = "ns::A::m_1", Via = MemberPtrVia.Table }; break;
            case "mpDispatched": f.MemberPtrEdges[0] = new() { Target = "ns::A::m_1", Via = MemberPtrVia.Arg, Dispatched = true }; break;
            case "kind": s["ns::A::m_1"].Kind = "function"; break;
            case "instanceOf": s["ns::tf<int>"].InstanceOf = "ns::tf2"; break;
            case "staticInit": si.RemoveAt(1); break;
            case "kindDropped": s["ns::B::B"].Kind = null; break;
        }
        Assert.NotEqual(Golden, Of(s, si));
    }

    [Fact]
    public void UncoveredFields_DoNotChangeHash()
    {
        var s = GoldenSyms();
        s["ns::f"].Def = "z:9";
        s["ns::f"].Refs.Add("q:1");
        s["ns::f"].Edges.Add("ns::g");
        s["ns::f"].SimpleName = "f";
        Assert.Equal(Golden, Of(s));
    }
}
