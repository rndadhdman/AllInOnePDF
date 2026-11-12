# Creator: the publishing module

The Creator turns AllInOne PDF from "read and convert" into a publishing app. It is a Divi-style visual builder:
a publication is a stack of **sections**, each section holds **rows**, a row splits into **columns** by fractions,
and a column holds **modules** (text, heading, image, divider, spacer, quote, callout, code, table, button, contents,
page break). The look comes from a `PdfTheme` snapshot, so every theme in the gallery is also a publication style.
The output is a real PDF laid out by the app's layout engine (`AllInOnePdf.Layout`), plus every other format through the neutral document model.

This document is the specification the module was built from. `ARCHITECTURE.md` describes how it fits the rest of
the app; `AGENTS.md` has the team rules.

```
 UI (AllInOnePdf.UI)                      Publishing (AllInOnePdf.Publishing)            Core (AllInOnePdf.Core)
 ┌─────────────────────────┐    uses      ┌──────────────────────────────────┐   uses   ┌─────────────────────────┐
 │ /create gallery         │ ───────────▶ │ Model: Publication → Section →   │ ───────▶ │ Documents (Block/Inline)│
 │ /create/{id} builder    │              │   Row → Column → Module          │          │ PdfTheme + validator    │
 │ CreatorState (undo/redo)│              │ Serialization: JSON + .aiopub zip│          │ MarkdownImporter.Parse  │
 │ DesignCanvas (HTML)     │ ◀─ blocks ── │ ModuleBlocks: module → Block[]   │          │ ThemedComposer (Layout: │
 │ CreatorPreview (pages)  │              │ PublicationRenderer (Layout)     │ ───────▶ │   text drawn like PDFs) │
 │ Inspector / Structure   │              │ PublicationFlattener → Document  │          │ IPdfRasterizer          │
 └─────────────────────────┘              │ IPublicationStore (.aiopub files)│          └─────────────────────────┘
                                          │ BuiltInTemplates                 │
                                          └──────────────────────────────────┘
```

## 1. Projects and ownership

| Project | Role |
|---------|------|
| `src/AllInOnePdf.Publishing` | Class library. Model, serialization, package format, store, validator, templates, module → block mapping, flattener, renderer. References Core and the layout engine only. Namespace root `AllInOnePdf.Publishing`. |
| `tests/AllInOnePdf.Publishing.Tests` | xUnit tests for everything above (same csproj shape as `AllInOnePdf.Core.Tests`). |
| `src/AllInOnePdf.Core` | Gains `Rendering/ThemedComposer` (the themed drawing facade extracted from `PdfRenderer`), `MarkdownImporter.Parse` (synchronous snippet parsing) and `AppPaths.PublicationsDirectory`. Nothing else changes; `PdfRenderer` output stays identical. |
| `src/AllInOnePdf.UI` | `Pages/Creator*`, `Components/Creator/*`, `Services/Creator*`, a nav item, DI registration. |

Core never references Publishing. Publishing never references the UI.

## 2. Model (`AllInOnePdf.Publishing.Model`)

Plain mutable classes with public get/set properties, every value defaulted, serialisable with `CoreJson.Options`
(camelCase, enums as strings). Ids are `Guid.NewGuid().ToString("N")`.

```csharp
public sealed class Publication
{
    string Id; string Name = "Untitled publication"; string? Description; int SchemaVersion = 1;
    DateTimeOffset Created, Modified; List<string> Tags;
    PublicationMetadata Metadata;         // Title, Subtitle, Authors (List<string>), Date?, Language = "en", Keywords, Description
    string? ThemeId;                      // gallery theme the snapshot came from (for the picker); may be stale
    PdfTheme Theme;                       // snapshot; Page, Typography, Headings, Colors, Code, Tables, Quotes, Lists, Links, Images,
                                          // Header, Footer and Callouts apply. Theme.Cover and Theme.Toc are ignored (sections and the
                                          // Contents module replace them).
    List<Section> Sections;
    SectionLayout DefaultLayout = Structure; // what new sections get and the "Structure | Free flow" switch; existing sections keep theirs
    List<PublicationAsset> Assets;        // images; bytes live in the package, not in publication.json
}
public sealed class PublicationAsset { string Id; string FileName; string MediaType; byte[] Data; int? PixelWidth, PixelHeight; }

public sealed class Section { string Id; string? Label; BoxDesign Design; bool PageBreakBefore; bool PageBreakAfter;
                              SectionLayout Layout = Structure; double? CanvasHeightPt;   // Free flow: null = one full page
                              List<Row> Rows;                   // Structure only
                              List<PlacedModule> Items; }       // Free flow only
public enum SectionLayout { Structure, FreeFlow }
public sealed class PlacedModule { double XPt, YPt; double WidthPt = 240, HeightPt = 80; int Z; bool LockAspect; Module Module;
                                   const double DefaultWidthPt = 240, DefaultHeightPt = 80, MinSizePt = 8, MaxPt = 2000; }
public sealed class Row     { string Id; string? Label; List<double> Fractions = [1]; double GutterPt = 16; VerticalAlign Align = Top;
                              bool KeepTogether = true; BoxDesign Design; List<Column> Columns; }   // Columns.Count == Fractions.Count
public sealed class Column  { string Id; BoxDesign Design; List<Module> Modules; }

public sealed class Spacing   { double TopPt, RightPt, BottomPt, LeftPt; static Spacing All(double); static Spacing Vertical(double); }
public class BoxDesign        { Spacing Margin; Spacing Padding; string? Background; string? BorderColor; double BorderWidthPt; double CornerRadiusPt;
                                double OffsetXPt; double OffsetYPt; int ZIndex;          // Position (see Rules)
                                const double MaxOffsetPt = 400; const int MaxZIndex = 100; }
public sealed class ModuleDesign : BoxDesign { TextAlignment? Alignment; string? TextColor; double FontScale = 1; }  // TextAlignment from Core.Documents

public enum VerticalAlign { Top, Middle, Bottom }
public enum BlockAlign { Left, Center, Right }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextModule), "text")] … one per module …
public abstract class Module { string Id; string? Label; ModuleDesign Design; bool Hidden; }

public sealed class TextModule     : Module { string Markdown = ""; }                       // CommonMark + GitHub extensions, like the importer
public sealed class HeadingModule  : Module { string Text = "Heading"; int Level = 1; }      // Text allows inline Markdown (**bold**, links)
public sealed class ImageModule    : Module { string? AssetId; string? Alt; string? Caption; double WidthPercent = 100; BlockAlign Align = Center;
                                              double CornerRadiusPt; double? MaxHeightPt; }
public sealed class DividerModule  : Module { DividerStyle Style = Solid; string? Color; double ThicknessPt = 0.75; double WidthPercent = 100; BlockAlign Align = Center; }
public sealed class SpacerModule   : Module { double HeightPt = 24; }
public sealed class QuoteModule    : Module { string Markdown = ""; string? Attribution; }
public sealed class CalloutModule  : Module { CalloutKind Kind = Note; string? Title; string Markdown = ""; }
public sealed class CodeModule     : Module { string Code = ""; string? Language; string? Caption; }
public sealed class TableModule    : Module { bool HeaderRow = true; List<List<string>> Cells; List<ColumnAlignment> Alignments; string? Caption; } // cell text = inline Markdown
public sealed class ButtonModule   : Module { string Text = "Learn more"; string? Url; ButtonStyle Style = Filled; BlockAlign Align = Left; string? Color; double CornerRadiusPt = 4; }
public sealed class ContentsModule : Module { string Title = "Contents"; int Depth = 2; bool ShowPageNumbers = true; }
public sealed class PageBreakModule : Module { }
public enum DividerStyle { Solid, Dashed, Dotted }
public enum ButtonStyle { Filled, Outline, Link }
```

