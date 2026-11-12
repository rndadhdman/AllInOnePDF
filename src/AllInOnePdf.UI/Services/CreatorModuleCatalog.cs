using AllInOnePdf.Core.Documents;
using AllInOnePdf.Publishing.Model;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace AllInOnePdf.UI.Services;

/// <summary>One kind of module the Creator can insert.</summary>
/// <param name="Type">Stable type key, the same as the JSON discriminator (<c>text</c>, <c>heading</c> …).</param>
/// <param name="Name">Display name in sentence case ("Text", "Page break").</param>
/// <param name="Description">One line for the Insert panel tooltip.</param>
/// <param name="Icon">Icon for tiles, the structure tree and the inspector.</param>
/// <param name="Create">Builds a new module with friendly starter content.</param>
public sealed record CreatorModuleKind(string Type, string Name, string Description, Icon Icon, Func<Module> Create);

/// <summary>The module types offered by the Insert panel, with names and icons used across the Creator.</summary>
public static class CreatorModuleCatalog
{
    /// <summary>Every module kind in Insert-panel order.</summary>
    public static IReadOnlyList<CreatorModuleKind> All { get; } =
    [
        new("text", "Text", "Paragraphs, lists and links written in Markdown", new Icons.Regular.Size20.TextT(),
            () => new TextModule { Markdown = "Write something here. Use **bold**, *italic*, lists and [links](https://example.com)." }),
        new("heading", "Heading", "A title or section heading, level 1 to 6", new Icons.Regular.Size20.TextHeader1(),
            () => new HeadingModule { Text = "New heading", Level = 2 }),
        new("image", "Image", "A photo or illustration from your computer", new Icons.Regular.Size20.Image(),
            () => new ImageModule { Alt = "" }),
        new("button", "Button", "A button that opens a link", new Icons.Regular.Size20.CursorClick(),
            () => new ButtonModule()),
        new("quote", "Quote", "A pull quote with an attribution", new Icons.Regular.Size20.TextQuote(),
            () => new QuoteModule { Markdown = "A memorable line worth repeating.", Attribution = "Someone notable" }),
        new("callout", "Callout", "A note, tip or warning box", new Icons.Regular.Size20.Info(),
            () => new CalloutModule { Kind = CalloutKind.Note, Markdown = "Something the reader should not miss." }),
        new("table", "Table", "Rows and columns of text", new Icons.Regular.Size20.Table(),
            () => new TableModule
            {
                HeaderRow = true,
                Cells = [["Item", "Amount"], ["First", "10"], ["Second", "20"]],
                Alignments = [ColumnAlignment.Left, ColumnAlignment.Right],
            }),
        new("code", "Code", "Source code with syntax highlighting", new Icons.Regular.Size20.Code(),
            () => new CodeModule { Code = "Console.WriteLine(\"Hello\");", Language = "csharp" }),
        new("divider", "Divider", "A horizontal line", new Icons.Regular.Size20.LineHorizontal1(),
            () => new DividerModule()),
        new("spacer", "Spacer", "Empty vertical space", new Icons.Regular.Size20.ArrowAutofitHeight(),
            () => new SpacerModule()),
        new("contents", "Contents", "A table of contents built from the headings", new Icons.Regular.Size20.TextBulletListLtr(),
            () => new ContentsModule()),
        new("pagebreak", "Page break", "Start the following content on a new page", new Icons.Regular.Size20.DocumentPageBreak(),
            () => new PageBreakModule()),
    ];

    /// <summary>The kind with that type key, or null.</summary>
    public static CreatorModuleKind? Find(string? type) =>
        type is null ? null : All.FirstOrDefault(k => string.Equals(k.Type, type, StringComparison.OrdinalIgnoreCase));

    /// <summary>The type key of a module instance.</summary>
    public static string TypeOf(Module module) => module switch
    {
        TextModule => "text",
        HeadingModule => "heading",
        ImageModule => "image",
        DividerModule => "divider",
        SpacerModule => "spacer",
        QuoteModule => "quote",
        CalloutModule => "callout",
        CodeModule => "code",
        TableModule => "table",
        ButtonModule => "button",
        ContentsModule => "contents",
        PageBreakModule => "pagebreak",
        _ => "module",
    };

    /// <summary>Display name of a module instance ("Heading").</summary>
    public static string NameOf(Module module) => Find(TypeOf(module))?.Name ?? "Module";

    /// <summary>Icon of a module instance.</summary>
    public static Icon IconOf(Module module) => Find(TypeOf(module))?.Icon ?? new Icons.Regular.Size20.Square();

    /// <summary>A short preview of the module's content for the structure tree ("Spring newsletter").</summary>
    public static string Summary(Module module)
    {
        if (!string.IsNullOrWhiteSpace(module.Label))
        {
            return module.Label.Trim();
        }

        var text = module switch
        {
            TextModule t => t.Markdown,
            HeadingModule h => h.Text,
            ImageModule i => string.IsNullOrWhiteSpace(i.Caption) ? i.Alt : i.Caption,
            QuoteModule q => q.Markdown,
            CalloutModule c => string.IsNullOrWhiteSpace(c.Title) ? c.Markdown : c.Title,
            CodeModule c => c.Code,
            TableModule t => t.Cells.Count > 0 ? string.Join(" · ", t.Cells[0]) : null,
            ButtonModule b => b.Text,
            ContentsModule c => c.Title,
            SpacerModule s => s.HeightPt.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " pt",
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            return NameOf(module);
        }

        var line = text.ReplaceLineEndings(" ").Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("#", string.Empty, StringComparison.Ordinal).Replace("*", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal).Trim();
        return line.Length > 48 ? line[..47].TrimEnd() + "…" : line;
    }
}
