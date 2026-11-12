using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.UI.Services;

/// <summary>One entry of the Reader outline (table of contents).</summary>
public sealed class ReaderOutlineNode
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public required int Level { get; init; }
    public List<ReaderOutlineNode> Children { get; } = [];
}

public static class ReaderOutlineBuilder
{
    /// <summary>
    /// Builds a nested outline from <see cref="Document.Headings"/>. Call after the HTML render, which assigns
    /// the heading ids (<see cref="HeadingBlock.Id"/>) the outline links to. Skipped levels nest under the
    /// nearest shallower heading.
    /// </summary>
    public static IReadOnlyList<ReaderOutlineNode> Build(Document document)
    {
        var roots = new List<ReaderOutlineNode>();
        var stack = new Stack<ReaderOutlineNode>();
        foreach (var heading in document.Headings)
        {
            if (string.IsNullOrWhiteSpace(heading.Id))
            {
                continue;
            }

            var text = heading.ToPlainText().Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var node = new ReaderOutlineNode { Id = heading.Id, Text = text, Level = Math.Clamp(heading.Level, 1, 6) };
            while (stack.Count > 0 && stack.Peek().Level >= node.Level)
            {
                stack.Pop();
            }

            if (stack.Count == 0)
            {
                roots.Add(node);
            }
            else
            {
                stack.Peek().Children.Add(node);
            }

            stack.Push(node);
        }

        return roots;
    }

    /// <summary>Ids of <paramref name="activeId"/> and all its ancestors, for highlighting the open path.</summary>
    public static HashSet<string> PathTo(IReadOnlyList<ReaderOutlineNode> roots, string? activeId)
    {
        var path = new HashSet<string>(StringComparer.Ordinal);
        if (activeId is null)
        {
            return path;
        }

        bool Visit(ReaderOutlineNode node)
        {
            if (node.Id == activeId || node.Children.Any(Visit))
            {
                path.Add(node.Id);
                return true;
            }

            return false;
        }

        foreach (var root in roots)
        {
            if (Visit(root))
            {
                break;
            }
        }

        return path;
    }
}