Rules:

* `Row.Fractions` are positive and normalised to sum 1 (validator). `ColumnLayouts.Presets` lists the Divi set as
  `ColumnLayoutPreset(Name, Fractions)`: 1 · ½ ½ · ⅓ ⅔ · ⅔ ⅓ · ⅓ ⅓ ⅓ · ¼ ¼ ¼ ¼ · ¼ ¾ · ¾ ¼ · ¼ ½ ¼ · ⅕ ⅗ ⅕ · ½ ¼ ¼ · ¼ ¼ ½ · ⅙ ×6;
  `ColumnLayouts.Match(fractions)` finds the preset a row uses. `ColumnLayouts.Apply(row, fractions)` keeps content:
  new columns are empty, modules of removed columns are appended to the last remaining column.
* Box design, borders and corners: a `BorderWidthPt` above 0 draws a border in `BorderColor`, or in the theme's
  `Colors.Border` when that is null (the inspector shows it as "Theme border colour"). `CornerRadiusPt` rounds the
  box's background and border and clips everything inside the box, pictures and child backgrounds included; an
  `ImageModule.CornerRadiusPt` rounds the picture itself.
* Box design, Position (sections, rows, columns and modules): `OffsetXPt`/`OffsetYPt` (points, positive = right/down,
  −400…400) shift where the box is drawn without moving anything else: the flow, the neighbours and the page break
  decisions use the unshifted box, so a shifted box may overlap its neighbours or the page margin. `ZIndex` (0…100) is
  the paint order among overlapping boxes: a higher level paints over lower ones, equal levels paint in document
  order, 0 is the normal flow. All three default to 0, files written before they existed load with 0, and the
  validator clamps out-of-range values (non-numbers become 0). The design canvas draws them with
  `position:relative; left; top; z-index`; the PDF renderer (and so Preview) draws them through the layout engine's
  `TranslateX/TranslateY` and `ZIndex` calls (§5).
