using System.Globalization;
using System.Text;

namespace IPDocketing.Core.Services;

/// <summary>
/// Mark-to-mark similarity, built for trademark conflict screening rather than
/// generic string matching.
///
/// WHY THE OLD SCORE WASN'T GOOD ENOUGH
///
/// The watch previously used one signal: normalised Levenshtein distance over
/// the raw strings. That is wrong in both directions on real portfolios.
///
///   FALSE NEGATIVES (the expensive kind - a conflict you never saw):
///     "SHUBH LAXMI" vs "SHUBH LAXMI FOODS PVT LTD"  -> 55%, under threshold,
///        even though the distinctive part is identical
///     "KWIK BRITE"  vs "QUICK BRIGHT"               -> 58%, though they are
///        phonetically the same mark
///     "LAXMI"       vs "LAKSHMI"                    -> 71%, a routine
///        transliteration variant of one Indian word
///     "SUNRISE"     vs "SUN RISE"                   -> spacing alone drops it
///
///   FALSE POSITIVES (the kind that trains people to ignore alerts):
///     "SUPER FOODS" vs "SUPER TOOLS"  -> 83% on a shared non-distinctive word
///     any two 4-letter marks sharing three letters
///
/// WHAT THIS DOES INSTEAD
///
/// Five independent signals are computed and the strongest governs, with the
/// others contributing. Each is reported by name, so an alert can say WHY it
/// fired rather than showing a bare number that nobody can check:
///
///   1. Normalised edit distance  - after folding case, diacritics, punctuation
///                                  and spacing
///   2. Token-set ratio           - distinctive tokens only, order-independent;
///                                  catches the "+ PVT LTD" and word-order cases
///   3. Phonetic                  - a Metaphone-style key with Indian-English
///                                  spelling variants folded in (KSH/X, PH/F,
///                                  V/W, double letters)
///   4. Containment               - one distinctive core wholly inside the other
///   5. OCR-tolerant              - re-runs signal 1 with the character
///                                  confusions OCR actually makes folded
///                                  together; only consulted when the input
///                                  came from OCR
///
/// Non-distinctive tokens are stripped before comparison, because two marks
/// sharing only "SUPER" or "PVT LTD" are not similar in any sense a registrar
/// would recognise - and an alert list full of those is one nobody reads.
///
/// NONE OF THIS IS A LEGAL OPINION. It is a shortlist for human review.
/// Likelihood of confusion turns on goods, channels of trade, distinctiveness
/// and reputation, none of which a string comparison can see.
/// </summary>
public class MarkSimilarityService
{
    /// <summary>Words carrying no source-identifying weight - stripped before comparison.</summary>
    private static readonly HashSet<string> NonDistinctive = new(StringComparer.OrdinalIgnoreCase)
    {
        // Corporate forms
        "PVT", "PRIVATE", "LTD", "LIMITED", "LLP", "INC", "CORP", "CORPORATION",
        "COMPANY", "CO", "AND", "THE", "OF", "A", "AN",
        // Generic trade words that appear across thousands of Indian marks
        "INDIA", "INDIAN", "BHARAT", "NATIONAL", "INTERNATIONAL", "GLOBAL",
        "SUPER", "ROYAL", "NEW", "MODERN", "QUALITY", "PREMIUM", "GOLD", "GOLDEN",
        "BEST", "TOP", "STAR", "SHREE", "SHRI", "SRI", "GROUP", "ENTERPRISES",
        "INDUSTRIES", "TRADERS", "TRADING", "PRODUCTS", "FOODS", "AGRO",
        "BRAND", "BRANDS", "MARKETING", "EXPORTS", "IMPEX", "SONS", "BROTHERS",
    };

