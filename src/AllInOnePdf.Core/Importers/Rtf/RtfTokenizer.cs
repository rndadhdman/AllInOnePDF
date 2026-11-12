namespace AllInOnePdf.Core.Importers.Rtf;

internal enum RtfTokenKind
{
    Eof,
    GroupStart,
    GroupEnd,
    /// <summary><c>\word</c> with an optional signed numeric parameter.</summary>
    ControlWord,
    /// <summary><c>\x</c> for one non-letter character (except <c>\'</c>, which is <see cref="HexByte"/>).</summary>
    ControlSymbol,
    /// <summary><c>\'hh</c>: one raw byte in the current code page.</summary>
    HexByte,
    /// <summary>A run of plain text bytes (no braces, backslashes or line breaks).</summary>
    Text,
    /// <summary>The raw bytes that follow <c>\binN</c>.</summary>
    Binary,
}

internal readonly record struct RtfToken(
    RtfTokenKind Kind,
    string Word = "",
    int Parameter = 0,
    bool HasParameter = false,
    char Symbol = '\0',
    int Start = 0,
    int Length = 0,
    byte Byte = 0);

/// <summary>
/// Splits RTF bytes into tokens. Works on bytes, not decoded text, so the builder can decode runs of text and
/// <c>\'hh</c> escapes together in the right code page. A missing closing brace is tolerated (EOF closes every
/// open group) and reported through <see cref="Unbalanced"/>.
/// </summary>
internal sealed class RtfTokenizer
{
    private const int MaxWordLength = 32;

    private readonly byte[] _data;
    private readonly Dictionary<int, string> _words = [];
    private int _pos;

    public RtfTokenizer(byte[] data, int start = 0)
    {
        _data = data;
        _pos = start;
    }

    public int Position => _pos;

    public int Length => _data.Length;

    /// <summary>Current group nesting depth.</summary>
    public int Depth { get; private set; }

    /// <summary>True when a closing brace had no matching opening one, or the input ended inside a group.</summary>
    public bool Unbalanced { get; private set; }

    public ReadOnlySpan<byte> Slice(int start, int length) => _data.AsSpan(start, length);

    public RtfToken Next()
    {
        while (_pos < _data.Length)
        {
            var b = _data[_pos];
            switch (b)
            {
                case (byte)'{':
                    _pos++;
                    Depth++;
                    return new RtfToken(RtfTokenKind.GroupStart);
                case (byte)'}':
                    _pos++;
                    if (Depth == 0)
                    {
                        Unbalanced = true;
                        continue;
                    }

                    Depth--;
                    return new RtfToken(RtfTokenKind.GroupEnd);
                case (byte)'\\':
                    return ReadControl();
                case (byte)'\r' or (byte)'\n' or 0:
                    _pos++;
                    continue;
                default:
                    return ReadText();
            }
        }

        if (Depth > 0)
        {
            Unbalanced = true;
        }

        return new RtfToken(RtfTokenKind.Eof);
    }

    private RtfToken ReadText()
    {
        var start = _pos;
        while (_pos < _data.Length)
        {
            var b = _data[_pos];
            if (b is (byte)'{' or (byte)'}' or (byte)'\\' or (byte)'\r' or (byte)'\n' or 0)
            {
                break;
            }

            _pos++;
        }

        return new RtfToken(RtfTokenKind.Text, Start: start, Length: _pos - start);
    }

    private RtfToken ReadControl()
    {
        _pos++; // backslash
        if (_pos >= _data.Length)
        {
            return new RtfToken(RtfTokenKind.ControlSymbol, Symbol: '\\');
        }

        var c = _data[_pos];
        if (!IsLetter(c))
        {
            _pos++;
            if (c == '\'')
            {
                if (_pos + 1 < _data.Length && HexValue(_data[_pos]) is var hi and >= 0 && HexValue(_data[_pos + 1]) is var lo and >= 0)
                {
                    _pos += 2;
                    return new RtfToken(RtfTokenKind.HexByte, Byte: (byte)((hi << 4) | lo));
                }

                // A damaged escape: drop it.
                return new RtfToken(RtfTokenKind.ControlSymbol, Symbol: ':');
            }

            // "\<CR>" and "\<LF>" are the same as \par.
            return c is (byte)'\r' or (byte)'\n'
                ? new RtfToken(RtfTokenKind.ControlWord, Word: "par")
                : new RtfToken(RtfTokenKind.ControlSymbol, Symbol: (char)c);
        }

        var wordStart = _pos;
        var hash = 17;
        while (_pos < _data.Length && IsLetter(_data[_pos]) && _pos - wordStart < MaxWordLength)
        {
            hash = unchecked((hash * 31) + _data[_pos]);
            _pos++;
        }

        var word = Intern(wordStart, _pos - wordStart, hash);

        var hasParameter = false;
        long value = 0;
        var negative = false;
        if (_pos < _data.Length && _data[_pos] == '-' && _pos + 1 < _data.Length && IsDigit(_data[_pos + 1]))
        {
            negative = true;
            _pos++;
        }

        while (_pos < _data.Length && IsDigit(_data[_pos]))
        {
            hasParameter = true;
            if (value < int.MaxValue)
            {
                value = (value * 10) + (_data[_pos] - '0');
            }

            _pos++;
        }

        if (_pos < _data.Length && _data[_pos] == ' ')
        {
            _pos++;
        }

        var parameter = (int)Math.Clamp(negative ? -value : value, int.MinValue, int.MaxValue);

        if (hasParameter && parameter > 0 && string.Equals(word, "bin", StringComparison.Ordinal))
        {
            var start = _pos;
            var length = (int)Math.Min(parameter, (long)_data.Length - _pos);
            _pos += length;
            return new RtfToken(RtfTokenKind.Binary, Start: start, Length: length);
        }

        return new RtfToken(RtfTokenKind.ControlWord, word, parameter, hasParameter);
    }

    private string Intern(int start, int length, int hash)
    {
        var key = unchecked((hash * 31) + length);
        if (_words.TryGetValue(key, out var cached) && cached.Length == length && Matches(cached, start))
        {
            return cached;
        }

        var word = string.Create(length, (_data, start), static (span, state) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = (char)state._data[state.start + i];
            }
        });
        _words[key] = word;
        return word;
    }

    private bool Matches(string word, int start)
    {
        for (var i = 0; i < word.Length; i++)
        {
            if (word[i] != _data[start + i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLetter(byte b) => (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z');

    private static bool IsDigit(byte b) => b >= '0' && b <= '9';

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
