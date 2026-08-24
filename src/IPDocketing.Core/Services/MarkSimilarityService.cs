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
    /// Devanagari consonants, which carry an inherent /a/ when no vowel sign
    /// follows them. Kept separate from the map above because the map also holds
    /// independent vowels and vowel signs, which do not.
    /// </summary>
    private static readonly HashSet<char> DevanagariConsonants = new()
    {
        'क', 'ख', 'ग', 'घ', 'च', 'छ', 'ज', 'झ', 'ट', 'ठ', 'ड', 'ढ', 'ण',
        'त', 'थ', 'द', 'ध', 'न', 'प', 'फ', 'ब', 'भ', 'म', 'य', 'र', 'ल',
        'व', 'श', 'ष', 'स', 'ह',
    };

    /// <summary>
    /// Vowel signs (matras). A consonant followed by one of these has its
    /// inherent /a/ replaced, so no /a/ should be inserted. The anusvara is
    /// deliberately NOT here - it is a nasal, and क + anusvara is KAN, not KN.
    /// </summary>
    private static readonly HashSet<char> DevanagariVowelSigns = new()
    {
        'ा', 'ि', 'ी', 'ु', 'ू', 'ृ',
        'े', 'ै', 'ो', 'ौ',
    };

    /// <summary>
    /// Character groups OCR genuinely confuses. Folding these together lets a
    /// misread Journal entry still match - "S0NRISE" against "SUNRISE".
    /// </summary>
    private static readonly Dictionary<char, char> OcrConfusions = new()
    {
        // U belongs with the round glyphs, not with V. It was mapped to V, and
        // that made the one example this table is documented by fail: SUNRISE
        // folded to SVNRISE while S0NRISE folded to SONRISE, so the two never
        // met and signal 5 contributed nothing to its own motivating case.
        ['0'] = 'O', ['Q'] = 'O', ['D'] = 'O', ['U'] = 'O',
        ['1'] = 'I', ['L'] = 'I', ['|'] = 'I', ['!'] = 'I',
        ['5'] = 'S', ['$'] = 'S',
        ['8'] = 'B', ['6'] = 'G', ['2'] = 'Z', ['7'] = 'T',
        ['W'] = 'V',
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
    /// One mark with everything derived from it computed once.
    ///
    /// This type exists for a measured reason. A watch run compares every
    /// published mark against every portfolio matter - a 400-page issue against
    /// 500 marks is millions of pairings - and normalisation, the distinctive
    /// core, the token list and the phonetic key are each a pure function of ONE
    /// side. Recomputing them inside the pair loop meant the portfolio was
    /// re-normalised once per published mark and vice versa: tens of millions of
    /// calls where tens of thousands do. Prepare each side once, compare many
    /// times.
    /// </summary>
    public sealed record PreparedMark(
        string Normalized,
        string Core,
        string Phonetic,
        List<string> Tokens)
    {
        public bool IsEmpty => Normalized.Length == 0;
    }

    /// <summary>Does all the per-mark work up front. Safe to cache and reuse.</summary>
    public static PreparedMark Prepare(string? mark)
    {
        var raw = Normalize(mark);
        if (raw.Length == 0) return new PreparedMark(string.Empty, string.Empty, string.Empty, new List<string>());

        var core = DistinctiveCore(raw);
        if (core.Length == 0) core = raw.Replace(" ", "");

        return new PreparedMark(raw, core, PhoneticKeyOfCore(core), DistinctiveTokens(raw));
    }

    /// <summary>
    /// Compares two marks. <paramref name="fromOcr"/> enables the confusion-
    /// tolerant signal, which is off by default because folding 0/O and 1/I on
    /// clean text creates false positives of its own.
    /// </summary>
    public SimilarityResult Compare(string markA, string markB, bool fromOcr = false)
        => Compare(Prepare(markA), Prepare(markB), fromOcr);

    /// <summary>
    /// The same comparison against marks whose derived forms are already
    /// computed. Use this on any path that compares one mark against many.
    /// </summary>
    public SimilarityResult Compare(PreparedMark a, PreparedMark b, bool fromOcr = false)
    {
        var reasons = new List<string>();

        var rawA = a.Normalized;
        var rawB = b.Normalized;

        if (a.IsEmpty || b.IsEmpty)
            return new SimilarityResult(0, "none", reasons, rawA, rawB);

        if (rawA == rawB)
        {
            reasons.Add("Identical after normalisation");
            return new SimilarityResult(100, "identical", reasons, rawA, rawB);
        }

        var coreA = a.Core;
        var coreB = b.Core;

        // Cheap structural reject. Every signal below is bounded above by the
        // length ratio of the two cores EXCEPT containment, which is checked
        // explicitly here rather than assumed away. A three-letter mark against
        // a twenty-letter one cannot reach the threshold by any route, and on a
        // full journal run most pairs are exactly that.
        var shortLen = Math.Min(coreA.Length, coreB.Length);
        var longLen = Math.Max(coreA.Length, coreB.Length);
        if (longLen > 0 && shortLen * 100 / longLen < 50 &&
            !(coreA.Length <= coreB.Length
                ? coreB.Contains(coreA, StringComparison.Ordinal)
                : coreA.Contains(coreB, StringComparison.Ordinal)))
        {
            reasons.Add("Too different in length to be a conflict");
            return new SimilarityResult(shortLen * 100 / longLen, "none", reasons, rawA, rawB);
        }

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
        var tokenScore = TokenSetRatio(a.Tokens, b.Tokens);
        if (tokenScore > 0)
            Consider(tokenScore, "tokens", $"Shares {tokenScore}% of its distinctive words regardless of order");

        // 3. Phonetic
        var phoneticA = a.Phonetic;
        var phoneticB = b.Phonetic;

        // The length floor is the whole point. PhoneticKey drops every interior
        // vowel, so short vowel-heavy marks collapse to a two-letter consonant
        // skeleton and then collide wholesale: LILY, LEELA, LOLA and LULU all
        // reduce to "LL"; MOON, MAINA and MEENA to "MN"; TATA and TITU to "TT".
        // At 92 apiece those land at the top of the report in the high band,
        // labelled "Sounds the same" - and enough of them across a 40,000-mark
        // issue buries the real conflicts. Three consonants is the point at
        // which an exact key match means something.
        if (phoneticA.Length > 2 && phoneticA == phoneticB)
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
        var at = shorter.Length >= 4 ? longer.IndexOf(shorter, StringComparison.Ordinal) : -1;
        if (at >= 0)
        {
            var coverage = (int)Math.Round(shorter.Length * 100.0 / longer.Length);

            // WHERE the shorter mark sits inside the longer one decides whether
            // this is incorporation or coincidence, and the old
            // Math.Max(75, coverage) could not tell the difference - it threw
            // coverage away whenever it was below 75 and alerted at 75 on any
            // four-letter run appearing anywhere. That fired on AXIS/PRAXIS,
            // NOVA/CASANOVA, VEDA/AYURVEDANTA and KING/SMOKING GUN.
            //
            // A mark taken over WHOLE and added to is the real signal, and it
            // starts at the beginning - the first syllable is what a registrar
            // and a customer both weigh most. So a prefix match is worth a
            // floor of 70; anything embedded mid-word or at the tail is worth
            // only its coverage, which lets a genuinely large overlap still
            // score while coincidental ones fall away.
            var containment = at == 0 ? Math.Max(70, coverage) : coverage;

            Consider(containment, "containment",
                at == 0
                    ? $"'{longer}' begins with the whole of '{shorter}'"
                    : $"'{shorter}' appears inside '{longer}' ({coverage}% of it)");
        }

        // 5. OCR-tolerant, only where the text came from OCR AND the pair is
        //    already a near miss. The fold is applied to both sides - it has to
        //    be, since folding one side only would leave the very characters it
        //    is meant to reconcile still differing - and that means it can
        //    manufacture a match out of two unrelated marks: DUO and OVO both
        //    fold to "OVO" and scored 95. Requiring a decent unfolded score
        //    first keeps this signal doing its actual job, which is rescuing a
        //    mark a misread character pushed just under the line, not inventing
        //    conflicts between marks that were never alike.
        if (fromOcr && best >= 55)
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

        // FormD FIRST, then transliterate. This order matters and used to be the
        // other way round, which broke two things at once:
        //
        //  - Precomposed nukta letters (ज़ ड़ फ़ क़, all common on the Indian
        //    register) are single codepoints and are NOT keys in the map, so
        //    they fell through untranslated. Decomposing first splits each into
        //    its base consonant plus a combining nukta, and the consonant then
        //    transliterates normally. Before this, "ज़ायका" normalised to
        //    "JAYKA" with a literal Devanagari codepoint still embedded, and
        //    every downstream comparison worked on that.
        //  - The blanket NonSpacingMark skip below discarded the vowel signs
        //    ु ू े ै ं outright, because those are Mn characters. A mark
        //    written with any of them lost the vowel entirely.
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var stripped = new StringBuilder();

        for (var i = 0; i < decomposed.Length; i++)
        {
            var ch = decomposed[i];

            // Strip accents, but never a mark this map has a reading for.
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark &&
                !Devanagari.ContainsKey(ch))
                continue;

            if (Devanagari.TryGetValue(ch, out var latin))
            {
                // Captured BEFORE the consonant is appended - medial schwa
                // deletion asks what sound came before it, and once the
                // consonant is in the buffer the answer is always "a consonant".
                var precededByVowel = stripped.Length > 0 && "AEIOU".Contains(stripped[^1]);

                stripped.Append(latin);

                // The inherent vowel. A Devanagari consonant with no following
                // matra is pronounced with an /a/; without this, पहाड़ी
                // romanises as PHADEE rather than PAHADEE - and the fused
                // consonant pair that leaves behind then trips the digraph
                // rewrites in PhoneticKey (PH->F), so one dropped vowel costs
                // the spelling signal AND the phonetic one.
                if (DevanagariConsonants.Contains(ch) &&
                    !SuppressesInherentVowel(decomposed, i, precededByVowel))
                    stripped.Append('A');

                continue;
            }

            if (char.IsLetterOrDigit(ch)) stripped.Append(char.ToUpperInvariant(ch));
            else if (char.IsWhiteSpace(ch) || ch is '-' or '&' or '/') stripped.Append(' ');
        }

        return string.Join(' ', stripped.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// True when the consonant at <paramref name="index"/> does NOT take its
    /// inherent /a/.
    ///
    /// Three cases, and the third is the one that needs care:
    ///
    ///  1. A vowel sign or virama follows - the inherent vowel is replaced or
    ///     explicitly killed.
    ///  2. The consonant is word-final. Hindi drops the final schwa, which is
    ///     why कमल is KAMAL and not KAMALA.
    ///  3. Medial schwa deletion. Hindi also drops a schwa in the environment
    ///     V C _ C V - a schwa between two consonants, where the preceding
    ///     sound is a vowel and the following consonant carries its own vowel.
    ///     This is what makes ज़ायका ZAYKA rather than ZAYAKA. Without the rule
    ///     the naive "every consonant gets an A" produces a spelling nobody
    ///     files under, and the extra vowel then fuses into a digraph that
    ///     PhoneticKey rewrites, so the phonetic signal is lost too.
    ///
    /// Not a complete account of Hindi phonology - schwa deletion is
    /// famously irregular - but it covers the ordinary shapes of the marks on
    /// the Indian register, which is the whole job here.
    /// </summary>
    private static bool SuppressesInherentVowel(string text, int index, bool precededByVowel)
    {
        var j = index + 1;
        while (j < text.Length && text[j] == '़') j++;   // nukta modifies the consonant

        if (j >= text.Length) return true;               // word-final schwa
        var next = text[j];

        if (next == '्') return true;                    // virama
        if (DevanagariVowelSigns.Contains(next)) return true;
        if (next is < 'ऀ' or > 'ॿ') return true;         // space, Latin, punctuation: word end

        // Medial deletion needs a vowel BEFORE it...
        if (!precededByVowel) return false;

        // ...and a following consonant that carries its own vowel.
        if (!DevanagariConsonants.Contains(next)) return false;

        var k = j + 1;
        while (k < text.Length && text[k] == '़') k++;

        return k < text.Length && DevanagariVowelSigns.Contains(text[k]);
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
    private static int TokenSetRatio(List<string> tokensA, List<string> tokensB)
    {
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
        => PhoneticKeyOfCore(Normalize(value).Replace(" ", ""));

    /// <summary>
    /// The key for a string that has ALREADY been normalised and had its spaces
    /// removed. Split out because the public entry point re-normalised its
    /// input, and it was being handed the distinctive core - which was
    /// normalised on the way in. That was two extra full normalisations on every
    /// comparison, on the hottest path in the application.
    /// </summary>
    private static string PhoneticKeyOfCore(string s)
    {
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