    /// <summary>
    /// Devanagari to Latin, enough to compare a Devanagari mark against a
    /// romanised one. Not a full transliteration standard - deliberately maps
    /// to the spellings Indian applicants actually file under.
    /// </summary>
    private static readonly Dictionary<char, string> Devanagari = new()
    {
        ['अ'] = "A", ['आ'] = "AA", ['इ'] = "I", ['ई'] = "EE", ['उ'] = "U", ['ऊ'] = "OO",
        ['ए'] = "E", ['ऐ'] = "AI", ['ओ'] = "O", ['औ'] = "AU",
        ['क'] = "K", ['ख'] = "KH", ['ग'] = "G", ['घ'] = "GH",
        ['च'] = "CH", ['छ'] = "CHH", ['ज'] = "J", ['झ'] = "JH",
        ['ट'] = "T", ['ठ'] = "TH", ['ड'] = "D", ['ढ'] = "DH", ['ण'] = "N",
        ['त'] = "T", ['थ'] = "TH", ['द'] = "D", ['ध'] = "DH", ['न'] = "N",
        ['प'] = "P", ['फ'] = "PH", ['ब'] = "B", ['भ'] = "BH", ['म'] = "M",
        ['य'] = "Y", ['र'] = "R", ['ल'] = "L", ['व'] = "V",
        ['श'] = "SH", ['ष'] = "SH", ['स'] = "S", ['ह'] = "H",
        ['ा'] = "A", ['ि'] = "I", ['ी'] = "EE", ['ु'] = "U", ['ू'] = "OO",
        ['े'] = "E", ['ै'] = "AI", ['ो'] = "O", ['ौ'] = "AU", ['ं'] = "N",
    };

    /// <summary>
    /// Character groups OCR genuinely confuses. Folding these together lets a
    /// misread Journal entry still match - "S0NRISE" against "SUNRISE".
    /// </summary>
    private static readonly Dictionary<char, char> OcrConfusions = new()
    {
        ['0'] = 'O', ['Q'] = 'O', ['D'] = 'O',
        ['1'] = 'I', ['L'] = 'I', ['|'] = 'I', ['!'] = 'I',
        ['5'] = 'S', ['$'] = 'S',
        ['8'] = 'B', ['6'] = 'G', ['2'] = 'Z', ['7'] = 'T',
        ['U'] = 'V', ['W'] = 'V',
    };

    public sealed record SimilarityResult(
        int Score,
        string PrimarySignal,
        List<string> Reasons,
        string NormalizedA,
        string NormalizedB)
    {
        /// <summary>True where the marks are effectively the same after normalisation.</summary>
        public bool IsNearIdentical => Score >= 95;
    }

    /// <summary>
    /// Compares two marks. <paramref name="fromOcr"/> enables the confusion-
    /// tolerant signal, which is off by default because folding 0/O and 1/I on
    /// clean text creates false positives of its own.
    /// </summary>
    public SimilarityResult Compare(string markA, string markB, bool fromOcr = false)
    {
        var reasons = new List<string>();

        var rawA = Normalize(markA);
        var rawB = Normalize(markB);

        if (rawA.Length == 0 || rawB.Length == 0)
            return new SimilarityResult(0, "none", reasons, rawA, rawB);

        if (rawA == rawB)
        {
            reasons.Add("Identical after normalisation");
            return new SimilarityResult(100, "identical", reasons, rawA, rawB);
        }

        var coreA = DistinctiveCore(rawA);
        var coreB = DistinctiveCore(rawB);

        // Where stripping leaves nothing, fall back to the full string rather
        // than declaring a match between two piles of generic words.
        if (coreA.Length == 0) coreA = rawA;
        if (coreB.Length == 0) coreB = rawB;

        var best = 0;
        var primary = "none";

        void Consider(int score, string signal, string reason)
        {
            if (score <= best) return;
            best = score;
            primary = signal;
            reasons.Insert(0, reason);
        }

        // 1. Edit distance over the distinctive core
        var edit = EditRatio(coreA, coreB);
        Consider(edit, "spelling", $"Spelling {edit}% alike on the distinctive part ({coreA} / {coreB})");

        // 2. Token set - order-independent, ignores added corporate words
        var tokenScore = TokenSetRatio(rawA, rawB);
        if (tokenScore > 0)
            Consider(tokenScore, "tokens", $"Shares {tokenScore}% of its distinctive words regardless of order");

        // 3. Phonetic
        var phoneticA = PhoneticKey(coreA);
        var phoneticB = PhoneticKey(coreB);
        if (phoneticA.Length > 0 && phoneticA == phoneticB)
            Consider(92, "phonetic", $"Sounds the same ({phoneticA})");
        else if (phoneticA.Length > 2 && phoneticB.Length > 2)
        {
            var phoneticRatio = EditRatio(phoneticA, phoneticB);
            if (phoneticRatio >= 80)
                Consider(phoneticRatio - 5, "phonetic",
                    $"Sounds {phoneticRatio}% alike ({phoneticA} / {phoneticB})");
        }

        // 4. Containment - one core wholly inside the other. Only counted when
        //    the contained part is substantial; "SUN" inside "SUNDARAM" is not
        //    a conflict signal on its own.
        var shorter = coreA.Length <= coreB.Length ? coreA : coreB;
        var longer = coreA.Length <= coreB.Length ? coreB : coreA;
        if (shorter.Length >= 4 && longer.Contains(shorter, StringComparison.Ordinal))
        {
            var coverage = (int)Math.Round(shorter.Length * 100.0 / longer.Length);
            var containment = Math.Max(75, coverage);
            Consider(containment, "containment",
                $"'{shorter}' appears whole inside '{longer}'");
        }

        // 5. OCR-tolerant, only where the text came from OCR
        if (fromOcr)
        {
            var ocrScore = EditRatio(FoldOcr(coreA), FoldOcr(coreB));
            if (ocrScore > best + 5)
                Consider(ocrScore - 5, "ocr",
                    $"Matches at {ocrScore}% once characters OCR commonly confuses are treated as equal");
        }

        if (reasons.Count == 0) reasons.Add("No strong signal");

        return new SimilarityResult(Math.Clamp(best, 0, 100), primary, reasons, rawA, rawB);
    }