* Free flow (`Section.Layout == FreeFlow`): the section is a fixed canvas, `CanvasHeightPt` tall (null = one full
  page: the body height left after margins, header and footer; the renderer never lets a canvas exceed one page), as
  wide as the section's content box, and every `PlacedModule` in `Items` is drawn at `XPt`/`YPt` from the canvas's
  top-left (inside the section's margin, border and padding) with `WidthPt` × `HeightPt`, content cut off when it does
  not fit, painted in ascending `Z` (ties in list order). `Rows` is ignored in free flow and `Items` in structure, so a
  section converted back and forth keeps nothing hidden: conversion moves the modules. `Publication.DefaultLayout` is
  what a new section gets and what the Creator's "Structure | Free flow" switch sets; it never changes existing
  sections by itself. The validator clamps positions to ±2000 pt, sizes to 8…2000 pt, `Z` to 0…100 and
  `CanvasHeightPt` to 40…2000, drops items without a module and gives their modules unique ids like any other module.
* `PublicationAsset.Data` is `[JsonIgnore]`: `ToJson()` and undo snapshots stay small, the package carries the bytes,
  and `Clone()` re-attaches them. `Publication.FindAsset(id)` and `Publication.NewId()` are the helpers. Ids must match
  `[A-Za-z0-9_-]{1,64}` (regenerated otherwise) so a publication id is always a safe file name.
* `PublicationValidator.Normalize(publication)` clamps every number (fractions ≥ 0.05, levels 1–6, widths 5–100 %,
  heights 0–600 pt, spacing 0–200 pt, gutters 0–100 pt, font scale 0.5–3, colours via the theme colour rules),
  fixes column/fraction count mismatches, regenerates duplicate ids, drops `null` entries, normalises the theme with
  `ThemeValidator.Normalize`, and clears `AssetId`s that point nowhere. Idempotent.
* `ModuleBlocks.ToBlocks(Module, Publication)` maps a module to Core `Block`s (the single source of truth used by the
  renderer, the flattener and the UI's design canvas): Text → parsed Markdown blocks; Heading → `HeadingBlock`;
  Image → `ImageBlock` with `Source = "res:" + AssetId`, `WidthPercent`, caption; Quote → `QuoteBlock`; Callout →
  `CalloutBlock`; Code → `CodeBlock`; Table → `TableBlock`; Divider → `ThematicBreakBlock`; PageBreak → `PageBreakBlock`;
  Button → `ParagraphBlock` with a `LinkInline` (styled by the renderer); Spacer and Contents → no blocks (renderer
  specials). Markdown is parsed with `MarkdownImporter.Parse` (no front matter, no local image loading).
* `PublicationFlattener.ToDocument(publication)` builds a `Document`: metadata from `Metadata` (title falls back to
  `Name`), assets as `Document.Resources`, sections → rows → columns → modules in reading order, section and row
  page breaks as `PageBreakBlock`, Contents modules skipped (exporters have their own). A free-flow section's items
  are read top to bottom, then left to right (`PublicationFlattener.Modules(section)`: items whose vertical ranges
  overlap form a band, inside a band items whose horizontal ranges overlap form a column, columns left to right,
  each read top to bottom), so Word, HTML, EPUB and Markdown exports read a canvas sensibly. Used for Export as… and
  Read in Reader.

## 3. Package format and store (`Serialization`, `Storage`)

A publication is saved as **`<id>.aiopub`**, a zip:

```
publication.json   the Publication (CoreJson), assets listed without bytes
assets/<id>        raw image bytes (id is 32 hex chars; the entry name is validated on read)
thumbnail.png      first page at low DPI (optional; the UI supplies it on save)
```

`PublicationPackage.Write(Publication, byte[]? thumbnail) → byte[]` and `Read(Stream) → (Publication, byte[]? thumbnail)`
(constants `Extension`, `MediaType`, `MaxPackageBytes`). Reading rejects packages over 200 MB, entries outside the
known names, and asset ids that do not match `^[a-f0-9]{32}$`; every failure, bad JSON included, is an
`InvalidDataException`; nothing is extracted to disk.

```csharp
public interface IPublicationStore
{
    Task<IReadOnlyList<PublicationSummary>> ListAsync(CancellationToken ct = default);   // sorted by Modified desc
    Task<Publication?> GetAsync(string id, CancellationToken ct = default);
    Task SaveAsync(Publication publication, byte[]? thumbnailPng = null, CancellationToken ct = default); // atomic (.tmp + move), sets Modified
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
    Task<Publication> DuplicateAsync(string id, string? newName = null, CancellationToken ct = default);
    Task<Publication> ImportAsync(Stream package, CancellationToken ct = default);        // fresh id when it collides
    Task<byte[]?> GetThumbnailAsync(string id, CancellationToken ct = default);
    event Action? Changed;
}
public sealed record PublicationSummary(string Id, string Name, string? Description, DateTimeOffset Modified, string ThemeName,
                                        PageSizeName PageSize, PageOrientation Orientation, int SectionCount, IReadOnlyList<string> Tags);
```

`FilePublicationStore` keeps files in `AppPaths.PublicationsDirectory` (`<data>/publications`), mirrors
`FileThemeStore` (gate, logging, skip unreadable files). `AddAllInOnePdfPublishing(this IServiceCollection)` registers
the store and the renderer with `TryAddSingleton`.

## 4. Templates (`Templates/BuiltInTemplates`)

`PublicationTemplate(string Id, string Name, string Description, IReadOnlyList<string> Tags, Func<Publication> Create)`.
`BuiltInTemplates.All()`; each `Create()` returns a fresh, normalised publication whose `Name` is the template name:

| Id | Theme | Layout |
|----|-------|--------|
| `blank` | Clean | One section, one row, one empty column. |
| `newsletter` | Clean | Masthead row (title + date), ½ ½ lead story with image placeholder, ⅓ ⅓ ⅓ short items, footer divider + contact line. |
| `flyer` | Swiss | Big heading, hero image placeholder, ½ ½ feature texts with callouts, centred button. |
| `report` | Academic | Title section (heading, subtitle, author, date, page break), Contents module, three chapters with headings, text, a table and a Note callout. |
| `menu` | Newsprint | Two ⅓ ⅔ … rows of dish names and prices as Minimal tables, dividers between courses. |
| `resume` | Compact | ⅓ ⅔: sidebar (name, contact, skills list) and main column (experience, education). |
| `invoice` | Technical | Header row (company block, invoice meta), line-item table, ⅔ ⅓ totals right-aligned, terms callout. |
| `poster` | Swiss (A4 portrait, no header or footer) | One **free-flow** section filling the page (`CanvasHeightPt` null, `DefaultLayout = FreeFlow`): a large H1 near the top, an intro line, an image placeholder across the width, a text block under it on the left, a white bordered quote overlapping the picture's bottom edge on the right at Z 2, and a button at the bottom. Positions are computed from the theme's content width and body height. |

Image placeholders are `ImageModule`s with `AssetId = null`; see rendering. `BuiltInTemplates.All()` lists eight
templates; the UI iterates the list and assumes no count.

## 5. Rendering (`Rendering/PublicationRenderer`)

```csharp
public interface IPublicationRenderer
{
    Task<byte[]> RenderAsync(Publication publication, PublicationRenderOptions? options = null, CancellationToken ct = default);
    Task<PublicationRenderResult> RenderWithPagesAsync(Publication publication, PublicationRenderOptions? options = null, CancellationToken ct = default);
}
public sealed class PublicationRenderOptions { bool Draft; bool ShowPlaceholders; bool EmbedMetadata = true; int ImageQuality = 85; }
public sealed record PublicationRenderResult(byte[] Pdf, int PageCount, IReadOnlyDictionary<string, PublicationPageSpan> Pages)
{   IReadOnlyDictionary<string, IReadOnlyList<PublicationBox>> Boxes; IReadOnlySet<string> Overflowing; }
public readonly record struct PublicationPageSpan(int First, int Last);
public readonly record struct PublicationBox(int Page, double XPt, double YPt, double WidthPt, double HeightPt);
```

Algorithm (layout engine, one `Page` description; `Core.Rendering.ThemedComposer` does every themed drawing):

1. `composer = new ThemedComposer(theme, flattenedContextDocument, highlighter)` where the context document carries
   metadata, resources and the heading blocks (registered so `{section}`, heading numbering and Contents links work).
2. `page` ← `composer.ConfigurePage`; header/footer ← `composer.ComposeHeader/ComposeFooter` when the theme enables them.
3. Content column: for each section → optional `PageBreak`, a box (`BoxDesign`: padding, background, border, radius)
   → each row → `Row` with `Spacing(GutterPt)` and `RelativeItem(fraction)` per column, vertical alignment applied per
   column → each column → box → each module (skipped when `Hidden`):
   * blocks from `ModuleBlocks.ToBlocks` → `composer.ComposeBlocks(container, blocks, columnWidthPt)` wrapped in the
     module box; `ModuleDesign.Alignment`, `TextColor` and `FontScale` applied via `DefaultTextStyle`/alignment.
   * Spacer → `Height(HeightPt)`; Divider → aligned `Width(percent)` + `LineHorizontal` (dashed/dotted drawn as
     segments); Button → aligned filled/outlined box with the theme accent (or `Color`), text in the body font,
     `Hyperlink(Url)`; Contents → `composer.ComposeToc(container, headings up to Depth, Title)`; PageBreak → `PageBreak()`.
   * Image with an asset → layout engine `Image` with `FitWidth` inside an aligned `Width(percent)` box, `MaxHeightPt`
     honoured, `CornerRadiusPt` clipped; without an asset → when `ShowPlaceholders`, a light grey rounded box
     (`Colors.Surface`, border `Colors.Border`, aspect 3:2, "Image" label in muted text), otherwise nothing.
4. `Row.KeepTogether`: `PreventPageBreak()` when the row's estimated height (text length heuristics like
   `PdfRenderer`) is under 80 % of the body height for multi-column rows, or 35 % for single-column rows
   (a page of running text is better split than pushed down with a gap); otherwise the row flows. A layout
   exception triggers one retry without keep-together.
5. Column widths: `columnWidthPt = (contentWidth − gutters) × fraction − column padding − border`, used for the
   composer's estimates (code shrink, keep-with-next).
6. Metadata: title = `Metadata.Title ?? Name`, authors, subject, keywords, language; `Draft` lowers image quality
   like `PdfRenderOptions.Draft`.
7. **Position** (structure sections, rows, columns, modules): every box is built as
   `TranslateX(OffsetXPt).TranslateY(OffsetYPt)` → `ZIndex(z)` (only when z > 0) → `TrackPages(key)` → margin,
   background, border, radius, padding → content. The translate sits outside everything the box draws, so the whole
   box moves while the flow, the neighbours and the page breaks stay exactly where they were (the engine measures a
   translated element as if it were not); a Z level above 0 defers the box's painting on each page until after the
   normal content, in ascending z, ties in document order. Tracking sits inside the translate, so the reported box is
   where the box really painted. A text-like module with a Position is drawn on its own (not merged into the
   column's flow of plain modules). The design canvas draws the same thing with `position:relative`, so they agree.
8. **Free flow** (`Section.Layout == FreeFlow`; `Rows` ignored): the section's box (Position, margin, background,
   border, radius, padding as above) around a canvas that is additionally inset by the border width, so the canvas
   starts inside margin + border + padding like the design canvas's CSS box. The canvas is the section's content
   width wide and `LayoutEstimator.CanvasHeight(section, bodyHeight)` tall: `CanvasHeightPt`, or the composer's
   `BodyHeightPt` (page content height less the header and footer bands) when null, clamped to
   `bodyHeight − (margins + paddings + 2 × border)` so canvas plus box never exceed one page, and at least 1 pt.
   Inside, `Freeform` places one `f.Item(x, y, w, h, z, key: "m:" + id)` per item (hidden modules and page breaks
   skipped), wrapped in `TrackPages("m:" + id)`; the module is composed like a structure module of width `w`
   (`ModuleBlocks`, its `Design` box inside the item, its design offsets and Z ignored, no keep-with-next). Nothing is
   trimmed: a leading heading keeps its theme space-before inside the item, exactly as a structure column draws it,
   so the design canvas must draw free-flow items without the `.aio-h:first-child { margin-top:0 }` rule (keep the
   heading's top margin inside the item box) to agree with the PDF. An image in an item keeps the theme's image
   spacing at the bottom (when there is room) and fills the rest less its caption (placeholder: the grey box over the
   whole area; picture: aspect kept, `FitArea`); with a caption the picture is at most the height a structure column
   gives it, so the caption sits right under it. Width %, alignment and max height are honoured. Items paint in ascending `Z`, ties in list order, clipped to their box and to the
   canvas. The canvas is a fixed `Height`, so a free-flow section that does not fit the rest of the page moves to the
   next page by itself; `PageBreakBefore/After` work as for structure sections; a publication whose only section is
   free flow is one page. Headings on canvases register in reading order (see the flattener), so Contents and
   numbering follow it.
9. **Result** (`RenderWithPagesAsync`): `Pages` maps every tracked id (prefix stripped) to its first and last page;
   `Boxes` maps it to `DocumentLayoutInfo.TrackedBoxes` converted to `PublicationBox` (page coordinates in points
   from the page's top-left, margins included, one entry per draw in draw order). Content split across pages has one
   box per page; a module in a structure column's flow of plain modules has one box per block it draws (heading,
   each paragraph, …), so take the union per page for its extent; a section, row or column has one box per page.
   Free-flow items never split: exactly one box at canvas origin + (x, y), `w` × `h`; an item whose content does not
   even start in its box (too small for a line, or for the two lines orphan control asks for) draws nothing but
   still gets that box and its page. `Overflowing` is the ids of free-flow items whose content did not fit their box
   (drawn cut off, or not at all).

**Converting a section (`Content/SectionLayoutConverter`)**:

* `CanvasBox(section, boxes)` → the canvas of the section on the page where the last render started drawing it:
  the section's first box less margin, border and padding (left/top: `margin + border + padding`; width/height:
  minus both sides). Null when the render has no box for it.
* `ToFreeFlow(section, canvasWidthPt, canvasOrigin?, boxes?, publication?)`: modules in reading order become items
  (page-break modules dropped). With boxes and an origin, an item gets the union of its module's boxes on its first
  page relative to the origin (plus the heights drawn on later pages; a module starting on a later page than the
  origin is stacked under the previous items). The one adjustment: a heading module with a box of its own that the
  structure render drew with its space-before pulled up inside its box (top of a multi-column row whose columns open
  with different heading spaces) starts that space higher. Rendering the result puts every module's box, and every
  word, where the structure PDF had it (tested on every template section within 0.5 pt). Without boxes, modules stack from
  the top at full width with `LayoutEstimator` heights (+4 pt) and `StackGapPt` between them. Offsets are baked into
  the item's place and reset to 0; a module's `ZIndex` becomes the item's `Z` and resets to 0. A null
  `CanvasHeightPt` is set to the content's height (at least the origin's height, ≥ 40 pt) so later content does not
  move to the next page. `publication` supplies the theme and assets for the estimates (Clean without it).
