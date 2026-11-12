# The layout engine (`AllInOnePdf.Layout`)

AllInOne PDF lays out every PDF with its own engine: a fluent document model (columns, rows, tables, text, images)
paginated by a measure-and-draw protocol and written as PDF through SkiaSharp, with HarfBuzz text shaping. It
replaced QuestPDF in November 2026 so the app has no dependency outside OSI licences. This document is the
specification the engine is built and measured against.

The API deliberately mirrors the QuestPDF subset the renderers used (`Core/Rendering/ThemedComposer.cs`,
`Publishing/Rendering/PublicationLayout.cs`, about 2,800 lines and 95 API members): those files switch engines by
changing their `using` lines. Behaviour is specified as "what QuestPDF did", because the acceptance test is pixel
agreement with QuestPDF's output on a corpus of 87 documents and 398 pages (section 9).

## 1. Shape of the library

| Namespace | Contents | Owner |
|-----------|----------|-------|
| `AllInOnePdf.Layout.Infrastructure` | The contract (written by the director, do not change without asking): `Size`, `Position`, `Unit`, `SpacePlan`, `Color`, `TextStyle`, `FontWeight`, `FontPosition`, `Element`, `ContainerElement`, `IContainer`, `ContainerChaining`, `PageContext`, `CapturedPosition`, `IDrawingCanvas`, `FreeCanvas`, `GlyphRun`, `DocumentMetadata`, `DocumentSettings`, `ImageCompressionQuality`, `ContentDirection`, `DocumentLayoutException`, `DocumentComposeException`. | director |
| `AllInOnePdf.Layout.Fluent` | The public fluent API: `Document`, `IDocumentContainer`, `PageDescriptor`, `ColumnDescriptor`, `RowDescriptor`, `TableDescriptor` family, `TextDescriptor`, `TextSpanDescriptor`, `ImageDescriptor`, `SvgImageDescriptor`, `ILineDescriptor`, `LayersDescriptor`, `IDynamicComponent` family, and `public static partial class ContainerExtensions` split into one partial file per owner. | all (see 8) |
| `AllInOnePdf.Layout.Elements` | Internal element classes behind the API. | per element |
| `AllInOnePdf.Layout.Text` | Fonts (`FontManager`, `FontRegistry`), shaping (`TextShaper`), line breaking and the `TextBlock` element. | text agent |
| `AllInOnePdf.Layout.Drawing` | `SkiaCanvas : IDrawingCanvas`, `PdfDocumentWriter` (SKDocument), `PageLayout` (the page loop). | core agent |
| `AllInOnePdf.Layout.Svg` | A small SVG renderer onto `SKCanvas`. | media agent |

Packages: `SkiaSharp` (+ Linux NoDependencies natives, already shipped by PDFtoImage), `HarfBuzzSharp` (+ Linux
natives). Nothing else. Everything the engine needs from Skia goes through `IDrawingCanvas`, except SVG, which draws
on `IDrawingCanvas.Native`.

## 2. Layout protocol

`Element.Measure(Size available)` answers with a `SpacePlan`:

* `Empty`: nothing (left) to draw.
* `Wrap`: does not fit at all here; the parent should move to the next page and ask again.
* `PartialRender(size)`: this much fits now; the element remembers what it drew and continues next time.
* `FullRender(size)`: everything (left) fits in `size`.

`Element.Draw(Size available)` draws at the canvas origin with the same `available` the parent measured with, and
advances the element's pagination state. Parents translate the canvas (`Canvas.Translate`) before drawing a child
and translate back afterwards. Measuring must be free of side effects and repeatable: a parent may measure a child
several times before drawing it. `Reset()` returns the whole tree to "nothing drawn yet" between passes.

Sizes never exceed the offered space except where the spec says a child may overflow (`Unconstrained` is not
implemented). Infinity is `Size.Infinity` (14400 pt), never `float.PositiveInfinity`.

## 3. Document and pages (core agent)

