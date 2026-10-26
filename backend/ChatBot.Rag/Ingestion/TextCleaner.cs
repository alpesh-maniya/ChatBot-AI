using System.Text;
using System.Text.RegularExpressions;

namespace ChatBot.Rag.Ingestion;

// Turns the lines of a layout block into one clean string.
//
// The awkward part is a hyphen at the end of a line. "publish-|ing" should become
// "publishing", but "passport-|related", "1,242-|page" and "Vance-|Cooks" must keep
// theirs. The document is its own dictionary: if the joined word appears unbroken
// somewhere else in the report, join it; otherwise keep the hyphen.
public sealed partial class TextCleaner
{
    private readonly HashSet<string> _vocabulary;
    private readonly IReadOnlyDictionary<string, string> _replacements;

    public TextCleaner(IEnumerable<string> vocabulary, IReadOnlyDictionary<string, string>? replacements = null)
    {
        _vocabulary = new HashSet<string>(vocabulary, StringComparer.OrdinalIgnoreCase);
        _replacements = replacements ?? new Dictionary<string, string>();
    }

    public static TextCleaner FromPages(IEnumerable<PageLayout> pages, IReadOnlyDictionary<string, string>? replacements = null)
    {
        var words = new List<string>();
        foreach (var line in pages.SelectMany(p => p.Blocks).SelectMany(b => b.Lines))
        {
            for (var i = 0; i < line.Words.Count; i++)
            {
                var text = Normalize(line.Words[i].Text);
                // A line-final fragment such as "publish-" is not a word in its own right.
                if (i == line.Words.Count - 1 && text.EndsWith('-')) continue;
                var bare = Bare(text);
                if (bare.Length > 0) words.Add(bare);
            }
        }
        return new TextCleaner(words, replacements);
    }

    public string JoinLines(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();

        foreach (var raw in lines)
        {
            var line = Normalize(raw).Trim();
            if (line.Length == 0) continue;

            if (sb.Length == 0)
            {
                sb.Append(line);
                continue;
            }

            if (sb[^1] == '­')
            {
                sb.Length--;
                sb.Append(line);
            }
            else if (sb[^1] == '-' && sb.Length > 1 && char.IsLetterOrDigit(sb[^2]))
            {
                if (char.IsLetter(sb[^2]) && char.IsLower(line[0]))
                {
                    var joined = Bare(LastWord(sb).TrimEnd('-') + FirstWord(line));
                    if (_vocabulary.Contains(joined))
                    {
                        sb.Length--;
                    }
                }
                sb.Append(line);
            }
            else if (sb[^1] == '—')
            {
                sb.Append(line);
            }
            else
            {
                sb.Append(' ').Append(line);
            }
        }

        var text = Spaces().Replace(sb.ToString(), " ").Trim();
        foreach (var (from, to) in _replacements)
        {
            text = text.Replace(from, to, StringComparison.Ordinal);
        }
        return text;
    }

    // Character-level fixes only; never changes digits, currency or parentheses, which
    // have to survive exactly as printed ("(28,745)", "$874,466,000").
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case 'ﬀ': sb.Append("ff"); break;
                case 'ﬁ': sb.Append("fi"); break;
                case 'ﬂ': sb.Append("fl"); break;
                case 'ﬃ': sb.Append("ffi"); break;
                case 'ﬄ': sb.Append("ffl"); break;
                case '‘' or '’' or 'ʼ': sb.Append('\''); break;
                case '“' or '”': sb.Append('"'); break;
                case '‐' or '‑': sb.Append('-'); break;
                case ' ' or '\t': sb.Append(' '); break;
                case '​' or '‌' or '‍' or '﻿': break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string LastWord(StringBuilder sb)
    {
        var i = sb.Length - 1;
        while (i > 0 && !char.IsWhiteSpace(sb[i - 1])) i--;
        return sb.ToString(i, sb.Length - i);
    }

    private static string FirstWord(string line)
    {
        var end = line.IndexOf(' ');
        return end < 0 ? line : line[..end];
    }

    private static string Bare(string word) => NonLetters().Replace(word, "").ToLowerInvariant();

    [GeneratedRegex(@"[^\p{L}]")]
    private static partial Regex NonLetters();

    [GeneratedRegex(@"[ ]{2,}")]
    private static partial Regex Spaces();
}
