namespace CodeSpawner.Cli;

public sealed class VerifyOptions
{
    public required string Corpus { get; set; }
    /// <summary>Manifest path; defaults to the <c>&lt;corpus&gt;-manifest.json</c> sibling the generator writes.</summary>
    public string? Manifest { get; set; }
}
