using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace EverythingonStart.Services;

/// <summary>Shared offline pinyin matching and native Everything regex expansion.</summary>
public static class PinyinSearchService
{
    private const int MaxQueryLength = 48;
    private const int MaxPatternLength = 24000;
    private sealed record PinyinIndex(
        Dictionary<char, string[]> Readings,
        Dictionary<string, string> Syllables,
        Dictionary<char, string> Initials);

    private static readonly Lazy<PinyinIndex> Index = new(LoadIndex);
    private static readonly object PatternLock = new();
    private static readonly Dictionary<string, string?> PatternCache = new(StringComparer.Ordinal);

    public static int GetMatchScore(string text, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 0;
        var lower = text.ToLowerInvariant();
        var q = query.Trim().ToLowerInvariant();
        if (lower == q) return 100;
        if (lower.StartsWith(q, StringComparison.Ordinal)) return 85;
        if (lower.Contains(q, StringComparison.Ordinal)) return 70;
        if (!IsPlainQuery(q)) return 0;

        var tokens = Normalize(q).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int score = 100;
        foreach (string token in tokens)
        {
            int tokenScore = Math.Max(MatchRomanized(lower, token, initials: false),
                MatchRomanized(lower, token, initials: true));
            if (tokenScore == 0) return 0;
            score = Math.Min(score, tokenScore);
        }
        return score;
    }

    private static int MatchRomanized(string text, string query, bool initials)
    {
        // Memoization avoids enumerating every pronunciation of polyphonic titles.
        var memo = new Dictionary<(int Text, int Query), int>();
        int MatchAt(int t, int q)
        {
            if (q == query.Length) return t;
            if (t == text.Length) return -1;
            if (memo.TryGetValue((t, q), out int cached)) return cached;
            int end = -1;
            if (text[t] == query[q]) end = MatchAt(t + 1, q + 1);
            if (Index.Value.Readings.TryGetValue(text[t], out var readings))
            {
                foreach (string reading in readings)
                {
                    string syllable = initials ? reading[..1] : reading;
                    var remaining = query.AsSpan(q);
                    if (remaining.StartsWith(syllable, StringComparison.Ordinal))
                        end = Math.Max(end, MatchAt(t + 1, q + syllable.Length));
                    else if (!initials && syllable.AsSpan().StartsWith(remaining, StringComparison.Ordinal))
                        end = Math.Max(end, t + 1); // Last syllable may be partially typed.
                }
            }
            memo[(t, q)] = end;
            return end;
        }

        for (int start = 0; start < text.Length; start++)
        {
            int end = MatchAt(start, 0);
            if (end >= 0)
                return (initials ? 50 : 65) - (start > 0 ? 10 : end < text.Length ? 5 : 0);
        }
        return 0;
    }

    /// <summary>Returns null for advanced syntax or oversized input; never rewrites such queries.</summary>
    public static string? BuildRegexPattern(string query)
    {
        if (!IsPlainQuery(query) || query.Trim().Length > MaxQueryLength)
            return null;
        string normalized = Normalize(query);
        lock (PatternLock)
        {
            if (PatternCache.TryGetValue(normalized, out var cached)) return cached;
            var tokens = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var patterns = tokens.Select(BuildTokenPattern).ToArray();
            string? result = patterns.Any(p => p == null) ? null : patterns.Length == 1 ? patterns[0]! :
                string.Concat(patterns.Select(p => $"(?=.*(?:{p}))"));
            if (result?.Length > MaxPatternLength) result = null;
            if (PatternCache.Count >= 64) PatternCache.Clear();
            PatternCache[normalized] = result;
            return result;
        }
    }

    public static string ExpandEverythingQuery(string query)
    {
        if (BuildRegexPattern(query) == null) return query;
        // Each whitespace-separated term remains an AND; category/type prefixes stay outside the OR.
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string expanded = string.Join(' ', terms.Select(term => $"<{term}|regex:\"{BuildRegexPattern(term)}\">"));
        return expanded.Length <= MaxPatternLength ? expanded : query;
    }

