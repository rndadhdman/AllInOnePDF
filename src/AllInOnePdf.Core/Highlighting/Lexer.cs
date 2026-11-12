using System.Collections.Frozen;
using AllInOnePdf.Core.Rendering;
using K = AllInOnePdf.Core.Rendering.SyntaxTokenKind;

namespace AllInOnePdf.Core.Highlighting;

internal readonly record struct Token(SyntaxTokenKind Kind, int Start, int Length);

/// <summary>
/// Small, allocation-light, single-pass tokenizer. It is deliberately forgiving: it never throws on malformed input,
/// always makes progress and always covers the whole input with contiguous tokens (unrecognised text is Plain).
/// </summary>
internal sealed class Lexer
{
    private const string OperatorChars = "+-*/%=&|^!<>?:~";
    private const string PunctuationChars = "{}[]();,.";

    private readonly string _s;
    private readonly List<Token> _tokens;
    private int _lastEnd;

    private Lexer(string s)
    {
        _s = s;
        _tokens = new List<Token>(Math.Max(16, s.Length / 5));
    }

    public static List<Token> Run(string text, LanguageSpec spec)
    {
        var lx = new Lexer(text);
        lx.Lex(spec, 0, text.Length);
        lx.Emit(K.Plain, lx._lastEnd, text.Length);
        return lx._tokens;
    }

    // ------------------------------------------------------------------ plumbing

    private void Emit(SyntaxTokenKind kind, int start, int end)
    {
        if (start > _lastEnd)
        {
            Emit(K.Plain, _lastEnd, start);
        }

        if (start < _lastEnd)
        {
            start = _lastEnd;
        }

        if (end <= start)
        {
            return;
        }

        if (_tokens.Count > 0)
        {
            var last = _tokens[^1];
            if (last.Kind == kind && last.Start + last.Length == start)
            {
                _tokens[^1] = last with { Length = end - last.Start };
                _lastEnd = end;
                return;
            }
        }

        _tokens.Add(new Token(kind, start, end - start));
        _lastEnd = end;
    }

    private void Lex(LanguageSpec spec, int start, int end)
    {
        switch (spec.Lexer)
        {
            case LexerKind.Json:
                LexJson(spec, start, end);
                break;
            case LexerKind.Yaml:
                LexYaml(spec, start, end);
                break;
            case LexerKind.Markup:
                LexMarkup(spec, start, end);
                break;
            case LexerKind.Css:
                LexCss(spec, start, end);
                break;
            case LexerKind.Markdown:
                LexMarkdown(start, end);
                break;
            case LexerKind.Ini:
                LexIni(spec, start, end);
                break;
            case LexerKind.Toml:
                LexToml(spec, start, end);
                break;
            case LexerKind.Diff:
                LexDiff(start, end);
                break;
            case LexerKind.Plain:
                Emit(K.Plain, start, end);
                break;
            default:
                LexGeneric(spec, start, end, stopAtNewline: false);
                break;
        }
    }

    private bool At(int i, string lit, int end) =>
        i >= 0 && i + lit.Length <= end && _s.AsSpan(i, lit.Length).SequenceEqual(lit);

    private bool AtIgnoreCase(int i, string lit, int end) =>
        i >= 0 && i + lit.Length <= end && _s.AsSpan(i, lit.Length).Equals(lit, StringComparison.OrdinalIgnoreCase);

    private int IndexOf(string lit, int from, int end)
    {
        if (from >= end)
        {
            return -1;
        }

        var idx = _s.AsSpan(from, end - from).IndexOf(lit, StringComparison.Ordinal);
        return idx < 0 ? -1 : from + idx;
    }

    private int LineEnd(int i, int end)
    {
        if (i >= end)
        {
            return end;
        }

        var idx = _s.AsSpan(i, end - i).IndexOf('\n');
        return idx < 0 ? end : i + idx;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool Contains(FrozenSet<string> set, ReadOnlySpan<char> word)
    {
        if (set.Count == 0)
        {
            return false;
        }

        return set.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup) ? lookup.Contains(word) : set.Contains(word.ToString());
    }

    /// <summary>Scans a quoted string body starting after the opening quote; returns the index after the closing quote.</summary>
    private int ScanString(int i, int end, char quote, char escape, bool multiline)
    {
        var j = i;
        while (j < end)
        {
            var ch = _s[j];
            if (escape != '\0' && ch == escape && j + 1 < end)
            {
                j += 2;
                continue;
            }

            if (ch == quote)
            {
                return j + 1;
            }

            if (ch == '\n' && !multiline)
            {
                return j;
            }

            j++;
        }

        return end;
    }

    private int ScanTriple(int i, int end, string quotes)
    {
        var close = IndexOf(quotes, i + quotes.Length, end);
        if (close < 0)
        {
            return end;
        }

        var j = close + quotes.Length;
        while (j < end && _s[j] == quotes[0])
        {
            j++;
        }

        return j;
    }

