using System.Text;
using System.Xml;

namespace AllInOnePdf.Core.Exporters.Epub;

/// <summary>Small XML helpers for the hand-written package documents (OPF, NCX, nav, cover).</summary>
internal static class EpubXml
{
    public const string XhtmlNs = "http://www.w3.org/1999/xhtml";
    public const string OpsNs = "http://www.idpf.org/2007/ops";

    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Escapes text for element content and double-quoted attribute values, dropping characters XML 1.0 forbids.</summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length + 16);
        foreach (var ch in StripInvalid(text))
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(ch); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Removes characters that are not allowed in XML 1.0 documents (control characters, lone surrogates).</summary>
    public static string StripInvalid(string text)
    {
        var clean = true;
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsValidAt(text, i, out var width))
            {
                clean = false;
                break;
            }

            i += width - 1;
        }

        if (clean)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (IsValidAt(text, i, out var width))
            {
                sb.Append(text, i, width);
            }

            i += width - 1;
        }

        return sb.ToString();
    }

    private static bool IsValidAt(string text, int i, out int width)
    {
        var ch = text[i];
        width = 1;
        if (char.IsHighSurrogate(ch))
        {
            if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                width = 2;
                return true;
            }

            return false;
        }

        return XmlConvert.IsXmlChar(ch);
    }

    /// <summary>A safe file-system and XML-id friendly token: ASCII letters, digits and dashes, starting with a letter.</summary>
    public static string Token(string? text, string fallback)
    {
        var sb = new StringBuilder();
        foreach (var ch in (text ?? string.Empty).ToLowerInvariant())
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var token = sb.ToString().Trim('-');
        if (token.Length > 40)
        {
            token = token[..40].TrimEnd('-');
        }

        if (token.Length == 0)
        {
            return fallback;
        }

        return char.IsLetter(token[0]) ? token : fallback + "-" + token;
    }

    /// <summary>True when <paramref name="xml"/> parses as a well-formed document (the DOCTYPE is skipped, never processed).</summary>
    public static bool IsWellFormed(string xml)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            while (reader.Read())
            {
            }

            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>The XHTML document wrapper shared by chapters, the cover and the navigation document.</summary>
    public static string XhtmlDocument(string language, string title, string stylesheetHref, string body, string? bodyAttributes = null)
    {
        var lang = Escape(language);
        var sb = new StringBuilder(body.Length + 512);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!DOCTYPE html>\n");
        sb.Append("<html xmlns=\"").Append(XhtmlNs).Append("\" xmlns:epub=\"").Append(OpsNs).Append("\" lang=\"").Append(lang).Append("\" xml:lang=\"").Append(lang).Append("\">\n");
        sb.Append("<head>\n<meta charset=\"utf-8\" />\n<title>").Append(Escape(title)).Append("</title>\n");
        sb.Append("<link rel=\"stylesheet\" type=\"text/css\" href=\"").Append(Escape(stylesheetHref)).Append("\" />\n</head>\n");
        sb.Append("<body").Append(bodyAttributes is null ? string.Empty : " " + bodyAttributes).Append(">\n");
        sb.Append(body);
        sb.Append("\n</body>\n</html>\n");
        return sb.ToString();
    }
}
