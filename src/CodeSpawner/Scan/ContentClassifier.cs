using CodeSpawner.Profile;

namespace CodeSpawner.Scan;

/// <summary>
/// Per-file content stats and the assigned <see cref="ContentClass"/>. All numeric; the include-target
/// basenames are kept only in-memory to build the include graph and are NEVER emitted to the profile.
/// </summary>
public sealed class FileStats
{
    public ContentClass Class;
    public double FunctionsPerKB, CallsPerFunction, GlobalRefsPerFile;
    public double DefineFrac, CommentFrac, BlankFrac, IncludeFrac, IdentUniqueRatio, AvgIdentLen, AvgLineLen;
    public long MaxLineLen;
    public string Encoding = "ascii";
    public bool Bom;
    public string Newline = "lf";
    public List<string> IncludeTargets = new();  // basenames, lowercased — internal only, never emitted
}

/// <summary>
/// Content-free heuristics that assign a content class from a byte prefix. Pure (no IO): the scanner reads
/// each sampled file once and hands the span here, so classification, density, and trigram metering all run
/// off a single read. Thresholds are FIXED + documented so a coarse profile still regenerates in the right
/// ballpark (CodeCarver, 2026-09-29). Density and class are derived from this one pass.
/// </summary>
public static class ContentClassifier
{
    // --- Fixed density-band thresholds (functions per KB / calls per function / ident refs per file). ---
    private const double FnPerKbLow = 0.5, FnPerKbHigh = 2.0;
    private const double CallsPerFnLow = 1.0, CallsPerFnHigh = 5.0;
    private const double RefsPerFileLow = 100, RefsPerFileHigh = 1000;

    // --- Classification thresholds. ---
    private const double PrintableBinaryCutoff = 0.75; // below this printable ratio -> binary
    private const double BlobDigitFrac = 0.60;         // data-heavy: mostly digits/hex/separators
    private const double DefineDenseFrac = 0.50;       // >=50% of non-blank lines are #define
    private const double XMacroFrac = 0.30;
    private const double TemplateFrac = 0.03;
    private const double TabularFrac = 0.70;           // >=70% of non-blank lines share the modal comma count
    private const long MinifiedLineLen = 100_000;      // a single line this long (with few lines) -> minified
    private const int MinifiedMaxLines = 50;
    private const double BlobHighEntropyBits = 3.0;    // Shannon entropy of byte values on the prefix

