using System.Globalization;
using System.Text;

namespace AllInOnePdf.Core.Exporters.Epub;

/// <summary>Builds the EPUB 3 navigation document (<c>nav.xhtml</c>) and the EPUB 2 fallback (<c>toc.ncx</c>).</summary>
internal static class EpubNavigation
{
    public const string NavHref = "nav.xhtml";
    public const string NcxHref = "toc.ncx";

    private sealed class Node
    {
        public required int Level { get; init; }
        public required string Label { get; init; }
        public required string Href { get; init; }
        public List<Node> Children { get; } = [];
    }

    /// <summary>Landmarks / guide targets, relative to <c>OEBPS/</c>.</summary>
    public sealed record Targets(string? CoverHref, bool NavInSpine, string FirstChapterHref);

    public static string BuildNav(IReadOnlyList<EpubChapter> chapters, int depth, string tocTitle, string language, Targets targets)
    {
        var roots = BuildTree(chapters, depth);
        var sb = new StringBuilder(4096);
        sb.Append("<nav epub:type=\"toc\" id=\"toc\" role=\"doc-toc\" class=\"aio-nav\">\n");
        sb.Append("<h1 class=\"aio-nav-title\">").Append(EpubXml.Escape(tocTitle)).Append("</h1>\n");
        AppendList(sb, roots);
        sb.Append("</nav>\n");

        sb.Append("<nav epub:type=\"landmarks\" id=\"landmarks\" hidden=\"hidden\">\n<h2>Landmarks</h2>\n<ol>\n");
        if (targets.CoverHref is not null)
        {
            sb.Append("<li><a epub:type=\"cover\" href=\"").Append(EpubXml.Escape(targets.CoverHref)).Append("\">Cover</a></li>\n");
        }

        if (targets.NavInSpine)
        {
            sb.Append("<li><a epub:type=\"toc\" href=\"").Append(NavHref).Append("#toc\">").Append(EpubXml.Escape(tocTitle)).Append("</a></li>\n");
        }

        sb.Append("<li><a epub:type=\"bodymatter\" href=\"").Append(EpubXml.Escape(targets.FirstChapterHref)).Append("\">Start of content</a></li>\n");
        sb.Append("</ol>\n</nav>");

        return EpubXml.XhtmlDocument(language, tocTitle, "css/style.css", sb.ToString());
    }

    public static string BuildNcx(IReadOnlyList<EpubChapter> chapters, int depth, string identifier, string title, IReadOnlyList<string> authors, string language)
    {
        var roots = BuildTree(chapters, depth);
        var sb = new StringBuilder(4096);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\" xml:lang=\"").Append(EpubXml.Escape(language)).Append("\">\n");
        sb.Append("<head>\n");
        sb.Append("<meta name=\"dtb:uid\" content=\"").Append(EpubXml.Escape(identifier)).Append("\" />\n");
        sb.Append("<meta name=\"dtb:depth\" content=\"").Append(Math.Max(1, MaxDepth(roots)).ToString(CultureInfo.InvariantCulture)).Append("\" />\n");
        sb.Append("<meta name=\"dtb:totalPageCount\" content=\"0\" />\n<meta name=\"dtb:maxPageNumber\" content=\"0\" />\n");
        sb.Append("</head>\n");
        sb.Append("<docTitle><text>").Append(EpubXml.Escape(title)).Append("</text></docTitle>\n");
        foreach (var author in authors)
        {
            sb.Append("<docAuthor><text>").Append(EpubXml.Escape(author)).Append("</text></docAuthor>\n");
        }

        sb.Append("<navMap>\n");
        var order = 0;
        foreach (var node in roots)
        {
            AppendNavPoint(sb, node, ref order);
        }

        sb.Append("</navMap>\n</ncx>\n");
        return sb.ToString();
    }

    /// <summary>Every href the navigation document links to (for tests and sanity checks).</summary>
    internal static IEnumerable<string> Hrefs(IReadOnlyList<EpubChapter> chapters, int depth)
    {
        var stack = new Stack<Node>(BuildTree(chapters, depth));
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node.Href;
            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }
    }

    private static List<Node> BuildTree(IReadOnlyList<EpubChapter> chapters, int depth)
    {
        depth = Math.Clamp(depth, 1, 6);
        var roots = new List<Node>();
        var stack = new List<Node>();
        foreach (var chapter in chapters)
        {
            var headings = chapter.Headings.Where(h => h.Level <= depth && h.Label.Length > 0).ToList();
            var startsWithHeading = headings.Count > 0 && chapter.Headings.Count > 0 && ReferenceEquals(headings[0], chapter.Headings[0]) && StartsWith(chapter, headings[0]);
            if (!startsWithHeading)
            {
                // Content before the first heading (or a chapter with no headings) still gets an entry.
                Add(roots, stack, new Node { Level = headings.Count > 0 ? Math.Max(1, headings.Min(h => h.Level)) : 1, Label = chapter.Title, Href = chapter.Href });
            }

            foreach (var h in headings)
            {
                Add(roots, stack, new Node { Level = h.Level, Label = h.Label, Href = chapter.Href + "#" + h.Id });
            }
        }

        return roots;
    }

    /// <summary>True when the chapter's first element is that heading (so the chapter needs no separate entry).</summary>
    private static bool StartsWith(EpubChapter chapter, EpubHeading heading) =>
        chapter.Body.FirstElementChild is { } first && string.Equals(first.Id, heading.Id, StringComparison.Ordinal);

    private static void Add(List<Node> roots, List<Node> stack, Node node)
    {
        while (stack.Count > 0 && stack[^1].Level >= node.Level)
        {
            stack.RemoveAt(stack.Count - 1);
        }

        if (stack.Count == 0)
        {
            roots.Add(node);
        }
        else
        {
            stack[^1].Children.Add(node);
        }

        stack.Add(node);
    }

    private static void AppendList(StringBuilder sb, List<Node> nodes)
    {
        sb.Append("<ol>\n");
        foreach (var node in nodes)
        {
            sb.Append("<li><a href=\"").Append(EpubXml.Escape(node.Href)).Append("\">").Append(EpubXml.Escape(node.Label)).Append("</a>");
            if (node.Children.Count > 0)
            {
                sb.Append('\n');
                AppendList(sb, node.Children);
            }

            sb.Append("</li>\n");
        }

        sb.Append("</ol>\n");
    }

    private static void AppendNavPoint(StringBuilder sb, Node node, ref int order)
    {
        order++;
        var o = order.ToString(CultureInfo.InvariantCulture);
        sb.Append("<navPoint id=\"np-").Append(o).Append("\" playOrder=\"").Append(o).Append("\">");
        sb.Append("<navLabel><text>").Append(EpubXml.Escape(node.Label)).Append("</text></navLabel>");
        sb.Append("<content src=\"").Append(EpubXml.Escape(node.Href)).Append("\" />\n");
        foreach (var child in node.Children)
        {
            AppendNavPoint(sb, child, ref order);
        }

        sb.Append("</navPoint>\n");
    }

    private static int MaxDepth(List<Node> nodes) => nodes.Count == 0 ? 0 : 1 + nodes.Max(n => MaxDepth(n.Children));
}