    private static string? BuildTokenPattern(string token)
    {
        var index = Index.Value;
        if (!token.Any(c => c is >= 'a' and <= 'z')) return Regex.Escape(token);
        var paths = new List<string>();
        var choices = new Dictionary<int, List<(string Pattern, int Next)>>();
        int patternSize = 0, visited = 0;
        void Full(int offset, string prefix, bool allowLatinLiterals)
        {
            if (++visited > 4096 || paths.Count >= 32 || prefix.Length > MaxPatternLength / 2) return;
            if (offset == token.Length)
            {
                if (patternSize + prefix.Length + 1 <= MaxPatternLength / 2 && !paths.Contains(prefix))
                { paths.Add(prefix); patternSize += prefix.Length + 1; }
                return;
            }
            if (!choices.TryGetValue(offset, out var options))
            {
                options = [];
                // Prefer complete, longest syllables before literal fallback paths.
                for (int length = Math.Min(6, token.Length - offset); length >= 1; length--)
                    if (index.Syllables.TryGetValue(token.Substring(offset, length), out string? chars))
                        options.Add((chars, offset + length));
                string remaining = token[offset..];
                if (remaining.All(c => c is >= 'a' and <= 'z') && remaining.Length <= 6)
                {
                    var partial = index.Syllables.Where(p => p.Key.StartsWith(remaining, StringComparison.Ordinal))
                        .SelectMany(p => ExpandCharacterClass(p.Value)).Distinct().Order().ToArray();
                    if (partial.Length > 0) options.Add((CharacterClass(partial), token.Length));
                }
                options.Add((Regex.Escape(token[offset].ToString()), offset + 1));
                choices[offset] = options;
            }
            foreach (var option in options)
            {
                bool latinLiteral = option.Next == offset + 1 && option.Pattern == token[offset].ToString()
                    && token[offset] is >= 'a' and <= 'z';
                if (!latinLiteral || allowLatinLiterals)
                    Full(option.Next, prefix + option.Pattern, allowLatinLiterals);
            }
        }

        var initials = new StringBuilder();
        foreach (char c in token)
        {
            if (index.Initials.TryGetValue(c, out string? chars))
                initials.Append("(?:").Append(Regex.Escape(c.ToString())).Append('|').Append(chars).Append(')');
            else
                initials.Append(Regex.Escape(c.ToString()));
        }
        Full(0, string.Empty, allowLatinLiterals: false);
        // Avoid large, redundant literal/pinyin permutations when a fully romanized path exists.
        if (paths.Count == 0) { visited = 0; Full(0, string.Empty, allowLatinLiterals: true); }
        if (initials.Length + patternSize + 8 <= MaxPatternLength) paths.Add(initials.ToString());
        if (paths.Count == 0) return null;
        string pattern = $"(?:{string.Join('|', paths)})";
        return pattern.Length <= MaxPatternLength ? pattern : null;
    }

    private static bool IsPlainQuery(string query) => !string.IsNullOrWhiteSpace(query) &&
        query.All(c => char.IsWhiteSpace(c) || c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
            or '.' or '_' or '-' or 'ü' or 'Ü' || IsHanzi(c));

    private static bool IsHanzi(char c) => c == '\u3007' || c is >= '\u3400' and <= '\u4dbf' or >= '\u4e00' and <= '\u9fff';

    private static string Normalize(string value)
    {
        var builder = new StringBuilder();
        foreach (char c in value.ToLowerInvariant())
        {
            if (c is 'ü' or 'ǖ' or 'ǘ' or 'ǚ' or 'ǜ') { builder.Append('v'); continue; }
            foreach (char normalized in c.ToString().Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(normalized) != UnicodeCategory.NonSpacingMark)
                    builder.Append(normalized);
        }
        return builder.ToString();
    }

    private static PinyinIndex LoadIndex()
    {
        using var stream = typeof(PinyinSearchService).Assembly.GetManifestResourceStream("EverythingonStart.Pinyin.txt")
            ?? throw new InvalidOperationException("Missing embedded pinyin dictionary.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var readings = new Dictionary<char, string[]>();
        var syllables = new Dictionary<string, SortedSet<char>>(StringComparer.Ordinal);
        var initials = new Dictionary<char, SortedSet<char>>();
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith("U+", StringComparison.Ordinal)) continue;
            int colon = line.IndexOf(':');
            if (colon < 0 || !int.TryParse(line.AsSpan(2, colon - 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out int code) || code > char.MaxValue || !IsHanzi((char)code)) continue;
            int comment = line.IndexOf('#', colon);
            string raw = line[(colon + 1)..(comment < 0 ? line.Length : comment)].Trim();
            var pronunciations = raw.Split(',').Select(Normalize)
                .Where(p => p.Length > 0 && p.All(c => c is >= 'a' and <= 'z')).Distinct().ToArray();
            readings[(char)code] = pronunciations;
            foreach (string syllable in pronunciations)
            {
                if (!syllables.TryGetValue(syllable, out var set)) syllables[syllable] = set = [];
                set.Add((char)code);
                if (!initials.TryGetValue(syllable[0], out var initialSet)) initials[syllable[0]] = initialSet = [];
                initialSet.Add((char)code);
            }
        }
        return new PinyinIndex(readings, syllables.ToDictionary(p => p.Key, p => CharacterClass(p.Value)),
            initials.ToDictionary(p => p.Key, p => CharacterClass(p.Value)));
    }

    private static string CharacterClass(IEnumerable<char> characters)
    {
        var chars = characters.Distinct().Order().ToArray();
        var builder = new StringBuilder("[");
        for (int i = 0; i < chars.Length; i++)
        {
            char start = chars[i], end = start;
            while (i + 1 < chars.Length && chars[i + 1] == end + 1) end = chars[++i];
            builder.Append(start);
            if (end != start) { if (end > start + 1) builder.Append('-'); builder.Append(end); }
        }
        return builder.Append(']').ToString();
    }

    private static IEnumerable<char> ExpandCharacterClass(string pattern)
    {
        for (int i = 1; i < pattern.Length - 1; i++)
        {
            if (i + 2 < pattern.Length - 1 && pattern[i + 1] == '-')
            {
                for (int c = pattern[i]; c <= pattern[i + 2]; c++) yield return (char)c;
                i += 2;
            }
            else yield return pattern[i];
        }
    }
}