    public static FileStats Classify(ReadOnlySpan<byte> raw, string ext)
    {
        var st = new FileStats();

        // --- Encoding / BOM. Strip UTF-16 interleaved nulls so text isn't mis-flagged as binary. ---
        byte[]? scratch = null;
        ReadOnlySpan<byte> data = raw;
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
        { st.Encoding = "utf-8"; st.Bom = true; data = raw[3..]; }
        else if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
        { st.Encoding = "utf-16le"; st.Bom = true; scratch = StripEveryOther(raw[2..], evenOffset: 0); data = scratch; }
        else if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
        { st.Encoding = "utf-16be"; st.Bom = true; scratch = StripEveryOther(raw[2..], evenOffset: 1); data = scratch; }

        long total = data.Length;
        if (total == 0) { st.Class = ContentClass.Text; return st; }

        // --- Byte-level tallies in a single pass. ---
        long printable = 0, digitish = 0;
        var hist = new long[256];
        foreach (byte x in data)
        {
            hist[x]++;
            if (x is >= 0x20 and <= 0x7E || x is 0x09 or 0x0A or 0x0D) printable++;
            if (x is >= (byte)'0' and <= (byte)'9' || x is (byte)'a' or (byte)'b' or (byte)'c' or (byte)'d'
                or (byte)'e' or (byte)'f' or (byte)'A' or (byte)'B' or (byte)'C' or (byte)'D' or (byte)'E'
                or (byte)'F' or (byte)'x' or (byte)',' or (byte)' ' or (byte)'\n') digitish++;
        }
        double printableRatio = (double)printable / total;
        double digitFrac = (double)digitish / total;
        double entropy = 0;
        foreach (long h in hist) { if (h == 0) continue; double p = (double)h / total; entropy -= p * Math.Log2(p); }

        // If it's clearly non-text, decide binary vs data-blob before line work.
        bool utf16 = st.Encoding.StartsWith("utf-16", StringComparison.Ordinal);
        if (!utf16 && printableRatio < PrintableBinaryCutoff)
        {
            st.Class = ContentClass.Binary;
            return st;
        }

        // --- Line + token pass. ---
        long lineCount = 0, blankLines = 0, defineLines = 0, includeLines = 0, commentLines = 0;
        long fnShapes = 0, callSites = 0, templateLines = 0, enumStructLines = 0, tableRows = 0, xMacroLines = 0;
        long identTotal = 0, identLenSum = 0, sumLineLen = 0;
        long maxLineLen = 0, crlf = 0;
        var distinctIdents = new HashSet<string>(StringComparer.Ordinal);
        var commaCounts = new Dictionary<int, int>();

        int start = 0;
        for (int i = 0; i <= data.Length; i++)
        {
            bool eof = i == data.Length;
            if (!eof && data[i] != (byte)'\n') continue;

            int end = i;
            bool hasCr = end > start && data[end - 1] == (byte)'\r';
            if (hasCr) { end--; crlf++; }
            var line = data[start..end];
            start = i + 1;
            lineCount++;

            int len = line.Length;
            sumLineLen += len;
            if (len > maxLineLen) maxLineLen = len;

            int firstNs = FirstNonSpace(line);
            if (firstNs < 0) { blankLines++; continue; }
            var trimmed = line[firstNs..];

            if (StartsWithHash(trimmed, "define")) defineLines++;
            else if (StartsWithHash(trimmed, "include")) { includeLines++; AddInclude(trimmed, st.IncludeTargets); }
            else if (IsComment(trimmed)) commentLines++;

            if (ContainsBraceOpen(line) && !IsControlStmt(trimmed)) fnShapes++;
            if (ContainsTemplate(trimmed)) templateLines++;
            if (StartsWithWord(trimmed, "enum") || StartsWithWord(trimmed, "struct")
                || StartsWithWord(trimmed, "union") || StartsWithWord(trimmed, "typedef")) enumStructLines++;
            if (ContainsSeq(line, (byte)'}', (byte)',')) tableRows++;
            if (IsUpperMacroCall(trimmed)) xMacroLines++;

            // tokens + calls + commas
            int commas = 0;
            int t = 0;
            while (t < line.Length)
            {
                byte ch = line[t];
                if (ch == (byte)',') commas++;
                if (IsIdentStart(ch))
                {
                    int s = t;
                    t++;
                    while (t < line.Length && IsIdentPart(line[t])) t++;
                    int ilen = t - s;
                    identTotal++;
                    identLenSum += ilen;
                    if (distinctIdents.Count < 100_000)
                        distinctIdents.Add(System.Text.Encoding.ASCII.GetString(line[s..t]));
                    if (t < line.Length && line[t] == (byte)'(') callSites++;
                }
                else t++;
            }
            commaCounts.TryGetValue(commas, out int cc);
            commaCounts[commas] = cc + 1;
        }

        st.Newline = crlf > lineCount / 2 ? "crlf" : "lf";
        double nonBlank = Math.Max(1, lineCount - blankLines);
        double kb = Math.Max(1.0, total / 1024.0);

        st.MaxLineLen = maxLineLen;
        st.AvgLineLen = lineCount > 0 ? (double)sumLineLen / lineCount : 0;
        st.BlankFrac = (double)blankLines / Math.Max(1, lineCount);
        st.CommentFrac = commentLines / nonBlank;
        st.DefineFrac = defineLines / nonBlank;
        st.IncludeFrac = includeLines / nonBlank;
        st.IdentUniqueRatio = identTotal > 0 ? (double)distinctIdents.Count / identTotal : 0;
        st.AvgIdentLen = identTotal > 0 ? (double)identLenSum / identTotal : 0;
        st.FunctionsPerKB = fnShapes / kb;
        st.CallsPerFunction = fnShapes > 0 ? (double)callSites / fnShapes : callSites > 0 ? callSites : 0;
        st.GlobalRefsPerFile = identTotal;

        // Modal comma regularity for tabular detection.
        double tabularShare = 0;
        int modalCommas = 0, modalHits = 0;
        foreach (var (k, v) in commaCounts)
            if (k > 0 && v > modalHits) { modalHits = v; modalCommas = k; }
        if (modalCommas > 0) tabularShare = modalHits / nonBlank;

        st.Class = Decide(ext, printableRatio, digitFrac, entropy, identTotal, total, lineCount, maxLineLen,
            st.DefineFrac, xMacroLines / nonBlank, templateLines / nonBlank, st.FunctionsPerKB,
            (enumStructLines + tableRows) / nonBlank, tabularShare);

        return st;
    }

