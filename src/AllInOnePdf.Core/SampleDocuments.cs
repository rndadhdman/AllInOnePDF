using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers;

namespace AllInOnePdf.Core;

/// <summary>Built-in sample content for previews, the theme builder and empty states.</summary>
public static class SampleDocuments
{
    public const string WelcomeMarkdown = """
        ---
        title: Designing a Theme
        subtitle: A short sample document for previewing PDF themes
        author: AllInOne PDF
        ---

        # Designing a Theme

        A theme decides how your words land on the page: the **typeface**, the *rhythm* of line spacing, how much air surrounds a heading, and the colour of a link. This sample exercises every element a theme can style, so you can see changes the moment you make them.

        ## Typography

        Good body text disappears. Readers should notice the ideas, not the letterforms. The default theme uses Inter at 11 points with a line height of 1.5, which reads comfortably on both screen and paper. Switch to Literata for a bookish feel, or tighten the leading for dense technical material.

        > The details are not the details. They make the design.
        > — Charles Eames

        ### Lists and tasks

        - Headings in six levels, each with its own size, weight and spacing
        - Paragraph spacing, first-line indents and justified text
        - Inline styles: **bold**, *italic*, ~~strikethrough~~, `code`, and [links](https://example.com)
        - Nested lists
          - with sub-items
          - and more

        1. Ordered lists
        2. Keep their numbers
        3. Across page breaks

        - [x] Build the document model
        - [x] Render it to PDF
        - [ ] Make it beautiful

        ## Code

        Code blocks use a monospace face with a soft background. Turn on line numbers for tutorials.

        ```csharp
        public static IEnumerable<int> Fibonacci()
        {
            var (a, b) = (0, 1);
            while (true)
            {
                yield return a;
                (a, b) = (b, a + b);
            }
        }
        ```

        ## Tables

        | Element      | Themeable properties              | Default   |
        |:-------------|:----------------------------------|----------:|
        | Body text    | Font, size, line height, colour   | Inter 11  |
        | Headings     | Six levels, numbering, rules      | SemiBold  |
        | Code         | Font, background, line numbers    | JetBrains |
        | Tables       | Grid style, header, stripes       | Lines     |

        ## Callouts

        > [!TIP]
        > Duplicate a built-in theme, then change one thing at a time. Watch the preview update live.

        > [!WARNING]
        > System fonts are not portable. Stick to bundled fonts for identical output on Windows and Linux.

        ---

        That is every block type. Open the theme builder, pick a preset, and start adjusting.
        """;

    public static async Task<Document> CreateWelcomeAsync(CancellationToken cancellationToken = default)
    {
        var importer = new MarkdownImporter();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(WelcomeMarkdown));
        var document = await importer.ImportAsync(new ImportContext
        {
            Input = stream,
            FileName = "welcome.md",
            Options = OptionValues.FromDefaults(importer.Options),
        }, cancellationToken);
        return document;
    }
}