* `ToStructure(section)`: the canvas read as bands (vertical overlap) → one row each; inside a band, items that
  overlap horizontally share a column (left to right, stacked top to bottom); fractions in proportion to the
  columns' extents; more than 20 columns merges the narrowest neighbouring pair until it fits. An item's `Z` above 0
  becomes its module's `ZIndex`. Structure → Free flow → Structure keeps every module in order.

Not supported on purpose: text flowing from one column into the next (the layout engine's columns paginate independently),
section backgrounds bleeding into the page margins, rotated elements, free-flow canvases taller than a page. Overlap
comes from a box's Position (offset and Z level, see §2) and from free-flow items.

## 6. The Core facade: `ThemedComposer`

`PdfRenderer.Composer` already draws every block the way the theme says. `ThemedComposer` (public, sealed,
`AllInOnePdf.Core.Rendering`) is that drawing code made reusable; `PdfRenderer` is rewritten on top of it so there
is one implementation and its output is unchanged (the existing rendering tests must pass untouched).

```csharp
public sealed class ThemedComposer
{
    public ThemedComposer(PdfTheme theme, Document context, ISyntaxHighlighter? highlighter = null, ThemedComposerOptions? options = null);
    // context supplies metadata (templates, PDF info), resources (res: images) and footnotes; its Blocks may be empty.
    public PdfTheme Theme { get; }
    public double ContentWidthPt { get; }      // page width minus margins
    public double BodyHeightPt { get; }        // content height minus header and footer bands
    public string Title { get; }
    public DocumentSettings CreateDocumentSettings(int imageQuality = 85);   // applies Draft

    public void RegisterHeadings(IEnumerable<HeadingBlock> headings);   // assigns section ids, numbers and {section} capture ids
    public void ConfigurePage(PageDescriptor page, bool isCover = false);
    public void ComposeHeader(IContainer container);                    // theme.Header strip, templates resolved per page
    public void ComposeFooter(IContainer container);
    public void ComposeBlocks(IContainer container, IReadOnlyList<Block> blocks, double? availableWidthPt = null, BlockTextStyle? style = null);
    public void ComposeBlocks(ColumnDescriptor col, IReadOnlyList<Block> blocks, double? availableWidthPt = null, BlockTextStyle? style = null);
    public void ComposeInlines(TextDescriptor text, IEnumerable<Inline> inlines, bool uppercase = false);
    public void ComposeToc(IContainer container, IReadOnlyList<HeadingBlock> entries, string? title = null, bool? showPageNumbers = null);
    public void ComposeCover(IContainer container);
    public void ComposeFootnotes(IContainer container);
}
public sealed class ThemedComposerOptions { bool Draft; CancellationToken CancellationToken; string? FallbackTitle; }
public sealed record BlockTextStyle(double FontScale = 1, string? TextColor = null);   // scales every themed size; recolours body, heading and quote text
public static class ThemeDrawing { QColor Hex(string? hex, string fallback); bool IsDark(string? hex); string[] FontChain(string family); }
public static class ImageHeader { (int Width, int Height) ReadPixelSize(byte[] data); }
```

`ComposeBlocks` honours keep-with-next, orphan control and page-break-before-H1 (never for the first block of a call).
`availableWidthPt` is used for the estimates (code shrink-to-fit, keep-with-next heights); null means the page's
content width. The `ColumnDescriptor` overload adds the blocks straight into an existing column, which is how
`PdfRenderer` keeps its pagination byte-for-byte; a non-default `BlockTextStyle` nests the flow so it can be
wrapped. Unregistered headings draw but are no link or page-number target. `HeadingBlock.Alignment` (a Core model
addition) overrides the theme level's alignment, in the PDF and in the HTML renderer.

## 7. UI: `/create` and `/create/{id}`

Nav: **Create** between Reader and Convert (`Icons.Regular.Size20.DesignIdeas`, id `aio-nav-create`).

**Gallery (`Pages/Creator.razor`)**: `PageHeader` "Create" with "New publication" (opens the template picker) and
"Import…" (`.aiopub`). "Start from a template" strip of template cards (thumbnail = page 1 of the template rendered
with `ShowPlaceholders`, cached in a singleton `CreatorThumbnailCache` like the themes gallery). "Your publications"
grid: thumbnail, name, theme name, modified, actions Open · Duplicate · Export package · Delete (confirm; also deletes
the publication's draft). Empty state when there are none.

Unsaved work is never lost from view: `CreatorDrafts` (singleton, `UI/Services/CreatorDrafts.cs`) lists the
`publication:<route id>` drafts from `IDraftStore`, reads each one's name with `CreatorState.ReadDraft` (the same
package reader the builder's recovery uses), skips unreadable drafts and drafts older than 30 days, and orders them
newest first. `CreatorDrafts.Match` splits them against the saved publications: drafts of never-saved publications
(`new:<templateId>`) fill a **Continue where you left off** strip above the templates (page-1 thumbnail rendered from
the draft and cached per key and write time in `CreatorThumbnailCache`, name, "New publication from the X template",
"Unsaved changes from <relative time>", **Continue** and **Discard** with a confirmation). A saved publication whose
draft is newer than its last save keeps its card, marked "Unsaved changes from …", with **Continue** in place of Open
and a discard button; drafts of deleted publications are not shown. **Continue** calls
`CreatorState.RequestDraftRestore(routeId)` before navigating to `/create/<routeId>`: the next `CheckForDraftAsync`
for that route restores the draft straight away (one undo step) instead of showing `DraftRecoveryBar`; opening a
route any other way still shows the bar. The request is used up by the next check whatever the route.

**Builder (`Pages/CreatorBuilder.razor`, route `/create/{Id}`; `new:<templateId>` creates from a template)**. Layout
mirrors the theme builder (`tb-*` structure, same tokens):

* **Top bar**: back, editable name, dirty indicator, Undo/Redo, mode toggle **Design | Preview**, zoom, **Theme…**
  (dialog listing gallery themes with thumbnails; picking one replaces `Publication.Theme` with a clone and sets
  `ThemeId`; one undo step), **Export PDF** (accent; desktop save dialog or browser download), a More menu: Export as…
  (DOCX, HTML, EPUB, Markdown, text via the flattener and Core exporters), Read in Reader (flatten →
  `AppState.SetDocument` → `/read`), Save package (.aiopub), Reset to saved. **Save** (Ctrl+S). Leaving with unsaved
  changes asks, like the theme builder.
* **Left panel (280 px, collapsible)**: tabs **Structure** (tree Sections → Rows → Columns → Modules; click selects,
  drag reorders, row actions duplicate/delete/move; a dotted "Page n" line from `CreatorState.PageMap` marks where
  each PDF page begins, at whatever depth the break falls, and a module or side-by-side row that runs over a page
  shows "p. 1–2"; "Add section" and "Add page" at the foot) and **Insert** (module tiles with icons; click inserts
  after the selection or into the selected column; tiles are draggable onto the canvas).
* **Canvas, Design mode (`Components/Creator/DesignCanvas.razor` + `.js`)**: a continuous scroll of page sheets of
  the page's width (theme page size and margins; the page background), each labelled "Page n of m". The sheets follow
  the real PDF: after every edit `CreatorState` renders the working copy in the background (450 ms after the last
  change, cancelling a pending render) through `IPublicationRenderer.RenderWithPagesAsync`, which reports the first
  and last page of every section, row, column and module (the layout engine's `TrackPages` keys, see
  `PublicationLayout`), and publishes it as `CreatorState.PageMap` (the preview reuses that PDF when the content has
  not changed). `CreatorCanvasSheets.Build` then starts a new sheet wherever the PDF starts a new page, down to the
  module: sections split between rows, single-column rows between modules (a section or row drawn on several sheets
  has one `<section>`/`.cr-row`/`.cr-col` part per sheet, same `data-id`, `data-part` counting up, toolbar on the
  first part only; the JS drop-index code counts modules and rows across parts). A multi-column row or a module the
  PDF splits inside stays on the sheet it starts on with a dotted "Continues on page n" mark. Before the first render
  (or without a renderer) only the explicit breaks split (`CreatorCanvasHtml.SheetRanges`: a section that starts on a
  new page after content, a section followed by a page break, a page-break module that ends a section). A stale map
  (ids added since) keeps new content on the sheet of its neighbours until the next render. Within a sheet, sections
  are stacked, rows are CSS grid
  (`grid-template-columns` from fractions, `gap` = gutter), columns are grid cells, modules are rendered as HTML from
  `ModuleBlocks.ToBlocks` through `IDocumentHtmlRenderer`, styled by `ThemeBuilderCssGenerator.Generate(theme, scope)`
  (reuse, do not fork). Box designs become inline styles. A page-break module in the middle of a column is a dashed
  line with a label. Empty columns show a dashed "Add module" target. Hover shows a Divi-style floating toolbar on the
  element (move handle · settings · duplicate · delete), selection gets the accent outline, structural "+" buttons
  appear between rows ("Add row") and after the last section ("Add section", and "Add page", which adds a section
  that starts on a new page). Drag and drop via HTML5 DnD in the JS module: modules move
  within and between columns, rows between sections, sections reorder; an insertion line shows the drop position; the
  drop reports `(kind, id, targetContainerId, index)` to .NET. Keyboard: Delete removes the selection (with undo),
  Ctrl+D duplicates, Ctrl+Z/Y, Esc clears, Alt+↑/↓ moves the selection.
* **Structure | Free flow** (segmented control in the canvas bar above the canvas, `CreatorBuilder`): bound to
  `Publication.DefaultLayout`. Switching when some sections use the other layout opens `LayoutSwitchDialog`
  ("Convert the existing sections to Free flow too?" / "… to Structure too?": **Convert all**, **Only new sections**,
  **Cancel**) and calls `CreatorState.SetDefaultLayout(layout, convertExisting)` (one undo step). **Add section**,
  **Add page** and the empty-publication insert create sections in the default layout.
* **Canvas, Free flow sections**: `CreatorCanvasSheets` gives a free-flow section a sheet of its own (it starts a
  sheet and the next section starts another, with or without a page map; `CreatorCanvasHtml.SheetRanges` likewise);
  it never splits. The section box keeps its toolbar ("Section n · Free flow") and box design; inside it `.cr-ff` is
  the canvas, `CreatorCanvasHtml.CanvasStyle` sizes it in points (the stage zoom scales points like every other length):
  width `CreatorFreeFlow.CanvasWidthPt` = page width − left/right page margins − the section's left/right margin,
  border and padding; height `CreatorFreeFlow.CanvasHeightPt` = the custom `CanvasHeightPt` capped to a full page, or a
  full page = `BodyHeightPt` (page height − top/bottom margins − header and footer bands, the same formula as
  `ThemedComposer.BodyHeightPt`: band = size × 1.25 + spacing + 6 with a rule) − the section's top/bottom margin,
  border and padding. Each item is `.cr-item` (`ItemStyle`: absolute `left/top/width/height` in pt, `z-index` = `Z`, DOM
  order = list order so ties paint like the PDF) holding `.cr-item-clip` (`ItemClipStyle`: `overflow:hidden`, the module
  design without its Position) with the same module HTML as Structure; images fill their box (`object-fit:contain`). An
  empty canvas shows a dotted "Drag modules here". Items whose id is in `PageMap.Overflowing` carry a warning badge
  ("Content is cut off. Make the box larger."). The selected item gets an overlay (`.cr-ff-sel`, above every item)
  with its toolbar and eight resize handles.
* **Free-flow interactions (`DesignCanvas.razor.js`)**: pointer events, not HTML5 drag and drop. Click selects; a drag
  (after 3 px) moves with a live CSS transform and calls `OnItemMoved` once on release (`CreatorState.MoveItem`, one
  undo step); handles resize live and call `OnItemResized` (`ResizeItem`). Corners keep the aspect when `LockAspect`
  (Shift inverts), sizes ≥ 8 pt. Snapping (4 screen px, converted to points with the zoom) to the canvas's edges and
  centre lines and other items' edges and centres, with guide lines; Alt disables it; Shift locks the axis while
  moving. The maths mirror `Services/CreatorSnap.cs` (`Move`, `Resize`, `SnapResize`, tested). The live position stays
  until the circuit re-renders the item there (or is restored after 1.5 s). Keyboard (`CreatorBuilder.razor.js`, only
  with a selected item and the canvas or nothing focused): arrows nudge 1 pt, Shift+arrows 10 pt (`NudgeItem`, held
  keys coalesce into one step), Delete, Ctrl+D (copy at +12, +12), Ctrl+] / Ctrl+[ forward / backward, Ctrl+Shift+] /
  Ctrl+Shift+[ to front / back; the same four buttons are on the item toolbar. Double-click opens the inspector's
  Content tab. An Insert tile dropped on a canvas creates the module with its top-left at the pointer and a per-type
  size (`CreatorFreeFlow.DefaultSize`: text 240×80, heading 300×48, image 200×150 with Lock aspect, divider 240×12,
  spacer 100×24, quote 260×90, callout 260×100, code 300×120, table 320×120, button 140×36, contents 260×200); a
  structure module dropped there becomes an item. The Page break tile is disabled ("Not in Free flow") whenever a
  click would insert onto a canvas (`CreatorState.InsertTargetIsFreeFlow`). Structure interactions are unchanged.* **Canvas, Preview mode (`Components/Creator/CreatorPreview.razor` + `.js`)**: the real PDF via
  `IPublicationRenderer` (`Draft`, `ShowPlaceholders`) rasterised with `IPdfRasterizer`, page strip + main page,
  debounced and cancellable (same approach as `ExactPreview`). Pages turn by clicking the strip, the previous/next
  buttons in the "Page n of m" pill, Page Up/Page Down (once the page is scrolled to its top/bottom), the left and
  right arrows, Home/End, and a wheel that keeps going past the bottom or top of a page (after a short dwell, one page
  per gesture).
* **Right panel (360 px) Inspector (`Components/Creator/Inspector.razor`)**: shows the selection with
  **Content** and **Design** tabs.
  * Module content editors: Text (Markdown textarea with a small toolbar: bold, italic, link, bullet list, heading,
    quote, code; live canvas update, 150 ms debounce), Heading (text, level segmented 1–6), Image (Choose image…:
    desktop dialog or `FluentInputFile`, PNG/JPEG/WebP/GIF/SVG up to 25 MB, stored as an asset; alt, caption,
    width %, align, radius, max height; Remove), Divider, Spacer, Quote (Markdown + attribution), Callout (kind,
    title, Markdown), Code (language select from `SyntaxHighlighter.SupportedLanguages`, caption, code textarea),
    Table (grid editor: cells, add/remove row/column, header row switch, per-column alignment, caption), Button
    (text, URL, style, align, colour, radius), Contents (title, depth, page numbers), Page break (nothing).
  * Design tab for a module: margin and padding (four fields with a "same on all sides" link), background, border
    colour/width, corner radius (hint "Rounds the box"; the Image editor's own radius says "Rounds the picture"),
    **Position** (after the Border group: Offset X and Offset Y, slider −200…200 pt, typed values up to ±400, step 1,
    hint "Moves the box without moving its neighbours"; Z level 0…100, hint "Higher levels paint over lower ones"),
    text alignment override, text colour, font scale. Sections, rows and columns get the same box fields, Position
    included.
  * Design canvas mirrors the box design: border, background, `border-radius` on the box; a radius also clips the
    content (`overflow:hidden` with the radius reduced by border and padding on an inner wrapper: the module body, the
    section's rows, the row's grid; a column clips itself), so a picture or a child background is cut at the corners
    like the PDF. Toolbars of elements inside a clipping box sit just inside their element's top edge instead of above
    it. Position draws as `position:relative; left; top; z-index`.
  * Free-flow item (a module on a canvas): the Design tab starts with **Box** (`ItemBoxEditor`: X, Y, Width, Height in
    points, Z level 0…100, Lock aspect; with Lock aspect the size fields keep the ratio); the Position group of
    `BoxDesignEditor` is hidden (`ShowPosition=false`) because the box already places the module. Content tab unchanged.
  * Section: **Layout** (Structure | Free flow, `SectionLayoutSettings`, calls `SetSectionLayout`) and, for free flow,
    **Canvas height** (Full page | Custom, pt, 40 pt up to a full page). Page-break switches and Add row show for Structure only.
  * Row: layout picker (visual tiles of `ColumnLayouts.Presets`, current one marked), gutter, vertical alignment,
    keep together, label, Design box. Column: Design box. Section: label, page break before/after, Design box.
  * Nothing selected: **Document**: name, metadata (title, subtitle, authors, date, language, keywords), theme
    (current name + Change…), **Page** (size, orientation, custom size, margins, background: edits the snapshot
    theme's `Page`), **Header & footer** (on/off, left/centre/right templates, size, colour, rule, first page: edits
    the snapshot theme). Every change is one undo step, coalesced while typing.
* Controls live in `Components/Creator/Fields/*` (number with slider, colour, switch, enum segment, text, spacing
  box), modelled on the theme builder's but bound to `CreatorState` (the theme builder's controls commit to
  `ThemeBuilderState` and must not be changed). The number field's slider and box are native inputs
  (`RangeInput`, `NumberInput`), not `FluentSlider`/`FluentNumberField`: those web components re-emit `input`/`change`
  whenever the circuit sets their value, so with several values in flight (a drag, held arrow keys) every stale value
  came back as a new edit and the field oscillated without end. `RangeInput` does not re-render while the pointer
  holds the thumb; `NumberInput` does not re-render while focused except to correct a rounded or clamped entry.
  Rounding, clamping, parsing and formatting are in `Services/CreatorNumber.cs`. Undo keys are per field and per
  selected element (`num:<key>:<id>`), so a drag is one step.

**`Services/CreatorState.cs`** (scoped): `Working` publication, `Version`, `IsDirty`, `IsNew`, undo/redo stacks of
JSON snapshots with coalescing (`Commit(string? key)`, 1.2 s window, 200 steps), `Selection` (`Kind`: Document |
Section | Row | Column | Module, `Id`), structural operations that each commit one step (`AddSection`, `AddRow(sectionId,
fractions, index)`, `SetRowLayout`, `AddModule(module, columnId, index)`, `Move(kind, id, targetId, index)`, `Duplicate`,
`Delete`, `SetTheme`), `AddAssetAsync(bytes, fileName, mediaType)` (decodes pixel size when it can), `Load`, `SaveAsync`
(renders the thumbnail with the renderer at 48 DPI), `SaveAsCopyAsync`, `ResetToSaved`, `Forget`, `Changed`.
Snapshots exclude identity (`Id`, `Name`, `Created`, `Modified`) like `ThemeBuilderState.Snapshot`.

Free flow in `CreatorState` (each commits one undo step; ids of items are their module ids, `Locate` returns a
`CreatorLocation` with `Item` (the `PlacedModule`; Row and Column null, Index = list index), and `Select`, `Duplicate`
(copy at +12, +12 just above the original), `Delete` (selects the section), `Move` and undo work for items):
`SetDefaultLayout(layout, convertExisting)`, `SetSectionLayout(sectionId, layout)`, `AddItem(sectionId, module, x, y,
w, h)`, `MoveItem(id, x, y, coalesceKey?)`, `NudgeItem(id, dx, dy)`, `ResizeItem(id, x, y, w, h)`, `SetItemZ(id, z)`,
`BringForward/SendBackward/BringToFront/SendToBack(id)` (paint order = ascending Z, ties in list order; a move gives
the item its neighbour's level and the list slot next to it, so other items keep their levels), `ReorderItem(id,
sectionId, frontIndex)` (the tree's drag; also moves an item to another canvas), `PlaceOnCanvas(id, sectionId, x, y)`
(canvas drop of an existing module), `FindItem(id)`, `InsertTargetIsFreeFlow`. `Move(id, columnId, index)` takes an
item off its canvas into a column and `Move(moduleId, freeFlowSectionId, frontIndex)` puts a column's module on a
canvas. `MoveBy` on an item brings it forward (−1) or sends it backward (+1). `SetSectionLayout` to Free flow passes
`SectionLayoutConverter.ToFreeFlow` the canvas origin and width from the latest render: the section's first box in
`PageMap.Boxes` moved in by the section's margin, border and padding on the left and top, width = that box's width
minus those on both sides (`CreatorFreeFlow.CanvasOrigin`, to be replaced by `SectionLayoutConverter.CanvasBox`), and
each module's boxes on that page as one union box, so modules stay where they are on screen; with no boxes (no render
yet, or a renderer that reports none) the converter stacks them. `CreatorFreeFlow.AfterConversion` mirrors the
converter's own follow-up (module offsets and Z move onto the item, a null canvas height becomes the content's
height) and is a no-op once the converter does it. Back to Structure, `ToStructure` groups vertically overlapping
items into rows; an empty result gets one empty row. `CreatorPageMap` carries `Boxes` and `Overflowing` from the
render result (`CreatorPageMap.From`).

**Structure tree, Free flow**: a free-flow section ("Free flow" meta) lists its items front to back (highest Z first)
with an "x, y" meta and a "cut off" mark for overflowing items; dragging an item changes its stacking order (onto
another canvas it moves there, onto a column it leaves the canvas); the up and down buttons bring forward and send
backward. Page markers still come from `PageMap`.

**`Services/CreatorCanvasHtml.cs`**: builds the design-mode markup from a publication (data attributes
`data-kind`, `data-id`, `data-index`; one `<article class="aio-doc">` per module so the theme CSS applies).

Accessibility: every canvas element is focusable (`tabindex="0"`, `aria-label` "Text module 2 of 3"), the toolbar
buttons are real buttons, drag and drop has keyboard equivalents, live region announces structural changes.

## 8. Phases

1. **Core facade** (`ThemedComposer`, `MarkdownImporter.Parse`, `AppPaths.PublicationsDirectory`) and **Publishing
   model** (model, validator, serialization, package, store, templates, `ModuleBlocks`, flattener, DI, tests) in
   parallel.
2. **PublicationRenderer** (+ tests rendering every template, visual check of the pages) and **Creator UI** in parallel.
3. Integration: DI, nav, docs, visual QA in the web host, packages.

## 9. Status and known limits

All three phases shipped (November 2026). Where the build deliberately stops short:

* The design canvas approximates two things the PDF gets right: heading numbers are counted per module, and a
  Contents module shows `#` instead of page numbers. Preview mode shows the real thing.
* Text flows down a column and the next column starts fresh; text never continues from one column into the next.
* Section and row backgrounds stay inside the page margins (no full-bleed bands).
* Images are capped at 20 MB each (`UploadLimits.MaxImageBytes`).
* Theme cover pages and the theme's own contents list are ignored by publications: build a title section and
  drop in a Contents module instead.