    /// <summary>Map the tallies onto a class, in priority order (most-specific first).</summary>
    private static ContentClass Decide(string ext, double printableRatio, double digitFrac, double entropy,
        long identTotal, long total, long lineCount, long maxLineLen, double defineFrac, double xMacroFrac,
        double templateFrac, double fnPerKb, double enumStructFrac, double tabularShare)
    {
        // data-heavy: mostly digits/hex/separators and few identifiers relative to size.
        double identDensity = total > 0 ? identTotal / (total / 1024.0) : 0;
        if (digitFrac >= BlobDigitFrac && identDensity < 5)
            // Split by byte-value entropy: random hex is high-entropy (balloons the index), a repeated
            // pattern is low-entropy (cheap, low cardinality).
            return entropy >= BlobHighEntropyBits ? ContentClass.DataBlobHighEntropy : ContentClass.DataBlobRepetitive;

        if (maxLineLen >= MinifiedLineLen && lineCount <= MinifiedMaxLines) return ContentClass.MinifiedLongLine;
        if (defineFrac >= DefineDenseFrac) return ContentClass.PreprocessorDense;
        if (xMacroFrac >= XMacroFrac) return ContentClass.XMacro;
        if (templateFrac >= TemplateFrac) return ContentClass.TemplateMetaprogramming;
        if (fnPerKb >= FnPerKbHigh) return ContentClass.InlineFunctionHeavy;
        if (enumStructFrac >= 0.20) return ContentClass.EnumStructTable;
        if (tabularShare >= TabularFrac) return ContentClass.TabularData;

        // Any real code markers -> generic code; otherwise prose/text.
        if (identTotal > 0 && (fnPerKb > 0 || defineFrac > 0)) return ContentClass.GenericCode;
        return ContentClass.Text;
    }

    public static DensityBand FunctionsPerKbBand(double v) => Band(v, FnPerKbLow, FnPerKbHigh);
    public static DensityBand CallsPerFunctionBand(double v) => Band(v, CallsPerFnLow, CallsPerFnHigh);
    public static DensityBand GlobalRefsBand(double v) => Band(v, RefsPerFileLow, RefsPerFileHigh);

    private static DensityBand Band(double v, double low, double high) =>
        v < low ? DensityBand.Low : v < high ? DensityBand.Med : DensityBand.High;

    // --- byte helpers ---
    private static byte[] StripEveryOther(ReadOnlySpan<byte> s, int evenOffset)
    {
        var outBuf = new byte[s.Length / 2 + 1];
        int n = 0;
        for (int i = evenOffset; i < s.Length; i += 2)
            if (s[i] != 0) outBuf[n++] = s[i];
        return outBuf[..n];
    }

    private static int FirstNonSpace(ReadOnlySpan<byte> line)
    {
        for (int i = 0; i < line.Length; i++)
            if (line[i] != (byte)' ' && line[i] != (byte)'\t') return i;
        return -1;
    }