    private int ScanNumber(int i, int end)
    {
        var s = _s;
        var j = i;
        if (s[j] == '0' && j + 1 < end && s[j + 1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O')
        {
            j += 2;
            while (j < end && (char.IsAsciiHexDigit(s[j]) || s[j] == '_'))
            {
                j++;
            }
        }
        else
        {
            while (j < end && (char.IsAsciiDigit(s[j]) || s[j] == '_'))
            {
                j++;
            }

            if (j + 1 < end && s[j] == '.' && char.IsAsciiDigit(s[j + 1]))
            {
                j++;
                while (j < end && (char.IsAsciiDigit(s[j]) || s[j] == '_'))
                {
                    j++;
                }
            }

            if (j < end && s[j] is 'e' or 'E')
            {
                var k = j + 1;
                if (k < end && s[k] is '+' or '-')
                {
                    k++;
                }

                if (k < end && char.IsAsciiDigit(s[k]))
                {
                    j = k;
                    while (j < end && char.IsAsciiDigit(s[j]))
                    {
                        j++;
                    }
                }
            }
        }

        while (j < end && char.IsAsciiLetterOrDigit(s[j]))
        {
            j++;
        }

        return j;
    }

    private int MatchBracket(int open, int end, char o, char c)
    {
        var depth = 0;
        for (var j = open; j < end; j++)
        {
            if (_s[j] == o)
            {
                depth++;
            }
            else if (_s[j] == c && --depth == 0)
            {
                return j + 1;
            }
            else if (_s[j] == '\n' && o != '{')
            {
                return j;
            }
        }

        return end;
    }

    private int SkipSpaces(int i, int end)
    {
        while (i < end && _s[i] is ' ' or '\t')
        {
            i++;
        }

        return i;
    }

    private static bool LooksNumeric(ReadOnlySpan<char> v)
    {
        if (v.IsEmpty)
        {
            return false;
        }

        var digits = 0;
        foreach (var ch in v)
        {
            if (char.IsAsciiDigit(ch))
            {
                digits++;
            }
            else if (ch is not ('.' or '-' or '+' or '_' or 'e' or 'E' or 'x' or 'X' or 'o') && !char.IsAsciiHexDigit(ch))
            {
                return false;
            }
        }

        return digits > 0 && (char.IsAsciiDigit(v[0]) || v[0] is '-' or '+' or '.');
    }

    // ------------------------------------------------------------------ generic (C family, scripts)

    private int LexGeneric(LanguageSpec sp, int start, int end, bool stopAtNewline)
    {
        var s = _s;
        var i = start;
        var lineStart = true;
        while (i < end)
        {
            var c = s[i];
            if (c == '\n')
            {
                if (stopAtNewline)
                {
                    return i;
                }

                Emit(K.Plain, i, i + 1);
                i++;
                lineStart = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                var w = i + 1;
                while (w < end && s[w] != '\n' && char.IsWhiteSpace(s[w]))
                {
                    w++;
                }

                Emit(K.Plain, i, w);
                i = w;
                continue;
            }

            var atLineStart = lineStart;
            lineStart = false;
            int j;

            if (sp.Preprocessor && atLineStart && c == '#')
            {
                j = LineEnd(i, end);
                Emit(K.Keyword, i, j);
                i = j;
                continue;
            }

            if (sp.HashBracketAttributes && c == '#' && (At(i, "#[", end) || At(i, "#![", end)))
            {
                j = MatchBracket(s[i + 1] == '!' ? i + 2 : i + 1, end, '[', ']');
                Emit(K.Attribute, i, j);
                i = j;
                continue;
            }

            if (TryComment(sp, ref i, start, end))
            {
                continue;
            }

            if (sp.TripleQuotes && (At(i, "\"\"\"", end) || (At(i, "'''", end) && sp.StringQuotes.Contains('\'', StringComparison.Ordinal))))
            {
                j = ScanTriple(i, end, s.Substring(i, 3));
                Emit(K.String, i, j);
                i = j;
                continue;
            }

            if (sp.CSharpStrings && c is '@' or '$')
            {
                var q = i;
                while (q < end && s[q] is '@' or '$' && q - i < 4)
                {
                    q++;
                }

                if (q < end && s[q] == '"')
                {
                    var verbatim = s.AsSpan(i, q - i).Contains('@');
                    if (At(q, "\"\"\"", end))
                    {
                        j = ScanTriple(q, end, "\"\"\"");
                    }
                    else if (verbatim)
                    {
                        j = q + 1;
                        while (j < end)
                        {
                            if (s[j] == '"')
                            {
                                if (j + 1 < end && s[j + 1] == '"')
                                {
                                    j += 2;
                                    continue;
                                }

                                j++;
                                break;
                            }

                            j++;
                        }
                    }
                    else
                    {
                        j = ScanString(q + 1, end, '"', '\\', multiline: false);
                    }

                    Emit(K.String, i, j);
                    i = j;
                    continue;
                }
            }

            if (sp.StringQuotes.Contains(c, StringComparison.Ordinal))
            {
                if (c == '\'' && sp.Lifetimes && i + 1 < end && IsIdentStart(s[i + 1]) && !(i + 2 < end && s[i + 2] == '\''))
                {
                    j = i + 1;
                    while (j < end && IsIdentPart(s[j]))
                    {
                        j++;
                    }

                    Emit(K.Variable, i, j);
                    i = j;
                    continue;
                }

                var escape = c == '\'' && sp.Name == "bash" ? '\0' : sp.EscapeChar;
                j = ScanString(i + 1, end, c, escape, sp.MultilineQuotes.Contains(c, StringComparison.Ordinal));
                Emit(K.String, i, j);
                i = j;
                continue;
            }

            if (sp.IdentifierQuotes.Contains(c, StringComparison.Ordinal))
            {
                j = ScanString(i + 1, end, c, '\0', multiline: false);
                Emit(K.Variable, i, j);
                i = j;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < end && char.IsAsciiDigit(s[i + 1]) && (i == start || !IsIdentPart(s[i - 1]))))
            {
                j = ScanNumber(i, end);
                Emit(K.Number, i, j);
                i = j;
                continue;
            }

            if (sp.DollarVariables && c == '$' && i + 1 < end)
            {
                var n = s[i + 1];
                if (n == '{')
                {
                    j = MatchBracket(i + 1, end, '{', '}');
                }
                else if (n == '(')
                {
                    Emit(K.Operator, i, i + 2);
                    i += 2;
                    continue;
                }
                else if (IsIdentStart(n))
                {
                    j = i + 2;
                    while (j < end && (IsIdentPart(s[j]) || (sp.DashInIdentifiers && s[j] == ':' && j + 1 < end && IsIdentStart(s[j + 1]))))
                    {
                        j++;
                    }
                }
                else if ("?!#@*0123456789_$-".Contains(n, StringComparison.Ordinal))
                {
                    j = i + 2;
                }
                else
                {
                    Emit(K.Operator, i, i + 1);
                    i++;
                    continue;
                }

                Emit(Contains(sp.Literals, s.AsSpan(i, j - i)) ? K.Number : K.Variable, i, j);
                i = j;
                continue;
            }

            if (c == '@' && (sp.AtAnnotations || sp.AtVariables) && i + 1 < end && IsIdentStart(s[i + 1]))
            {
                j = i + 1;
                while (j < end && (IsIdentPart(s[j]) || (sp.AtAnnotations && s[j] == '.' && j + 1 < end && IsIdentStart(s[j + 1]))))
                {
                    j++;
                }

                Emit(sp.AtAnnotations ? K.Attribute : K.Variable, i, j);
                i = j;
                continue;
            }

            if (c == '[' && ((sp.BracketAttributes && atLineStart) || sp.BracketTypes) && i + 1 < end && IsIdentStart(s[i + 1]))
            {
                var close = s.IndexOf(']', i + 1, LineEnd(i, end) - i - 1);
                if (close > 0)
                {
                    var ok = true;
                    if (sp.BracketTypes)
                    {
                        for (var k = i + 1; k < close; k++)
                        {
                            if (!IsIdentPart(s[k]) && s[k] is not ('.' or ',' or '[' or '`'))
                            {
                                ok = false;
                                break;
                            }
                        }
                    }

                    if (ok)
                    {
                        Emit(sp.BracketTypes ? K.Type : K.Attribute, i, close + 1);
                        i = close + 1;
                        continue;
                    }
                }
            }

            if (sp.DashOptions && c == '-' && i + 1 < end
                && (IsIdentStart(s[i + 1]) || (s[i + 1] == '-' && i + 2 < end && IsIdentStart(s[i + 2])))
                && (i == start || char.IsWhiteSpace(s[i - 1]) || s[i - 1] is '(' or '|'))
            {
                j = i + 1;
                while (j < end && (IsIdentPart(s[j]) || s[j] == '-'))
                {
                    j++;
                }

                Emit(Contains(sp.DashOperators, s.AsSpan(i, j - i)) ? K.Operator : K.Attribute, i, j);
                i = j;
                continue;
            }

            if (sp.Jsx && c == '<' && i + 1 < end && (IsIdentStart(s[i + 1]) || s[i + 1] is '/' or '>') && JsxAllowed(i, start))
            {
                i = LexJsxTag(sp, i, end);
                continue;
            }

            if (IsIdentStart(c))
            {
                j = i + 1;
                while (j < end && (IsIdentPart(s[j]) || (sp.DashInIdentifiers && s[j] == '-' && j + 1 < end && IsIdentStart(s[j + 1]))))
                {
                    j++;
                }

                var word = s.AsSpan(i, j - i);
                if (j < end && s[j] is '"' or '\'' && Contains(sp.StringPrefixes, word))
                {
                    var q = s[j];
                    int k;
                    if (sp.TripleQuotes && At(j, new string(q, 3), end))
                    {
                        k = ScanTriple(j, end, new string(q, 3));
                    }
                    else
                    {
                        var raw = word.Contains('r') || word.Contains('R');
                        k = ScanString(j + 1, end, q, raw ? '\0' : sp.EscapeChar, multiline: false);
                    }

                    Emit(K.String, i, k);
                    i = k;
                    continue;
                }

                if (sp.BangMacros && j < end && s[j] == '!' && !(j + 1 < end && s[j + 1] == '='))
                {
                    Emit(K.Function, i, j + 1);
                    i = j + 1;
                    continue;
                }

                Emit(Classify(sp, word, j, end), i, j);
                i = j;
                continue;
            }

            if (OperatorChars.Contains(c, StringComparison.Ordinal))
            {
                j = i + 1;
                while (j < end && j - i < 3 && OperatorChars.Contains(s[j], StringComparison.Ordinal) && !(s[j] == '/' && j + 1 < end && s[j + 1] is '/' or '*'))
                {
                    j++;
                }

                Emit(K.Operator, i, j);
                i = j;
                continue;
            }

            Emit(PunctuationChars.Contains(c, StringComparison.Ordinal) ? K.Punctuation : K.Plain, i, i + 1);
            i++;
        }

        return end;
    }

    private bool TryComment(LanguageSpec sp, ref int i, int start, int end)
    {
        foreach (var (open, close) in sp.BlockComments)
        {
            if (At(i, open, end))
            {
                var j = IndexOf(close, i + open.Length, end);
                j = j < 0 ? end : j + close.Length;
                Emit(K.Comment, i, j);
                i = j;
                return true;
            }
        }

        foreach (var lc in sp.LineComments)
        {
            if (At(i, lc, end) && (!(sp.HashCommentNeedsSpace && lc == "#") || i == start || char.IsWhiteSpace(_s[i - 1])))
            {
                var j = LineEnd(i, end);
                Emit(K.Comment, i, j);
                i = j;
                return true;
            }
        }

        return false;
    }

    private SyntaxTokenKind Classify(LanguageSpec sp, ReadOnlySpan<char> word, int after, int end)
    {
        if (Contains(sp.Keywords, word))
        {
            return K.Keyword;
        }

        if (Contains(sp.Literals, word))
        {
            return K.Number;
        }

        if (Contains(sp.Types, word))
        {
            return K.Type;
        }

        if (Contains(sp.SpecialVariables, word))
        {
            return K.Variable;
        }

        var k = SkipSpaces(after, end);
        if (k < end && _s[k] == '(')
        {
            return K.Function;
        }

        if (Contains(sp.Builtins, word))
        {
            return K.Function;
        }

        if (sp.DashInIdentifiers && word.Contains('-'))
        {
            return K.Function;
        }

        if (sp.CapitalizedTypes && char.IsUpper(word[0]))
        {
            foreach (var ch in word)
            {
                if (char.IsLower(ch))
                {
                    return K.Type;
                }
            }
        }

        return K.Plain;
    }

    private bool JsxAllowed(int i, int start)
    {
        var k = i - 1;
        while (k >= start && char.IsWhiteSpace(_s[k]))
        {
            k--;
        }

        if (k < start)
        {
            return true;
        }

        var p = _s[k];
        if ("(=,:?&|{}[;>".Contains(p, StringComparison.Ordinal))
        {
            return true;
        }

        return k >= start + 5 && _s.AsSpan(k - 5, 6).SequenceEqual("return");
    }

    private int LexJsxTag(LanguageSpec sp, int i, int end)
    {
        var s = _s;
        var j = i + 1;
        if (j < end && s[j] == '/')
        {
            j++;
        }

        Emit(K.Punctuation, i, j);
        var n = j;
        while (n < end && (IsIdentPart(s[n]) || s[n] is '.' or '-' or ':'))
        {
            n++;
        }

        if (n > j)
        {
            Emit(char.IsUpper(s[j]) ? K.Type : K.Keyword, j, n);
        }

        i = n;
        while (i < end)
        {
            var c = s[i];
            if (c == '>')
            {
                Emit(K.Punctuation, i, i + 1);
                return i + 1;
            }

            if (c == '/' && i + 1 < end && s[i + 1] == '>')
            {
                Emit(K.Punctuation, i, i + 2);
                return i + 2;
            }

            if (char.IsWhiteSpace(c))
            {
                Emit(K.Plain, i, i + 1);
                i++;
            }
            else if (IsIdentStart(c))
            {
                var a = i + 1;
                while (a < end && (IsIdentPart(s[a]) || s[a] is '-' or ':'))
                {
                    a++;
                }

                Emit(K.Attribute, i, a);
                i = a;
            }
            else if (c == '=')
            {
                Emit(K.Operator, i, i + 1);
                i++;
            }
            else if (c is '"' or '\'')
            {
                var q = ScanString(i + 1, end, c, '\\', multiline: true);
                Emit(K.String, i, q);
                i = q;
            }
            else if (c == '{')
            {
                var close = MatchBracket(i, end, '{', '}');
                Emit(K.Punctuation, i, i + 1);
                var innerEnd = close > i + 1 && s[close - 1] == '}' ? close - 1 : close;
                LexGeneric(sp, i + 1, innerEnd, stopAtNewline: false);
                Emit(K.Punctuation, innerEnd, close);
                i = close;
            }
            else
            {
                Emit(K.Plain, i, i + 1);
                i++;
            }
        }

        return end;
    }

    // ------------------------------------------------------------------ JSON

    private void LexJson(LanguageSpec sp, int start, int end)
    {
        var s = _s;
        var i = start;
        while (i < end)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                var w = i + 1;
                while (w < end && char.IsWhiteSpace(s[w]))
                {
                    w++;
                }

                Emit(K.Plain, i, w);
                i = w;
                continue;
            }

            if (TryComment(sp, ref i, start, end))
            {
                continue;
            }

            int j;
            if (c is '"' or '\'')
            {
                j = ScanString(i + 1, end, c, '\\', multiline: false);
                var k = j;
                while (k < end && char.IsWhiteSpace(s[k]))
                {
                    k++;
                }

                Emit(k < end && s[k] == ':' ? K.Attribute : K.String, i, j);
                i = j;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c is '-' or '+' && i + 1 < end && char.IsAsciiDigit(s[i + 1])))
            {
                j = ScanNumber(c is '-' or '+' ? i + 1 : i, end);
                Emit(K.Number, i, j);
                i = j;
                continue;
            }