```csharp
public static class Document { public static IDocument Create(Action<IDocumentContainer> handler); }
public interface IDocument { IDocument WithMetadata(DocumentMetadata m); IDocument WithSettings(DocumentSettings s); byte[] GeneratePdf(); void GeneratePdf(Stream stream); }
public interface IDocumentContainer { void Page(Action<PageDescriptor> handler); }
public sealed class PageDescriptor
{
    void Size(float width, float height, Unit unit = Unit.Point);
    void MarginTop/MarginBottom/MarginLeft/MarginRight(float value, Unit unit = Unit.Point);  // also Margin(float), MarginVertical, MarginHorizontal
    void PageColor(Color color);
    void DefaultTextStyle(Func<TextStyle, TextStyle> style);   // applied under the root style for header, content and footer
    IContainer Header(); IContainer Content(); IContainer Footer();
}
```

The page loop (`Drawing/PageLayout`): for every page description in order, pages are produced until the content
answers `FullRender` or `Empty`:

1. `PageContext.BeginPage()`. Available width = page width − left − right margins; height likewise.
2. Measure the header with the full content size; its height `h` is reserved. Same for the footer (`f`).
3. Measure the content with `height − h − f`. `Wrap` on an otherwise empty page, or a `PartialRender` that draws
   nothing (no progress, detected by the same plan on two consecutive pages with no state change), throws
   `DocumentLayoutException` with the page number.
4. Draw: fill the page with `PageColor`, draw header at the top, content below it, footer at the bottom
   (`y = pageHeight − bottomMargin − f`). Header and footer are measured and drawn once per page and may not
   paginate; their content containers are re-drawn on every page (dynamic text such as page numbers changes).
5. Repeat while the content plan was `PartialRender`.

Two passes: pass 1 with `FreeCanvas` fills `PageContext` (page count, section pages, captures); `Reset()`; pass 2
with `SkiaCanvas` draws. Text that shows `TotalPages` or a section's page number uses the pass-1 values in pass 2 and
shows `?` in pass 1 (QuestPDF behaviour; widths may differ by a digit, accepted). `GeneratePdf` wraps exceptions
from compose lambdas in `DocumentComposeException` and never leaves a half-written stream.

`DocumentSettings.ImageCompressionQuality` maps to `SKDocumentPdfMetadata.EncodingQuality` (Best 100, VeryHigh 95,
High 90, Medium 80, Low 65, VeryLow 40); `ImageRasterDpi` to `RasterDpi`. Metadata maps 1:1; `Language` is not
supported by Skia's PDF metadata and is ignored.

## 4. Containers and layout elements (core agent)

All on `IContainer`, each returning the new inner `IContainer` unless noted. Names and defaults are QuestPDF's.