    private static bool StartsWithHash(ReadOnlySpan<byte> trimmed, string word)
    {
        if (trimmed.Length == 0 || trimmed[0] != (byte)'#') return false;
        int i = 1;
        while (i < trimmed.Length && (trimmed[i] == (byte)' ' || trimmed[i] == (byte)'\t')) i++;
        return StartsWithWord(trimmed[i..], word);
    }

    private static bool StartsWithWord(ReadOnlySpan<byte> s, string word)
    {
        if (s.Length < word.Length) return false;
        for (int i = 0; i < word.Length; i++) if (s[i] != (byte)word[i]) return false;
        return s.Length == word.Length || !IsIdentPart(s[word.Length]);
    }

    private static bool IsComment(ReadOnlySpan<byte> t) =>
        t.Length >= 2 && ((t[0] == (byte)'/' && (t[1] == (byte)'/' || t[1] == (byte)'*')) || t[0] == (byte)'*');

    private static bool IsControlStmt(ReadOnlySpan<byte> t) =>
        StartsWithWord(t, "if") || StartsWithWord(t, "for") || StartsWithWord(t, "while")
        || StartsWithWord(t, "switch") || StartsWithWord(t, "catch") || StartsWithWord(t, "else")
        || StartsWithWord(t, "do");

    // ") {" or "){" anywhere on the line — a function/definition body opener.
    private static bool ContainsBraceOpen(ReadOnlySpan<byte> line)
    {
        for (int i = 0; i < line.Length - 1; i++)
        {
            if (line[i] != (byte)')') continue;
            int j = i + 1;
            while (j < line.Length && line[j] == (byte)' ') j++;
            if (j < line.Length && line[j] == (byte)'{') return true;
        }
        return false;
    }

    private static bool ContainsTemplate(ReadOnlySpan<byte> t)
    {
        int idx = IndexOf(t, "template");
        if (idx < 0) return false;
        int j = idx + 8;
        while (j < t.Length && t[j] == (byte)' ') j++;
        return j < t.Length && t[j] == (byte)'<';
    }

    private static bool ContainsSeq(ReadOnlySpan<byte> line, byte a, byte b)
    {
        for (int i = 0; i < line.Length - 1; i++) if (line[i] == a && line[i + 1] == b) return true;
        return false;
    }

    // Uppercase macro invocation: FOO( / FOO_BAR( with >=3 leading upper/underscore chars.
    private static bool IsUpperMacroCall(ReadOnlySpan<byte> t)
    {
        int i = 0;
        while (i < t.Length && (t[i] is >= (byte)'A' and <= (byte)'Z' || t[i] == (byte)'_'
               || (i > 0 && t[i] is >= (byte)'0' and <= (byte)'9'))) i++;
        return i >= 3 && i < t.Length && t[i] == (byte)'(';
    }

    private static void AddInclude(ReadOnlySpan<byte> trimmed, List<string> targets)
    {
        int q = -1; byte close = 0;
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] == (byte)'"') { q = i; close = (byte)'"'; break; }
            if (trimmed[i] == (byte)'<') { q = i; close = (byte)'>'; break; }
        }
        if (q < 0) return;
        int e = q + 1;
        while (e < trimmed.Length && trimmed[e] != close) e++;
        if (e >= trimmed.Length) return;
        var path = trimmed[(q + 1)..e];
        // basename only (after the last '/' or '\')
        int slash = -1;
        for (int i = 0; i < path.Length; i++) if (path[i] == (byte)'/' || path[i] == (byte)'\\') slash = i;
        var bn = path[(slash + 1)..];
        if (bn.Length > 0 && bn.Length < 256)
            targets.Add(System.Text.Encoding.ASCII.GetString(bn).ToLowerInvariant());
    }

    private static int IndexOf(ReadOnlySpan<byte> s, string needle)
    {
        for (int i = 0; i + needle.Length <= s.Length; i++)
        {
            bool ok = true;
            for (int k = 0; k < needle.Length; k++) if (s[i + k] != (byte)needle[k]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static bool IsIdentStart(byte b) => b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or (byte)'_';
    private static bool IsIdentPart(byte b) => IsIdentStart(b) || b is >= (byte)'0' and <= (byte)'9';
}
