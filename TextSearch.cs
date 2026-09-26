using System.Text;
using System.Text.RegularExpressions;

namespace MarkdownPad;

/// <summary>
/// A match found by <see cref="SearchQuery"/>: where it is in the text, plus the regex match behind it
/// (on the text with "\n" line breaks), which fills in $1-style replacements.
/// </summary>
internal readonly record struct SearchHit(int Offset, int Length, Match Match);

/// <summary>
/// Find/replace matching with the options VS Code has: match case, whole word and regular expressions.
/// Searching runs on the text with every line break as "\n", so ^ and $ work per line and \n spans a
/// line break whatever the file uses; hits are mapped back to offsets in the real text.
/// </summary>
internal sealed class SearchQuery
{
    private readonly Regex? _regex;
    private readonly bool _useRegex;

    /// <exception cref="ArgumentException">The regular expression is invalid.</exception>
    public SearchQuery(string text, bool matchCase, bool wholeWord, bool useRegex)
    {
        _useRegex = useRegex;
        if (text.Length == 0) return;
        var pattern = useRegex ? text : Regex.Escape(text);
        // A whole-word edge sits next to a non-word character, or is one itself (so "-foo" is found
        // in "a-foo", as in VS Code).
        if (wholeWord) pattern = $@"(?:(?<!\w)|(?=\W))(?:{pattern})(?:(?!\w)|(?<=\W))";
        var options = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        if (!matchCase) options |= RegexOptions.IgnoreCase;
        _regex = new Regex(pattern, options, TimeSpan.FromSeconds(1));
    }

    public bool IsEmpty => _regex is null;

    /// <exception cref="RegexMatchTimeoutException">The pattern took too long on this text.</exception>
    public List<SearchHit> FindAll(string text, int limit)
    {
        var hits = new List<SearchHit>();
        if (_regex is null) return hits;
        var lf = new LfText(text);
        for (var m = _regex.Match(lf.Text); m.Success && hits.Count < limit; m = m.NextMatch())
        {
            int start = lf.ToOriginal(m.Index);
            hits.Add(new SearchHit(start, lf.ToOriginal(m.Index + m.Length) - start, m));
        }
        return hits;
    }

    /// <summary>
    /// The text to put in place of a hit. With regular expressions $1, ${name}, $&amp; and $$ work as in
    /// VS Code, and \n, \t and \\ are escapes; otherwise it's used exactly as typed.
    /// </summary>
    public string ReplacementFor(SearchHit hit, string replaceWith, string newline)
    {
        if (!_useRegex) return replaceWith;
        var result = hit.Match.Result(Unescape(replaceWith));
        return newline == "\n" ? result : result.Replace("\n", newline);
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char next = i + 1 < s.Length ? s[i + 1] : '\0';
            if (s[i] == '\\' && next is 'n' or 't' or '\\')
            {
                sb.Append(next switch { 'n' => '\n', 't' => '\t', _ => '\\' });
                i++;
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Text with every line break as "\n", and the way back to offsets in the original.</summary>
    private sealed class LfText
    {
        public readonly string Text;
        private readonly List<int> _lfStarts = new() { 0 }, _starts = new() { 0 };

        public LfText(string original)
        {
            Text = original;
            if (!original.Contains('\r')) return; // already "\n" only: offsets are the same
            var sb = new StringBuilder(original.Length);
            for (int i = 0; i < original.Length; i++)
            {
                char c = original[i];
                if (c == '\r' && i + 1 < original.Length && original[i + 1] == '\n') i++;
                sb.Append(c == '\r' ? '\n' : c);
                if (c is '\r' or '\n') { _lfStarts.Add(sb.Length); _starts.Add(i + 1); }
            }
            Text = sb.ToString();
        }

        public int ToOriginal(int offset)
        {
            if (_lfStarts.Count == 1) return offset;
            int line = _lfStarts.BinarySearch(offset);
            if (line < 0) line = ~line - 1;
            return _starts[line] + offset - _lfStarts[line];
        }
    }
}
