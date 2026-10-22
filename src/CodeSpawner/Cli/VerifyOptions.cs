namespace CodeSpawner.Cli;

public sealed class VerifyOptions
{
    public required string Corpus { get; set; }
    /// <summary>Manifest path; defaults to the <c>&lt;corpus&gt;-manifest.json</c> sibling the generator writes.</summary>
    public string? Manifest { get; set; }
    /// <summary>Writes a reference carve of the firmware corpus here (fw/ and ext/ with every non-must-keep carved define
    /// removed, plus a compile db), for compiling against the truth.</summary>
    public string? ReferenceCarve { get; set; }
}