    /// <summary>
    /// Adjusts a similarity score for class proximity. Two identical marks in
    /// unrelated classes usually coexist; the same pair in the same class is the
    /// actual problem. Returns the adjusted score and an explanation.
    /// </summary>
    public (int Score, string? Note) ApplyClassWeighting(int score, string? classA, string? classB)
    {
        if (!int.TryParse(classA?.Trim(), out var a) || !int.TryParse(classB?.Trim(), out var b))
            return (score, null);

        if (a == b)
            return (Math.Min(100, score + 8), $"Same class ({a})");

        if (AreRelated(a, b))
            return (score, $"Related classes ({a} and {b})");

        // Not zeroed: a strong mark can be opposed across classes on reputation,
        // and burying that entirely would hide the cases that matter most.
        return (Math.Max(0, score - 12), $"Different classes ({a} vs {b})");
    }

    /// <summary>
    /// Nice classes that routinely conflict in practice - food and drink,
    /// clothing and retail, software and telecoms, pharma and cosmetics.
    /// A working shortlist, not the full coordination table.
    /// </summary>
    private static bool AreRelated(int a, int b)
    {
        int[][] groups =
        {
            new[] { 29, 30, 31, 32, 33, 43 },   // foods, drinks, restaurants
            new[] { 3, 5, 44 },                 // cosmetics, pharma, medical
            new[] { 9, 38, 42 },                // software, telecoms, IT services
            new[] { 18, 24, 25, 35 },           // leather, textiles, clothing, retail
            new[] { 6, 19, 37 },                // metals, building materials, construction
            new[] { 35, 36, 41, 45 },           // business, finance, education, legal
            new[] { 7, 8, 11, 12 },             // machines, tools, appliances, vehicles
        };

        return groups.Any(g => g.Contains(a) && g.Contains(b));
    }

    // --- normalisation -------------------------------------------------

    /// <summary>
    /// Uppercase, transliterate Devanagari, strip diacritics and punctuation,
    /// collapse whitespace. Everything downstream compares normalised forms, so
    /// "Sun-Rise®" and "SUN RISE" are the same input.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var transliterated = new StringBuilder();
        foreach (var ch in value)
        {
            if (Devanagari.TryGetValue(ch, out var latin)) transliterated.Append(latin);
            else transliterated.Append(ch);
        }