            if (IsIdentStart(c))
            {
                j = i + 1;
                while (j < end && IsIdentPart(s[j]))
                {
                    j++;
                }

                var k = SkipSpaces(j, end);
                Emit(Contains(sp.Literals, s.AsSpan(i, j - i)) ? K.Number : k < end && s[k] == ':' ? K.Attribute : K.Plain, i, j);
                i = j;
                continue;
            }

            Emit(c is '{' or '}' or '[' or ']' or ':' or ',' ? K.Punctuation : K.Plain, i, i + 1);
            i++;
        }
    }

    // ------------------------------------------------------------------ YAML

    private void LexYaml(LanguageSpec sp, int start, int end)
    {
        var s = _s;
        var ls = start;
        while (ls < end)
        {
            var le = LineEnd(ls, end);
            var i = SkipSpaces(ls, le);
            Emit(K.Plain, ls, i);

            if (i < le && s[i] == '#')
            {
                Emit(K.Comment, i, le);
            }
            else if ((At(i, "---", le) || At(i, "...", le)) && s.AsSpan(i + 3, le - i - 3).IsWhiteSpace())
            {
                Emit(K.Punctuation, i, le);
            }
            else
            {
                while (i < le && s[i] == '-' && (i + 1 == le || s[i + 1] is ' ' or '\t'))
                {
                    Emit(K.Punctuation, i, i + 1);
                    i = SkipSpaces(i + 1, le);
                }

                // key: value
                var keyEnd = -1;
                var keyStart = i;
                if (i < le && s[i] is '"' or '\'')
                {
                    var q = ScanString(i + 1, le, s[i], s[i] == '"' ? '\\' : '\0', multiline: false);
                    var k = SkipSpaces(q, le);
                    if (k < le && s[k] == ':' && (k + 1 == le || s[k + 1] is ' ' or '\t'))
                    {
                        keyEnd = q;
                    }
                }
                else
                {
                    for (var k = i; k < le; k++)
                    {
                        var ch = s[k];
                        if (ch == '#' && k > i && s[k - 1] is ' ' or '\t')
                        {
                            break;
                        }

                        if (ch is '[' or '{' or '"' or '\'' && k == i)
                        {
                            break;
                        }

                        if (ch == ':' && (k + 1 == le || s[k + 1] is ' ' or '\t'))
                        {
                            keyEnd = k;
                            break;
                        }
                    }
                }

                if (keyEnd > keyStart)
                {
                    Emit(K.Attribute, keyStart, keyEnd);
                    var colon = s.IndexOf(':', keyEnd, le - keyEnd);
                    Emit(K.Plain, keyEnd, colon);
                    Emit(K.Punctuation, colon, colon + 1);
                    i = colon + 1;
                }

                LexYamlValue(sp, i, le);
            }

            if (le < end)
            {
                Emit(K.Plain, le, le + 1);
            }

            ls = le + 1;
        }
    }

    private void LexYamlValue(LanguageSpec sp, int i, int le)
    {
        var s = _s;
        while (i < le)
        {
            var c = s[i];
            if (c is ' ' or '\t')
            {
                var w = SkipSpaces(i, le);
                Emit(K.Plain, i, w);
                i = w;
                continue;
            }

            if (c == '#' && (i == 0 || s[i - 1] is ' ' or '\t'))
            {
                Emit(K.Comment, i, le);
                return;
            }

            int j;
            if (c is '"' or '\'')
            {
                j = ScanString(i + 1, le, c, c == '"' ? '\\' : '\0', multiline: false);
                Emit(K.String, i, j);
                i = j;
                continue;
            }

            if (c is '&' or '*' && i + 1 < le && !char.IsWhiteSpace(s[i + 1]))
            {
                j = i + 1;
                while (j < le && !char.IsWhiteSpace(s[j]) && s[j] is not (',' or ']' or '}'))
                {
                    j++;
                }

                Emit(K.Variable, i, j);
                i = j;
                continue;
            }

            if (c == '!')
            {
                j = i + 1;
                while (j < le && !char.IsWhiteSpace(s[j]))
                {
                    j++;
                }

                Emit(K.Type, i, j);
                i = j;
                continue;
            }

            if (c is '|' or '>' && s.AsSpan(i + 1, le - i - 1).TrimStart("+-0123456789").IsWhiteSpace())
            {
                Emit(K.Operator, i, le);
                return;
            }

            if (c is '[' or ']' or '{' or '}' or ',' or ':')
            {
                Emit(K.Punctuation, i, i + 1);
                i++;
                continue;
            }

            // plain scalar up to a comment, or a flow separator inside [..] / {..}
            j = i;
            while (j < le && !(s[j] == '#' && s[j - 1] is ' ' or '\t') && s[j] is not (',' or ']' or '}'))
            {
                j++;
            }

            var raw = s.AsSpan(i, j - i);
            var trimmed = raw.TrimEnd();
            var kind = Contains(sp.Literals, trimmed) || LooksNumeric(trimmed) ? K.Number : K.String;
            Emit(kind, i, i + trimmed.Length);
            Emit(K.Plain, i + trimmed.Length, j);
            i = j;
        }
    }

    // ------------------------------------------------------------------ XML / HTML

    private void LexMarkup(LanguageSpec sp, int start, int end)
    {
        var s = _s;
        var i = start;
        while (i < end)
        {
            int j;
            if (At(i, "<!--", end))
            {
                j = IndexOf("-->", i + 4, end);
                j = j < 0 ? end : j + 3;
                Emit(K.Comment, i, j);
                i = j;
                continue;
            }

            if (At(i, "<![CDATA[", end))
            {
                j = IndexOf("]]>", i + 9, end);
                j = j < 0 ? end : j + 3;
                Emit(K.String, i, j);
                i = j;
                continue;
            }

            if (At(i, "<?", end) || At(i, "<!", end))
            {
                j = IndexOf(">", i + 2, end);
                j = j < 0 ? end : j + 1;
                Emit(K.Keyword, i, j);
                i = j;
                continue;
            }

            var c = s[i];
            if (c == '<' && i + 1 < end && (IsIdentStart(s[i + 1]) || s[i + 1] == '/'))
            {
                var closing = s[i + 1] == '/';
                j = closing ? i + 2 : i + 1;
                Emit(K.Punctuation, i, j);
                var n = j;
                while (n < end && (IsIdentPart(s[n]) || s[n] is '.' or ':' or '-'))
                {
                    n++;
                }

                var tagName = s.AsSpan(j, n - j);
                Emit(K.Keyword, j, n);
                var isScript = !closing && tagName.Equals("script", StringComparison.OrdinalIgnoreCase);
                var isStyle = !closing && tagName.Equals("style", StringComparison.OrdinalIgnoreCase);
                var selfClosed = false;
                i = n;
                while (i < end)
                {
                    var ch = s[i];
                    if (ch == '>')
                    {
                        Emit(K.Punctuation, i, i + 1);
                        i++;
                        break;
                    }

                    if (ch == '/' && i + 1 < end && s[i + 1] == '>')
                    {
                        Emit(K.Punctuation, i, i + 2);
                        i += 2;
                        selfClosed = true;
                        break;
                    }

                    if (char.IsWhiteSpace(ch))
                    {
                        Emit(K.Plain, i, i + 1);
                        i++;
                    }
                    else if (IsIdentStart(ch) || ch is '@' or ':' or '#' or '*' or '[' or '(')
                    {
                        var a = i + 1;
                        while (a < end && !char.IsWhiteSpace(s[a]) && s[a] is not ('=' or '>' or '"' or '\'') && !(s[a] == '/' && a + 1 < end && s[a + 1] == '>'))
                        {
                            a++;
                        }

                        Emit(K.Attribute, i, a);
                        i = a;
                    }
                    else if (ch == '=')
                    {
                        Emit(K.Operator, i, i + 1);
                        i++;
                    }
                    else if (ch is '"' or '\'')
                    {
                        var q = ScanString(i + 1, end, ch, '\0', multiline: true);
                        Emit(K.String, i, q);
                        i = q;
                    }
                    else
                    {
                        Emit(K.Plain, i, i + 1);
                        i++;
                    }
                }

                if ((isScript || isStyle) && !selfClosed && i < end)
                {
                    var closeTag = isScript ? "</script" : "</style";
                    var k = i;
                    var found = -1;
                    while (k < end)
                    {
                        var lt = IndexOf("</", k, end);
                        if (lt < 0)
                        {
                            break;
                        }

                        if (AtIgnoreCase(lt, closeTag, end))
                        {
                            found = lt;
                            break;
                        }

                        k = lt + 2;
                    }

                    var innerEnd = found < 0 ? end : found;
                    Lex(isScript ? Languages.Script : Languages.Style, i, innerEnd);
                    i = innerEnd;
                }

                continue;
            }

            if (c == '&')
            {
                var semi = s.IndexOf(';', i, Math.Min(12, end - i));
                if (semi > i + 1)
                {
                    Emit(K.Variable, i, semi + 1);
                    i = semi + 1;
                    continue;
                }
            }

            j = i + 1;
            while (j < end && s[j] is not ('<' or '&'))
            {
                j++;
            }

            Emit(K.Plain, i, j);
            i = j;
        }

        _ = sp;
    }

    // ------------------------------------------------------------------ CSS / SCSS

    private void LexCss(LanguageSpec sp, int start, int end)
    {
        var s = _s;
        var i = start;
        var depth = 0;
        var inValue = false;
        var expectColon = false;
        while (i < end)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                var w = i + 1;
                while (w < end && char.IsWhiteSpace(s[w]))
                {
                    w++;
                }

                Emit(K.Plain, i, w);
                i = w;
                continue;
            }

            if (!(inValue && c == '/' && i > start && s[i - 1] == ':') && TryComment(sp, ref i, start, end))
            {
                continue;
            }

            int j;
            if (c is '"' or '\'')
            {
                j = ScanString(i + 1, end, c, '\\', multiline: false);
                Emit(K.String, i, j);
                i = j;
                continue;
            }

            if (c == '@' && i + 1 < end && IsIdentStart(s[i + 1]))
            {
                j = ScanCssIdent(i + 1, end);
                Emit(K.Keyword, i, j);
                i = j;
                continue;
            }

            if ((c == '$' && sp.DollarVariables && i + 1 < end && IsIdentStart(s[i + 1])) || (c == '-' && At(i, "--", end) && i + 2 < end && IsIdentStart(s[i + 2])))
            {
                j = ScanCssIdent(i + 1, end);
                Emit(K.Variable, i, j);
                if (!inValue && depth >= 0)
                {
                    var k = SkipSpaces(j, end);
                    expectColon = k < end && s[k] == ':';
                }

                i = j;
                continue;
            }

            if (c == '#' && i + 1 < end && (IsIdentPart(s[i + 1]) || s[i + 1] == '-'))
            {
                j = ScanCssIdent(i + 1, end);
                Emit(inValue ? K.Number : K.Type, i, j);
                i = j;
                continue;
            }

            if (c == '.' && !inValue && i + 1 < end && (IsIdentStart(s[i + 1]) || s[i + 1] == '-'))
            {
                j = ScanCssIdent(i + 1, end);
                Emit(K.Type, i, j);
                i = j;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c is '.' or '-' or '+' && inValue && i + 1 < end && char.IsAsciiDigit(s[i + 1])))
            {
                j = c is '-' or '+' ? i + 1 : i;
                while (j < end && (char.IsAsciiDigit(s[j]) || s[j] == '.'))
                {
                    j++;
                }

                while (j < end && (char.IsAsciiLetter(s[j]) || s[j] == '%'))
                {
                    j++;
                }

                Emit(K.Number, i, j);
                i = j;
                continue;
            }

            switch (c)
            {
                case '{':
                    depth++;
                    inValue = false;
                    expectColon = false;
                    Emit(K.Punctuation, i, i + 1);
                    i++;
                    continue;
                case '}':
                    depth = Math.Max(0, depth - 1);
                    inValue = false;
                    expectColon = false;
                    Emit(K.Punctuation, i, i + 1);
                    i++;
                    continue;
                case ';':
                    inValue = false;
                    expectColon = false;
                    Emit(K.Punctuation, i, i + 1);
                    i++;
                    continue;
                case ':':
                    if (expectColon)
                    {
                        inValue = true;
                        expectColon = false;
                        Emit(K.Punctuation, i, i + 1);
                        i++;
                    }
                    else if (inValue)
                    {
                        Emit(K.Punctuation, i, i + 1);
                        i++;
                    }
                    else
                    {
                        j = i + 1;
                        if (j < end && s[j] == ':')
                        {
                            j++;
                        }

                        j = ScanCssIdent(j, end);
                        Emit(K.Attribute, i, j);
                        i = j;
                    }

                    continue;
                case '!':
                    j = ScanCssIdent(i + 1, end);
                    Emit(K.Keyword, i, j);
                    i = j;
                    continue;
            }

            if (IsIdentStart(c) || c == '-')
            {
                j = ScanCssIdent(i + 1, end);
                if (inValue)
                {
                    Emit(j < end && s[j] == '(' ? K.Function : K.Plain, i, j);
                }
                else if (depth > 0 && IsCssPropertyAhead(j, end))
                {
                    Emit(K.Attribute, i, j);
                    expectColon = true;
                }
                else
                {
                    Emit(K.Keyword, i, j);
                }

                i = j;
                continue;
            }

            Emit(c is '(' or ')' or ',' or '[' or ']' ? K.Punctuation : c is '>' or '+' or '~' or '&' or '*' or '=' ? K.Operator : K.Plain, i, i + 1);
            i++;
        }
    }

    private int ScanCssIdent(int j, int end)
    {
        while (j < end && (IsIdentPart(_s[j]) || _s[j] == '-'))
        {
            j++;
        }

        return j;
    }

    private bool IsCssPropertyAhead(int j, int end)
    {
        var k = SkipSpaces(j, end);
        if (k >= end || _s[k] != ':')
        {
            return false;
        }

        for (var m = k; m < end; m++)
        {
            switch (_s[m])
            {
                case ';':
                case '}':
                    return true;
                case '{':
                    return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------ Markdown

    private void LexMarkdown(int start, int end)
    {
        var s = _s;
        var ls = start;
        string? fence = null;
        while (ls < end)
        {
            var le = LineEnd(ls, end);
            var t = SkipSpaces(ls, le);
            Emit(K.Plain, ls, t);

            if (fence is not null)
            {
                if (At(t, fence, le))
                {
                    Emit(K.Punctuation, t, le);
                    fence = null;
                }
                else
                {
                    Emit(K.String, t, le);
                }
            }
            else if (At(t, "```", le) || At(t, "~~~", le))
            {
                fence = s.Substring(t, 3);
                Emit(K.Punctuation, t, t + 3);
                Emit(K.Attribute, t + 3, le);
            }
            else if (t < le && s[t] == '#' && IsAtxHeading(t, le))
            {
                Emit(K.Keyword, t, le);
            }
            else if (t < le && s[t] == '>')
            {
                Emit(K.Comment, t, le);
            }
            else if (IsHorizontalRule(t, le))
            {
                Emit(K.Punctuation, t, le);
            }
            else
            {
                var i = t;
                if (i + 1 < le && s[i] is '-' or '*' or '+' && s[i + 1] == ' ')
                {
                    Emit(K.Punctuation, i, i + 1);
                    i++;
                }
                else
                {
                    var d = i;
                    while (d < le && char.IsAsciiDigit(s[d]))
                    {
                        d++;
                    }

                    if (d > i && d + 1 < le && s[d] is '.' or ')' && s[d + 1] == ' ')
                    {
                        Emit(K.Punctuation, i, d + 1);
                        i = d + 1;
                    }
                }

                var b = SkipSpaces(i, le);
                if (b + 2 < le && s[b] == '[' && s[b + 1] is ' ' or 'x' or 'X' && s[b + 2] == ']')
                {
                    Emit(K.Plain, i, b);
                    Emit(K.Keyword, b, b + 3);
                    i = b + 3;
                }

                LexMarkdownInline(i, le);
            }

            if (le < end)
            {
                Emit(K.Plain, le, le + 1);
            }

            ls = le + 1;
        }
    }

    private bool IsAtxHeading(int t, int le)
    {
        var h = t;
        while (h < le && _s[h] == '#')
        {
            h++;
        }

        return h - t <= 6 && (h == le || _s[h] == ' ');
    }

    private bool IsHorizontalRule(int t, int le)
    {
        if (t >= le || _s[t] is not ('-' or '*' or '_'))
        {
            return false;
        }

        var ch = _s[t];
        var count = 0;
        for (var k = t; k < le; k++)
        {
            if (_s[k] == ch)
            {
                count++;
            }
            else if (_s[k] is not (' ' or '\t'))
            {
                return false;
            }
        }

        return count >= 3;
    }

    private void LexMarkdownInline(int i, int le)
    {
        var s = _s;
        while (i < le)
        {
            var c = s[i];
            int j;
            if (c == '`')
            {
                var n = i;
                while (n < le && s[n] == '`')
                {
                    n++;
                }

                var ticks = s.Substring(i, n - i);
                var close = IndexOf(ticks, n, le);
                j = close < 0 ? n : close + ticks.Length;
                Emit(close < 0 ? K.Plain : K.String, i, j);
                i = j;
                continue;
            }

            if ((At(i, "**", le) || At(i, "__", le)) && i + 2 < le)
            {
                var mark = s.Substring(i, 2);
                var close = IndexOf(mark, i + 2, le);
                if (close > i + 2)
                {
                    Emit(K.Keyword, i, close + 2);
                    i = close + 2;
                    continue;
                }
            }

            if (c is '*' or '_' && i + 1 < le && !char.IsWhiteSpace(s[i + 1]) && (c == '*' || i == 0 || !IsIdentPart(s[i - 1])))
            {
                var close = s.IndexOf(c, i + 1, le - i - 1);
                if (close > i + 1)
                {
                    Emit(K.Variable, i, close + 1);
                    i = close + 1;
                    continue;
                }
            }

            if (c == '[' || (c == '!' && i + 1 < le && s[i + 1] == '['))
            {
                var open = c == '!' ? i + 1 : i;
                var closeBracket = s.IndexOf(']', open + 1, le - open - 1);
                if (closeBracket > 0 && closeBracket + 1 < le && s[closeBracket + 1] == '(')
                {
                    var closeParen = s.IndexOf(')', closeBracket + 2, le - closeBracket - 2);
                    if (closeParen > 0)
                    {
                        Emit(K.Function, i, closeBracket + 1);
                        Emit(K.String, closeBracket + 1, closeParen + 1);
                        i = closeParen + 1;
                        continue;
                    }
                }
            }

            if (c == '<')
            {
                var close = s.IndexOf('>', i + 1, le - i - 1);
                if (close > i + 1 && (IsIdentStart(s[i + 1]) || s[i + 1] == '/'))
                {
                    Emit(K.Keyword, i, close + 1);
                    i = close + 1;
                    continue;
                }
            }

            if (c == '\\' && i + 1 < le)
            {
                Emit(K.Operator, i, i + 2);
                i += 2;
                continue;
            }

            if (c == '|')
            {
                Emit(K.Punctuation, i, i + 1);
                i++;
                continue;
            }

            j = i + 1;
            while (j < le && s[j] is not ('`' or '*' or '_' or '[' or '!' or '<' or '\\' or '|'))
            {
                j++;
            }

            Emit(K.Plain, i, j);
            i = j;
        }
    }

    // ------------------------------------------------------------------ INI / TOML

    private void LexIni(LanguageSpec sp, int start, int end)
    {
        var s = _s;
        var ls = start;
        while (ls < end)
        {
            var le = LineEnd(ls, end);
            var t = SkipSpaces(ls, le);
            Emit(K.Plain, ls, t);
            if (t < le && s[t] is ';' or '#')
            {
                Emit(K.Comment, t, le);
            }
            else if (t < le && s[t] == '[')
            {
                var close = s.IndexOf(']', t, le - t);
                var j = close < 0 ? le : close + 1;
                Emit(K.Type, t, j);
                Emit(K.Plain, j, le);
            }
            else
            {
                var eq = s.AsSpan(t, le - t).IndexOfAny('=', ':');
                if (eq < 0)
                {
                    Emit(K.Plain, t, le);
                }
                else
                {
                    eq += t;
                    var keyEnd = eq;
                    while (keyEnd > t && s[keyEnd - 1] is ' ' or '\t')
                    {
                        keyEnd--;
                    }

                    Emit(K.Attribute, t, keyEnd);
                    Emit(K.Plain, keyEnd, eq);
                    Emit(K.Operator, eq, eq + 1);
                    var v = SkipSpaces(eq + 1, le);
                    Emit(K.Plain, eq + 1, v);
                    if (v < le)
                    {
                        if (s[v] is '"' or '\'')
                        {
                            var q = ScanString(v + 1, le, s[v], '\\', multiline: false);
                            Emit(K.String, v, q);
                            Emit(K.Plain, q, le);
                        }
                        else
                        {
                            var value = s.AsSpan(v, le - v).TrimEnd();
                            Emit(Contains(sp.Literals, value) || LooksNumeric(value) ? K.Number : K.String, v, v + value.Length);
                            Emit(K.Plain, v + value.Length, le);
                        }
                    }
                }
            }

            if (le < end)
            {
                Emit(K.Plain, le, le + 1);
            }

            ls = le + 1;
        }
    }

    private void LexToml(LanguageSpec sp, int start, int end)
    {
        var s = _s;
        var i = start;
        while (i < end)
        {
            var le = LineEnd(i, end);
            var t = SkipSpaces(i, le);
            Emit(K.Plain, i, t);
            var next = le;
            if (t < le && s[t] == '#')
            {
                Emit(K.Comment, t, le);
            }
            else if (t < le && s[t] == '[' && IsTomlHeader(t, le))
            {
                var close = s.IndexOf(']', t, le - t);
                var j = close + 1;
                while (j < le && s[j] == ']')
                {
                    j++;
                }

                Emit(K.Type, t, j);
                next = LexGeneric(sp, j, end, stopAtNewline: true);
            }
            else
            {
                var eq = FindTomlKeyEquals(t, le);
                var v = t;
                if (eq > t)
                {
                    var keyEnd = eq;
                    while (keyEnd > t && s[keyEnd - 1] is ' ' or '\t')
                    {
                        keyEnd--;
                    }

                    Emit(K.Attribute, t, keyEnd);
                    Emit(K.Plain, keyEnd, eq);
                    Emit(K.Operator, eq, eq + 1);
                    v = eq + 1;
                }

                next = LexGeneric(sp, v, end, stopAtNewline: true);
            }

            if (next < end)
            {
                Emit(K.Plain, next, next + 1);
            }

            i = next + 1;
        }
    }

    private bool IsTomlHeader(int t, int le)
    {
        var close = _s.IndexOf(']', t, le - t);
        if (close < 0)
        {
            return false;
        }

        for (var k = t + 1; k < close; k++)
        {
            if (!IsIdentPart(_s[k]) && _s[k] is not ('.' or '-' or '[' or '"' or ' '))
            {
                return false;
            }
        }

        return close > t + 1;
    }

    private int FindTomlKeyEquals(int t, int le)
    {
        for (var k = t; k < le; k++)
        {
            var ch = _s[k];
            if (ch == '=')
            {
                return k;
            }

            if (ch == '"' && k == t)
            {
                k = ScanString(k + 1, le, '"', '\\', multiline: false) - 1;
                continue;
            }

            if (!IsIdentPart(ch) && ch is not ('.' or '-' or ' ' or '\t' or '"'))
            {
                return -1;
            }
        }

        return -1;
    }

    // ------------------------------------------------------------------ diff

    private void LexDiff(int start, int end)
    {
        var s = _s;
        var ls = start;
        while (ls < end)
        {
            var le = LineEnd(ls, end);
            if (At(ls, "+++", le) || At(ls, "---", le) || At(ls, "diff ", le) || At(ls, "index ", le) || At(ls, "new file", le) || At(ls, "deleted file", le) || At(ls, "similarity", le) || At(ls, "rename ", le))
            {
                Emit(K.Keyword, ls, le);
            }
            else if (At(ls, "@@", le))
            {
                var close = IndexOf("@@", ls + 2, le);
                var j = close < 0 ? le : close + 2;
                Emit(K.Attribute, ls, j);
                Emit(K.Plain, j, le);
            }
            else if (ls < le && s[ls] is '+' or '>')
            {
                Emit(K.Inserted, ls, le);
            }
            else if (ls < le && s[ls] is '-' or '<')
            {
                Emit(K.Deleted, ls, le);
            }
            else if (ls < le && s[ls] == '\\')
            {
                Emit(K.Comment, ls, le);
            }
            else
            {
                Emit(K.Plain, ls, le);
            }

            if (le < end)
            {
                Emit(K.Plain, le, le + 1);
            }

            ls = le + 1;
        }
    }
}