| Member | Behaviour |
|--------|-----------|
| `Column(Action<ColumnDescriptor>)` | Vertical stack. `ColumnDescriptor.Item()` adds a child container; `Spacing(float)` puts that gap between items (not before the first or after the last; a gap is not added after an item that ended a page). Paginates item by item: an item answering `Wrap` moves to the next page; an item answering `PartialRender` is drawn and the column answers `PartialRender`; an item that draws nothing on an otherwise empty page is the `DocumentLayoutException` case. Width = available width; height = sum of drawn items and gaps. |
| `Row(Action<RowDescriptor>)` | Horizontal layout. `RelativeItem(float weight = 1)` shares the width left after constant items in proportion to the weights; `ConstantItem(float width)` takes exactly that width; `AutoItem()` takes its natural width (measure with infinite width). `Spacing(float)` between items. All items get the same height: the row measures every item with the available size, the row's height is the tallest `FullRender`/`PartialRender` answer; items are drawn with `(itemWidth, rowHeight)`. Pagination: if any item is `PartialRender`, the row is `PartialRender` at the height of the tallest plan, every item draws what fits in that height, and the row continues next page with the items that were partial (QuestPDF does this; cells align at the top). If any item answers `Wrap`, the row answers `Wrap`. |
| `Padding(float)`, `PaddingTop/Bottom/Left/Right(float)`, `PaddingVertical/Horizontal(float)` | Insets; negative values allowed (the child is offered more space and drawn shifted; the renderers use `PaddingTop(−x)` to cancel heading space). |
| `Background(Color)` | Fills the element's drawn size, under the child. |
| `Border(float)`, `BorderTop/Bottom/Left/Right(float)`, `BorderColor(Color)` | Borders are drawn *over* the child along the edges, centred on the edge (QuestPDF draws `Border(w)` as a rectangle stroke of width w centred on the content rectangle, so half of it lies inside). Default colour black. The content size is not reduced. |
| `CornerRadius(float)` | Clips the subtree (background, borders, child) to a rounded rectangle. |
| `AlignLeft/AlignCenter/AlignRight()`, `AlignTop/AlignMiddle/AlignBottom()` | Offers the child its natural size (measure with the available size, draw with the plan size) and positions it. Horizontal alignment makes the element shrink-wrap horizontally: the child is measured with the available width but drawn at its plan width. Default (no alignment) stretches. |
| `Width(float)`, `MinWidth`, `MaxWidth`, `Height(float)`, `MinHeight`, `MaxHeight(float)` | Constrain the offered space. `Width(w)` wider than available → `Wrap`. `Height(h)` taller than the available height → `Wrap` (QuestPDF: a fixed-height box that does not fit moves to the next page; on an empty page it throws). |
| `AspectRatio(float ratio, AspectRatioOption option = FitWidth)` | Height = width / ratio (FitWidth) or width = height × ratio (FitHeight), or whichever fits (FitArea). |
| `Layers(Action<LayersDescriptor>)` | `Layer()` adds a background/overlay drawn in the primary layer's size; `PrimaryLayer()` decides the size and pagination. Layers are drawn in order of declaration (so a `Layer()` declared first is behind the primary). |
| `LineHorizontal(float thickness)` → `ILineDescriptor` | A horizontal rule of the available width; `LineColor(Color)`, `LineDashPattern(float[] onOff)`. Height = thickness. `LineVertical` likewise. (media agent) |
| `PageBreak()` | Answers `Wrap` once (forcing the next page), then `Empty`. Consecutive page breaks produce one break each; a page break as the very first element of the content does not produce an empty page. |
| `EnsureSpace(float minHeight)` | If less than `minHeight` is available and the child would be `PartialRender`, answer `Wrap` instead (so the child starts on the next page). When the child answers `FullRender` it passes through. |
| `PreventPageBreak()` | The child is only drawn when it fits whole (`FullRender`); otherwise `Wrap`. On an empty page a child that never fits throws `DocumentLayoutException`. (`ShowEntire()` is an alias.) |
| `SkipOnce()` | The child is skipped the first time it would be drawn (used for headers/footers not shown on the first page: measured as `Empty` on page 1, normal afterwards; state resets between passes). |
| `Section(string name)` | Records the page on which the subtree first draws in `PageContext` (`SetSectionPage`) and declares a named destination at its top-left (`DrawSectionDestination`). Links point here. |
| `CaptureContentPosition(string id)` | Records `(page, x, y)` of the subtree's top-left on every page it draws on (`RegisterCapture`). |
| `Hyperlink(string url)` | The subtree's drawn rectangle becomes a URL link. |
| `TranslateX(float, Unit = Point)`, `TranslateY(float, Unit = Point)` | Shift where the child paints (positive x right, positive y down) without changing its measured size or the position of anything else (QuestPDF's `Translate`). Nothing is clipped: the child may paint outside its slot and over its neighbours. Consecutive translates merge into one element and add up. |
| `ZIndex(int z)` | The child keeps its slot and measures exactly as before. With `z > 0` its drawing on a page is deferred and painted after everything that page draws at z 0 (content, header, footer), in ascending z, ties in document order; `z <= 0` draws in place. The deferred draw replays the canvas state captured where the element would have drawn: the accumulated translation and every active clip (`CornerRadius`, `Freeform`), which the page canvases track for this (`Drawing/CanvasState.cs`). A z-index element met while a layer is being painted defers only when its z is higher than that layer. Both passes defer the same way, so pagination, sections, captures and tracking are unaffected. Known limitation: content at z > 0 that is split across pages is deferred per page (each page's part paints over that page's normal content). Outside the page loop (element tests drawing directly) it draws in place. |
| `Freeform(Action<FreeformDescriptor>)` | A fixed canvas with absolutely placed children. It answers `FullRender` of the whole available size (`Wrap` when the width or height is 0), so callers size it with `Width`/`Height`; it does not paginate and clips everything to its bounds. `FreeformDescriptor.Item(x, y, width, height, z = 0, key = null)` returns the container of a child placed with its top-left at (x, y) points from the canvas's top-left, measured and drawn at `Size(width, height)` and clipped to that box; items paint in ascending z, ties in insertion order (local to the canvas, independent of `ZIndex`); they may extend past the canvas (clipped). An item whose content has no plan (`Empty`) still draws, so a background-only box paints. Content that does not fit its box (`PartialRender` or `Wrap` at that size) is drawn as far as it fits (nothing for `Wrap`) and, when `key` is given, the key is reported in `DocumentLayoutInfo.Overflowing`. `TrackPages` works inside items. |
| `Element(Action<IContainer>)`, `Element(Func<IContainer, IContainer>)` | Runs the lambda on the current container (a convenience for conditional chains). |
| `Dynamic(IDynamicComponent)` | Content computed per page (section headers). See 7. |
| `Image(byte[])` → `ImageDescriptor`, `Svg(string)` → `SvgImageDescriptor` | See 6 (media agent). |
| `Table(Action<TableDescriptor>)` | See 6 (media agent). |
| `Text(string)` → `TextSpanDescriptor`, `Text(Action<TextDescriptor>)` | See 5 (text agent). |
| `DefaultTextStyle(Func<TextStyle, TextStyle>)` | Pushes a text style for the subtree: every text block resolves its styles against the nearest enclosing default (which itself was applied to its parent, up to the page's default and `TextStyle.Default`). |

Chain rule: each extension creates a new `ContainerElement`, hangs it under the current one with
`ContainerChaining.Element`, and returns it. Leaf methods (`Text`, `Image`, `Svg`, `LineHorizontal`, `Table`,
`PageBreak`) set the child and return their descriptor or nothing.

## 5. Text (text agent)

```csharp
IContainer.Text(Action<TextDescriptor>)      // a text block (paragraph)
IContainer.Text(string) → TextSpanDescriptor  // one-span shortcut
sealed class TextDescriptor
{
    void DefaultTextStyle(Func<TextStyle, TextStyle>);    // base style for the block's spans
    void AlignLeft(); void AlignCenter(); void AlignRight(); void Justify();   // Justify: every line but the last and lines ending in a hard break
    void ParagraphFirstLineIndentation(float points);
    void ClampLines(int maxLines, string ellipsis = "…");  // lines beyond the limit are dropped; the last kept line gets the ellipsis if text was cut
    TextSpanDescriptor Span(string text);                 // "\n" inside a span is a hard line break
    TextSpanDescriptor Hyperlink(string text, string url);
    TextSpanDescriptor SectionLink(string text, string sectionName);
    TextSpanDescriptor BeginPageNumberOfSection(string sectionName);   // digits of the page the section starts on
    TextSpanDescriptor CurrentPageNumber(); TextSpanDescriptor TotalPages();
    IContainer Element();                                  // an inline element (small image) laid out on the baseline
}
sealed class TextSpanDescriptor   // mutates the span's style; every method returns this
{
    FontFamily(params string[]); FontSize(float); LineHeight(float); LetterSpacing(float em); FontColor(Color); BackgroundColor(Color);
    Weight(FontWeight); Bold(); SemiBold(); Medium(); NormalWeight(); Italic(bool = true); Underline(bool = true); Strikethrough(bool = true);
    Superscript(); Subscript(); DisableFontFeature(string); BreakAnywhere();
}
```

Rules, all matching QuestPDF:

* **Fonts.** `FontManager.RegisterFontFromStream(Stream)` (static, QuestPDF's name; `BundledFonts` calls it) and
  `RegisterFont(byte[])` load TTF/OTF faces and index them by family name, weight and slant read from the font.
  Family lookup is case-insensitive. A family that is not registered is looked up in the system through
  `SKFontManager.Default`; a family that does not exist anywhere falls through to the next name in
  `FontFamilies`, then to the first registered family. The closest weight is chosen (exact, else nearest; bold
  from a regular face is synthesised with `SKFont.Embolden`, italic with `SkewX`, only when the family has no such
  face). Glyphs missing from the chosen face are taken from the next family in the list, then from
  `SKFontManager.MatchCharacter`; still-missing glyphs draw the font's .notdef (never throw).
* **Shaping.** Each span is shaped with HarfBuzz (`HarfBuzzSharp.Font` over the face's blob, `Buffer` with
  `Direction.LeftToRight`, script and language guessed from the text), with the span's disabled features turned off
  (`calt`, `liga`). Letter spacing adds `em × size` after every glyph (not after the last glyph of a line).
  The result is a list of glyph clusters with advances, mapped back to UTF-16 ranges so line breaks can be found in
  the text.
* **Line breaking.** Break opportunities: after spaces (the space stays at the end of the line and has no width for
  alignment), after `-` and `–`, at soft hyphens (U+00AD, which is invisible except where the break happens, where
  the preceding glyph run gets a `-` glyph), before/after CJK ideographs, and anywhere when `BreakAnywhere` is set.
  A word longer than the line breaks anywhere as a last resort. Tabs are 4 spaces. A hard break (`\n`) ends the line
  and, when `Justify` is on, that line is not stretched.
* **Lines and metrics.** A line's height is `max(size × lineHeight)` over the spans on it, where the font size of
  super/subscript spans is 0.65 × their style size. The baseline sits at `lineTop + (lineHeight − (ascent + descent)) / 2 + ascent`,
  with `ascent`/`descent` from the largest span's font metrics (positive numbers, Skia's `-Ascent`). Superscript
  raises the baseline by 0.35 × size, subscript lowers it by 0.15 × size. The block's height is the sum of its
  line heights; its width is the widest line (the block still fills the available width for alignment).
* **Decorations.** Background colour: a rectangle from the line top to the line bottom under the run. Underline:
  a line of thickness `size / 14` at `baseline + descent × 0.35` (adjust to match QuestPDF by comparing the
  `stress-lists` pages). Strikethrough: thickness `size / 14` at `baseline − ascent × 0.3`.
* **Links.** `Hyperlink` spans mark their run rectangles (one per line fragment) with `DrawHyperlink`; `SectionLink`
  with `DrawSectionLink`.
* **Page numbers.** `CurrentPageNumber` draws `PageContext.CurrentPage`; `TotalPages` draws `PageCount` (`?` in
  pass 1); `BeginPageNumberOfSection` draws `GetSectionPage(name)` or `?`. These are re-shaped per page.
* **Pagination.** A block that does not fit draws as many whole lines as fit (`PartialRender`), or `Wrap` when not
  even the first line fits. Measuring with the same available size must give the same lines as drawing.
* **Inline elements.** `Element()` returns a container whose content is measured with (available width, infinite
  height) and placed on the baseline (bottom of the element on the baseline, raising the line height as needed).
* **Empty blocks.** A block without spans measures `Empty`. A block whose only text is spaces still produces one
  line (the renderers use `Span(" ")` for empty code lines).

`Text/TextBlock` is the element; `Text/FontManager` the registry; `Text/TextShaper` wraps HarfBuzz; `Text/LineBreaker`
splits shaped runs into lines. Keep HarfBuzz objects cached per face (they are expensive) and thread-safe (renders run
in parallel on the thread pool).

## 6. Tables, images, SVG, lines (media agent)

```csharp
IContainer.Table(Action<TableDescriptor>)
sealed class TableDescriptor { void ColumnsDefinition(Action<TableColumnsDefinitionDescriptor>); void Header(Action<TableCellDescriptor>); ITableCellContainer Cell(); void ExtendLastCellsToTableBottom(); }
sealed class TableColumnsDefinitionDescriptor { void RelativeColumn(float weight = 1); void ConstantColumn(float width); }
sealed class TableCellDescriptor { ITableCellContainer Cell(); }   // header rows: repeated on every page the table continues on
interface ITableCellContainer : IContainer { ITableCellContainer ColumnSpan(uint); ITableCellContainer RowSpan(uint); ITableCellContainer Row(uint); ITableCellContainer Column(uint); }
```

Table semantics: cells are placed left to right, top to bottom, into the first free slot (QuestPDF's auto
placement; explicit `Row`/`Column` override it). Column widths: constant columns first, the rest shared by weight.
Row height = tallest cell in the row (cells are measured with their column span's width and the height left on the
page). A row that does not fit on the page splits the way QuestPDF splits it: every cell draws what fits, the row
continues on the next page with all of its cells laid out again (finished content draws only its box). Rows joined
by a row span move together unless they start the page; a single row taller than a page is drawn clipped. Header
cells are drawn again at the top of every continuation page. Row-spanning cells stretch over their rows (the cell
background and borders cover the span). Single-side borders are filled bands centred on the edge; rounded borders
lie inside the box.

```csharp
IContainer.Image(byte[] data) → ImageDescriptor { FitWidth(); FitHeight(); FitArea(); FitUnproportionally(); UseOriginalImage(); WithCompressionQuality(ImageCompressionQuality); }
IContainer.Svg(string svg) → SvgImageDescriptor { FitWidth(); FitHeight(); FitArea(); }
```

Images: decoded with `SKImage.FromEncodedData`; unsupported data throws `DocumentComposeException` from the
`Image()` call itself (the renderers catch and fall back). Default scaling is `FitWidth` (the image takes the
available width at its aspect ratio; if the resulting height exceeds the available height the plan is `Wrap`).
`FitArea` fits inside both dimensions, centred... no: QuestPDF places it at the top-left of the box; keep that.
`FitHeight` fits the available height. Images never paginate. The PDF embeds JPEG data as-is and re-encodes other
formats as JPEG at `ImageCompressionQuality` unless the image has transparency (then lossless).

SVG: `Svg/SvgRenderer` draws an SVG string onto `IDrawingCanvas.Native` scaled into the box. Supported: `svg`
(`viewBox`, `width`, `height`), `g`, `path` (`d` via `SKPath.ParseSvgPathData`), `rect` (with `rx`/`ry`), `circle`,
`ellipse`, `line`, `polyline`, `polygon`, attributes `fill`, `stroke`, `stroke-width`, `stroke-linecap`,
`stroke-linejoin`, `opacity`, `fill-opacity`, `stroke-opacity`, `transform` (`translate`, `scale`, `rotate`,
`matrix`), colours as hex, `rgb()`, `none` and the CSS named colours. Everything else is ignored silently; an SVG
that fails to parse draws nothing. The renderers' callout icons and task-list ticks use only this subset.

Lines: `LineHorizontal(thickness)` / `LineVertical(thickness)` with `LineColor` and `LineDashPattern`.

## 7. Dynamic components (core agent)

**Page tracking (added for the Creator):** `container.TrackPages(key)` wraps content in a `PageTrackerElement` that
records, on every draw, the current page under `key` (`PageContext.TrackPage`: first and last page). `IDocument.GeneratePdf(out
DocumentLayoutInfo info)` returns the page count and the tracked spans; `ColumnDescriptor.ItemDecorator` lets a
composer wrap every item it adds for one block (`ThemedComposer.ComposeBlocks` takes a `pageKey` function), so a
flow of blocks can be tracked per module without nesting columns.

**Tracked boxes and overflow (added for the Creator's Position and Free flow):** in the drawing pass the tracker also
records `TrackedBox(int Page, float X, float Y, float Width, float Height)` on every draw (`PageContext.TrackBox`),
reported as `DocumentLayoutInfo.TrackedBoxes : IReadOnlyDictionary<string, IReadOnlyList<TrackedBox>>`, one entry per
draw in draw order (one per page for split content). Coordinates are points from the page's top-left corner, so page
margins, header height and every enclosing padding are included; the size is the space the tracked element was drawn
into (a column item: the column width × the item's height on that page; the page content itself: the whole content
area). Translated, deferred (`ZIndex`) and `Freeform`-placed content reports where it really painted.
`DocumentLayoutInfo.Overflowing : IReadOnlySet<string>` lists the keys of `Freeform` items whose content did not fit
(`PageContext.MarkOverflowing`). Both are reset in `BeginFirstPass`; `TrackedPages` is unchanged.

```csharp
public interface IDynamicComponent { DynamicComponentComposeResult Compose(DynamicContext context); }
public sealed class DynamicComponentComposeResult { public IDynamicElement Content { get; set; } public bool HasMoreContent { get; set; } }
public sealed class DynamicContext
{
    public int PageNumber { get; }   public int TotalPages { get; }   public Size AvailableSize { get; }
    public IDynamicElement CreateElement(Action<IContainer> content);
    public IEnumerable<CapturedPosition> GetContentCapturedPositions(string id);   // from PageContext
}
```

`Dynamic(component)` calls `Compose` every time the element is measured or drawn on a page (with that page's
number and the available size), lays out the returned content, and is `FullRender` unless `HasMoreContent`
(then `PartialRender`). The renderers use it only for running headers that look up captured heading positions.

## 8. File ownership and build rules

| Agent | Owns | Must not touch |
|-------|------|----------------|
| core | `Fluent/Document.cs`, `Fluent/PageDescriptor.cs`, `Fluent/ColumnDescriptor.cs`, `Fluent/RowDescriptor.cs`, `Fluent/LayersDescriptor.cs`, `Fluent/DynamicComponent.cs`, `Fluent/ContainerExtensions.Layout.cs` (everything in section 4 except Table/Image/Svg/Line/Text), `Elements/*` for those, `Drawing/*`, `tests/AllInOnePdf.Layout.Tests/Layout*Tests.cs` | `Text/*`, `Svg/*`, the media and text fluent files |
| text | `Text/*`, `Fluent/TextDescriptor.cs`, `Fluent/TextSpanDescriptor.cs`, `Fluent/ContainerExtensions.Text.cs`, `tests/.../Text*Tests.cs` | everything else |
| media | `Elements/Table*.cs`, `Elements/Image*.cs`, `Elements/Line*.cs`, `Svg/*`, `Fluent/TableDescriptor.cs`, `Fluent/ImageDescriptor.cs`, `Fluent/ContainerExtensions.Media.cs`, `tests/.../Media*Tests.cs` | everything else |

`Infrastructure/*`, the csproj and `Directory.Packages.props` belong to the director. The text and media agents
need a way to run their elements before the core agent's page loop exists: each writes a small test harness that
calls `Initialize(new PageContext(), new FreeCanvas())`, `Measure` and `Draw` directly, and (for drawing tests)
renders onto an `SKCanvas` through their own minimal `IDrawingCanvas` in the test project. The text agent's
`TextBlock` must not depend on anything in `Elements/`; the media agent's `Table` may contain any `Element`.

Rules: C# 13, nullable on, file-scoped namespaces, XML docs on public members, no new packages, build warning-clean
(AnalysisLevel latest-recommended), tests in `tests/AllInOnePdf.Layout.Tests`. Elements are not thread-safe but
two documents must be able to render on two threads at once (no static mutable state except font caches behind
locks).

## 9. Acceptance: the corpus

`tools/AllInOnePdf.LayoutCompare` renders the corpus (every built-in theme × two samples, four stress documents ×
six themes, every Creator template) to 72 dpi page images and compares them with a reference set rendered by
QuestPDF before the switch (`layoutcompare compare <refDir> <outDir>` writes per-page diffs and `summary.txt`).
Targets for the switch:

1. Every document has the same page count as the reference.
2. No page has more than 3 % differing pixels; the corpus mean is under 0.5 %.
3. PDF text extracts the same: the PDF import round-trip tests in `AllInOnePdf.Core.Tests` pass unchanged.
4. Every existing test in the solution passes.

Known, accepted differences: anti-aliasing noise (the comparison already tolerates 40/255 per channel), JPEG
re-encoding of images, and the `?` placeholder widths in pass 1 that never show.

**Result (6 November 2026, `main` at `4291860`)**: 398 of 398 page counts match; corpus mean 0.252 %; two pages
over 3 % (`stress-code-academic` p006–p007, 4.5–4.7 %), where the glyph positions match to 0.00002 pt and only the
rasteriser's anti-aliasing differs (our text origin carries a constant float error, 219.99994 pt; QuestPDF's drifts
page to page and is exactly 220 there); text extraction identical on all 87 PDFs; every test green (Layout 188,
Publishing 57, Core 843). The PDFs embed subset fonts (`Drawing/TrueTypeSubsetter.cs`) and total 3.99 MB against the
reference's 4.62 MB. The fidelity history is in `STATUS.md` section 2.

## 10. Phases

1. Director: contract, this document, the comparison tool and the reference set.
2. Core, text and media agents in parallel in their own clones; each delivers a green build with its own tests.
3. Director merges; the renderers switch `using QuestPDF.*` to `using AllInOnePdf.Layout.*`; the corpus is compared.
4. Fidelity agents fix the engine until section 9 holds (two rounds: text layer and line metrics, tables and page
   flow; then code blocks, justification, advances at 1/64 pt sizes, font subsetting); QuestPDF is removed from
   `Directory.Packages.props`, the notices and the docs, and `QuestPdfRenderer` becomes `PdfRenderer`.