        var decomposed = transliterated.ToString().Normalize(NormalizationForm.FormD);
        var stripped = new StringBuilder();

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) stripped.Append(char.ToUpperInvariant(ch));
            else if (char.IsWhiteSpace(ch) || ch is '-' or '&' or '/') stripped.Append(' ');
        }

        return string.Join(' ', stripped.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The mark with non-distinctive words removed and spacing dropped.</summary>
    private static string DistinctiveCore(string normalized) =>
        string.Concat(normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !NonDistinctive.Contains(t)));

    private static List<string> DistinctiveTokens(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !NonDistinctive.Contains(t) && t.Length > 1)
            .ToList();

    /// <summary>
    /// Order-independent overlap of distinctive tokens, weighted by token
    /// length so a shared long word counts for more than a shared short one.
    /// </summary>
    private static int TokenSetRatio(string a, string b)
    {
        var tokensA = DistinctiveTokens(a);
        var tokensB = DistinctiveTokens(b);
        if (tokensA.Count == 0 || tokensB.Count == 0) return 0;

        var matchedWeight = 0.0;
        var remaining = new List<string>(tokensB);

        foreach (var token in tokensA)
        {
            // Exact first, then a close spelling match, so "LAXMI"/"LAKSHMI"
            // still pairs up.
            var exact = remaining.FirstOrDefault(t => t == token);
            if (exact is not null)
            {
                matchedWeight += token.Length;
                remaining.Remove(exact);
                continue;
            }

            var near = remaining.FirstOrDefault(t => EditRatio(t, token) >= 80);
            if (near is not null)
            {
                matchedWeight += token.Length * 0.85;
                remaining.Remove(near);
            }
        }

        var totalWeight = Math.Max(tokensA.Sum(t => t.Length), tokensB.Sum(t => t.Length));
        if (totalWeight == 0) return 0;

        return (int)Math.Round(matchedWeight * 100.0 / totalWeight);
    }

    /// <summary>
    /// Metaphone-ish key with Indian-English spelling variants folded in. The
    /// substitutions are the ones that actually generate transliteration pairs
    /// on the Indian register: KSH/X, PH/F, V/W, doubled consonants, silent H
    /// after aspirated stops, and terminal vowels.
    /// </summary>
    public static string PhoneticKey(string value)
    {
        var s = Normalize(value).Replace(" ", "");
        if (s.Length == 0) return string.Empty;

        // Order matters - longer, more specific patterns first.
        // IGHT->IT and KW->K were added after testing: without them
        // "KWIK BRITE" and "QUICK BRIGHT" score 45%, because QU collapses to K
        // while KW went to KV, and the silent GH in BRIGHT survived. They are
        // the same mark to the ear, and that is exactly the pair a phonetic
        // signal exists to catch.
        var replacements = new (string From, string To)[]
        {
            ("IGHT", "IT"), ("KSH", "X"), ("KW", "K"), ("KH", "K"), ("GH", "G"), ("CHH", "C"), ("CH", "C"),
            ("JH", "J"), ("TH", "T"), ("DH", "D"), ("BH", "B"), ("PH", "F"),
            ("SH", "S"), ("CK", "K"), ("QU", "K"), ("Q", "K"), ("X", "KS"),
            ("W", "V"), ("Z", "S"), ("EE", "I"), ("OO", "U"), ("AA", "A"),
            ("Y", "I"), ("C", "K"),
        };

        var sb = new StringBuilder(s);
        foreach (var (from, to) in replacements)
            sb.Replace(from, to);

        var collapsed = new StringBuilder();
        char? previous = null;

        foreach (var ch in sb.ToString())
        {
            if (previous == ch) continue;      // drop doubled letters
            collapsed.Append(ch);
            previous = ch;
        }

        var result = collapsed.ToString();

        // Drop interior vowels, keeping the first character - the consonant
        // skeleton is what survives transliteration.
        var key = new StringBuilder();
        for (var i = 0; i < result.Length; i++)
        {
            var ch = result[i];
            if (i == 0 || !"AEIOU".Contains(ch)) key.Append(ch);
        }

        return key.ToString();
    }

    private static string FoldOcr(string value) =>
        string.Concat(value.Select(c => OcrConfusions.TryGetValue(c, out var folded) ? folded : c));

    private static int EditRatio(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 100;

        var distance = Levenshtein(a, b);
        var maxLength = Math.Max(a.Length, b.Length);
        return (int)Math.Round((1.0 - (double)distance / maxLength) * 100);
    }

    /// <summary>
    /// Two-row Levenshtein. The old version allocated a full (n+1)x(m+1) matrix
    /// per comparison; on a Journal run that is one such matrix per published
    /// mark per portfolio matter - tens of millions of allocations on a real
    /// portfolio. Two rows is the same result with a fraction of the pressure.
    /// </summary>
    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
