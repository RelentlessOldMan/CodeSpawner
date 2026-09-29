namespace CodeSpawner.Profile;

/// <summary>
/// The content-class taxonomy from docs/scan-design.md. A file's class is what drives a consumer's cost —
/// a 10 MB inline-function header parses in full (expensive, mints real graph nodes) while a 10 MB
/// <c>#define</c> header is auto-skipped (cheap, mints none), so <c>gen --from-profile</c> reproduces the
/// CLASS, not just the byte count. Values are STABLE labels: append new members, never renumber (the label
/// strings are the profile contract).
/// </summary>
public enum ContentClass
{
    PreprocessorDense,       // high #define-line fraction — cheap to parse, huge macro table
    InlineFunctionHeavy,     // high density of "){...}" bodies in a header — EXPENSIVE parse
    EnumStructTable,         // high enum/struct/table-row density — parse-heavy
    XMacro,                  // repeated FOO(a,b)-style macro-invocation lines — detector blind spot
    TemplateMetaprogramming, // high template</nested-angle density (C++) — libclang blows up
    TabularData,             // delimiter-regular rows (csv-like) — cheap, count pressure
    MinifiedLongLine,        // few lines, MB-long lines — line-scan / block-index path
    GenericCode,             // mixed decl/stmt density — baseline
    DataBlobHighEntropy,     // high-byte low-symbol + high trigram entropy (hex/base64) — balloons index
    DataBlobRepetitive,      // high-byte low-symbol + low trigram entropy — cheap (low cardinality)
    Text,                    // printable, no code markers
    Binary,                  // low printable ratio / high entropy
}

/// <summary>Parse-cost band a class carries, surfaced beside every archetype (CodeCarver, 2026-09-29).</summary>
public enum ParseCost
{
    Cheap,      // skipped or trivially parsed — near-zero node minting
    Baseline,   // parses in full at ordinary node density
    Heavy,      // parses in full AND mints many nodes/edges — the blowup classes
}

public static class ContentClasses
{
    /// <summary>The stable profile label for a class (kebab-case, matches docs/scan-design.md).</summary>
    public static string Label(this ContentClass c) => c switch
    {
        ContentClass.PreprocessorDense => "preprocessor-dense",
        ContentClass.InlineFunctionHeavy => "inline-function-heavy",
        ContentClass.EnumStructTable => "enum-struct-table",
        ContentClass.XMacro => "x-macro",
        ContentClass.TemplateMetaprogramming => "template-metaprogramming",
        ContentClass.TabularData => "tabular-data",
        ContentClass.MinifiedLongLine => "minified-longline",
        ContentClass.GenericCode => "generic-code",
        ContentClass.DataBlobHighEntropy => "data-blob-high-entropy",
        ContentClass.DataBlobRepetitive => "data-blob-repetitive",
        ContentClass.Text => "text",
        ContentClass.Binary => "binary",
        _ => "generic-code",
    };

    public static bool TryParse(string label, out ContentClass c)
    {
        c = label switch
        {
            "preprocessor-dense" => ContentClass.PreprocessorDense,
            "inline-function-heavy" => ContentClass.InlineFunctionHeavy,
            "enum-struct-table" => ContentClass.EnumStructTable,
            "x-macro" => ContentClass.XMacro,
            "template-metaprogramming" => ContentClass.TemplateMetaprogramming,
            "tabular-data" => ContentClass.TabularData,
            "minified-longline" => ContentClass.MinifiedLongLine,
            "generic-code" => ContentClass.GenericCode,
            "data-blob-high-entropy" => ContentClass.DataBlobHighEntropy,
            "data-blob-repetitive" => ContentClass.DataBlobRepetitive,
            "text" => ContentClass.Text,
            "binary" => ContentClass.Binary,
            _ => (ContentClass)(-1),
        };
        return (int)c >= 0;
    }

    /// <summary>Parse-cost band. Heavy = parses in full and mints many nodes (inline-fn / enum-struct / template).</summary>
    public static ParseCost Cost(this ContentClass c) => c switch
    {
        ContentClass.InlineFunctionHeavy => ParseCost.Heavy,
        ContentClass.EnumStructTable => ParseCost.Heavy,
        ContentClass.TemplateMetaprogramming => ParseCost.Heavy,
        ContentClass.GenericCode => ParseCost.Baseline,
        _ => ParseCost.Cheap,
    };

    public static string Label(this ParseCost p) => p switch
    {
        ParseCost.Cheap => "cheap",
        ParseCost.Baseline => "baseline",
        ParseCost.Heavy => "heavy",
        _ => "baseline",
    };

    /// <summary>
    /// Classes counted toward <c>parsedSourceBytes</c> — the parse headline. Source (.c/.cpp) and headers
    /// that actually feed the front end: NOT preprocessor-dense (auto-skipped), NOT blobs/data/text/binary.
    /// </summary>
    public static bool IsParsedSource(this ContentClass c) => c switch
    {
        ContentClass.InlineFunctionHeavy => true,
        ContentClass.EnumStructTable => true,
        ContentClass.TemplateMetaprogramming => true,
        ContentClass.XMacro => true,
        ContentClass.GenericCode => true,
        _ => false,
    };
}
