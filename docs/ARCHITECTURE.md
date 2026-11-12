# Architecture

AllInOne PDF converts files to PDF and PDF to other formats with a pure .NET engine, fully offline, on
Windows and Linux. One UI codebase runs as a desktop app and as a web app.

```
 ┌──────────────────────────┐    ┌──────────────────────────┐
 │ AllInOnePdf.Desktop      │    │ AllInOnePdf.Web          │
 │ Photino window (OS       │    │ ASP.NET Core Blazor      │
 │ webview) + Blazor        │    │ Server (localhost)       │
 └────────────┬─────────────┘    └────────────┬─────────────┘
              │  RootComponents.Add<Routes>   │ MapRazorComponents + AddAdditionalAssemblies
              ▼                               ▼
 ┌──────────────────────────────────────────────────────────┐
 │ AllInOnePdf.UI  (Razor Class Library, Fluent UI Blazor)  │
 │ Layout · Pages (Reader, Create, Convert, Themes,         │
 │ Builder, History, Settings) · Components · Services      │
 │ (AppState, Preferences, IHostServices)                   │
 └──────────────┬─────────────────────────┬─────────────────┘
                │                         ▼
                │          ┌──────────────────────────────────┐
                │          │ AllInOnePdf.Publishing           │
                │          │ Publication model (sections,     │
                │          │ rows, columns, modules) · .aiopub│
                │          │ packages and store · templates · │
                │          │ PublicationRenderer · flattener  │
                │          └──────────────┬───────────────────┘
                ▼                         ▼
 ┌──────────────────────────────────────────────────────────┐
 │ AllInOnePdf.Core                                         │
 │ Documents (neutral model) · Conversion (importers,       │
 │ exporters, registry, service, queue, options) · Themes   │
 │ (PdfTheme, built-ins, store) · Rendering (PdfRenderer,   │
 │ ThemedComposer, pdfium raster, HTML) · Fonts · Storage   │
 └────────────────────────────┬─────────────────────────────┘
                              ▼
 ┌──────────────────────────────────────────────────────────┐
 │ AllInOnePdf.Layout  (the PDF layout engine)              │
 │ Fluent document model · pagination · HarfBuzz shaping ·  │
 │ subset fonts · PDF output through SkiaSharp              │
 └──────────────────────────────────────────────────────────┘
```

## The one idea: a neutral document model

Every importer turns a file into a `Document` (`Core/Documents`). Every exporter turns a `Document` into a file.
Adding a format means writing **one** importer or **one** exporter, never N×M converters.

```
file ──IImporter──▶ Document ──IExporter──▶ file(s)
                       │
                       ├──IPdfRenderer (layout engine + PdfTheme)──▶ PDF bytes ──IPdfRasterizer──▶ page PNGs (preview)
                       └──IDocumentHtmlRenderer──▶ HTML fragment (Reader, fast theme preview)
```

`Document` holds `Block`s (heading, paragraph, list, code, quote, callout, table, image, rule, page break, raw HTML,
math) made of `Inline`s (text, styled run, code, link, image, line break, footnote ref). Binary resources (images)
live in `Document.Resources` and are referenced as `res:<id>`, so a document is self-contained.

## Conversion pipeline (`Core/Conversion`)

| Type | Role |
|------|------|
| `FileFormat`, `KnownFormats` | Format identity: id, name, extensions, media type, category. |
| `IImporter` / `IExporter` | The two plug-in contracts. Each declares `Options` (`OptionDefinition`s) that the UI renders generically. `Status` lets a converter be `Ready`, `Preview` or `Planned` (shown greyed out). |
| `OptionDefinition`, `OptionValues` | Typed option metadata (bool/int/number/text/choice/color/font/theme) and a JSON-friendly value bag. |
| `FormatRegistry` | Knows all importers/exporters; resolves source file → target format. |
| `ConversionService` | Runs import → (theme lookup) → export → safe write to disk. Sanitises output names; refuses paths that escape the output folder. |
| `ConversionQueue` | Background queue with bounded parallelism and a `Changed` event for live progress. |
| `ConversionProgress` | Stage + fraction; `Overall` maps to a single 0..1 bar. |

## Themes (`Core/Themes`)

`PdfTheme` is a plain JSON-serialisable object: page geometry, typography, six heading levels, colours, code,
tables, quotes, lists, links, images, header/footer templates (`{page} {pages} {title} {date} …`), cover page and
table of contents. `BuiltInThemes` ships 28 read-only presets in three partial files: the original six, document styles
(GitHub, Swiss, Academic, Tufte, Newsprint, Contract, Handout, Typewriter, Print, Large Print) and editor palettes
(Solarized, Catppuccin, Gruvbox, Rosé Pine Dawn, GitHub Dark, Nord, Dracula, Tokyo Night, Everforest), each built from
the project's published hex values. `IThemeStore` (`FileThemeStore`) keeps user themes
as `<AppData>/AllInOnePdf/themes/<id>.json`, supports duplicate/import/export, and remembers the default theme.

The theme builder edits a `PdfTheme` in memory and re-renders. Two preview paths:

* **Fast**: `IDocumentHtmlRenderer` + CSS generated from the theme (approximate, instant).
* **Exact**: `IPdfRenderer` → `IPdfRasterizer` page images (what the PDF really looks like, ~100-500 ms).

## Rendering (`Core/Rendering`)

* `PdfRenderer` – maps the model to the layout engine's fluent API (`AllInOnePdf.Layout`, below). Pure .NET, in process.
  Honours every theme property: page geometry, typography (incl. soft-hyphen hyphenation and orphan control), heading
  levels with rules, alignment and keep-with-next, colours, code (wrap or shrink/clip, line numbers, highlighting),
  tables (grid/lines/minimal/striped, repeated headers), quotes with attributions, callouts (per-kind colours, labels,
  icons), lists and task checkboxes, links, images, header/footer templates (`{page} {pages} {title} {section} {date}…`
  — `{section}` is resolved per page with the layout engine's dynamic components), cover styles and a contents page with real
  page numbers and dot leaders.
* `ThemedComposer` – the themed drawing code behind `PdfRenderer`, exposed so other renderers draw text exactly
  like the PDF renderer: page setup, header/footer strips, themed block flow into any layout engine container (with the
  available width for its estimates), inlines, contents and cover. The publication renderer is built on it.
* `Core/Highlighting/SyntaxHighlighter` – dependency-free tokenizer for ~20 languages (C#, JS/TS, Python, JSON, YAML,
  XML/HTML, CSS, shell, PowerShell, SQL, Go, Rust, Java, Kotlin, Markdown, TOML, INI, diff…) with Light/Dark/Mono
  palettes. Used by the PDF renderer and by the HTML renderer (`<span class="hl hl-kw|type|str|num|cmt|attr|punct|fn|var|op">`).
* `PdfiumRasterizer` – PDFtoImage (bundled pdfium) renders pages to PNG/JPEG/WebP for previews and thumbnails.
* `DocumentHtmlRenderer` – semantic HTML with `aio-*` classes; all text HTML-encoded, raw HTML dropped by default,
  remote images never referenced (offline).

### Layout engine (`AllInOnePdf.Layout`)

The app's own page layout engine, MIT, on SkiaSharp and HarfBuzzSharp only. Its fluent API (`Document.Create`,
pages, columns, rows, tables, text, images, SVG, dynamic components) is QuestPDF-compatible, so `ThemedComposer` and
`PublicationLayout` describe pages exactly as they did before the switch. Elements answer a measure-and-draw protocol
(`Empty`, `Wrap`, `PartialRender`, `FullRender`); the page loop (`Drawing/PageLayout`) reserves the header and footer,
fills each page with content until it is drawn in full, and runs twice: a measuring pass on a `FreeCanvas` that
records the page count, section pages and captured positions, then a drawing pass on Skia's PDF canvas that uses
them (total pages, contents page numbers, `{section}`). Text is shaped with HarfBuzz per span, broken into lines by
`Text/LineBreaker` (spaces, hyphens, soft hyphens, break-anywhere for code) and drawn as glyph runs; each face is
written into the PDF as a subset holding only the glyphs the document draws (`Drawing/TrueTypeSubsetter`,
`Drawing/FontSubsets`). `tools/AllInOnePdf.LayoutCompare` renders the fidelity corpus (every built-in theme, stress
documents, every Creator template) to page images and diffs them against a reference set. The specification is
`docs/LAYOUT-ENGINE.md`.

## Publishing (`AllInOnePdf.Publishing`)

The Creator's domain, in its own project so the page-builder model stays apart from the conversion model
(`docs/CREATOR.md` is the specification). A `Publication` is sections → rows (column fractions, gutter) → columns →
modules (text in Markdown, heading, image, divider, spacer, quote, callout, code, table, button, contents, page
break), each with a box design (margin, padding, background, border), plus a `PdfTheme` snapshot and image assets.
`ModuleBlocks` maps modules to Core blocks, so the design canvas (`IDocumentHtmlRenderer` + theme CSS), the
`PublicationRenderer` (the layout engine through `ThemedComposer`: real columns, page breaks, header/footer, a contents module
with page numbers) and `PublicationFlattener` (a `Document` for Export as… and the Reader) all read one source.
`PublicationValidator` clamps everything; `BuiltInTemplates` ships starter layouts; `IPublicationStore` keeps
`<data>/publications/<id>.aiopub` packages (a zip: `publication.json`, `assets/<id>`, `thumbnail.png`).

## Importers and exporters

* `Importers/MarkdownImporter` – Markdig (CommonMark + GitHub extensions, alerts, footnotes, math, task lists, YAML
  front matter); embeds local images as resources.
* `Importers/PlainTextImporter` – paragraphs by blank line, optional heading detection.
* `Importers/PdfImporter` + `Importers/Pdf/*` – PdfPig layout analysis: words → lines → blocks in reading order
  (Docstrum + reading-order detection for multi-column pages), running header/footer and printed-TOC removal, headings
  from bookmarks or font-size clusters, lists (bullets, numbers, drawn checkboxes), monospace code with reconstructed
  indentation, ruled/aligned tables, accent-bar quotes and labelled callouts, images, link annotations, hyphen joining;
  cover page folded into the metadata, tables continued over pages merged, wrapped code lines rejoined, struck-through
  and highlighted text from the drawings, footnotes from superscripts plus page-end notes. The renderer leaves
  invisible named destinations the importer reads back (`Pdf/PdfMarkers`): `aio-cover` on the cover and
  `aio-code-<n>-<language>` on each code block.
* `Importers/HtmlImporter` + `Importers/Html/HtmlDocumentConverter` – AngleSharp DOM → model (semantic elements,
  callout classes, task lists, tables with spans, data-URL and local images, footnotes, math); remote images never fetched.
* `Importers/EpubImporter` + `Importers/Epub/*` – container → OPF → spine, each XHTML through the HTML converter, images
  from the package, chapter breaks; zip-bomb and path-traversal guards; DRM detected and refused.
* `Importers/DocxImporter` + `Importers/Docx/*` – Open XML: styles → headings/quotes/code, numbering → nested lists,
  tables with merges, images, hyperlinks, footnotes, page breaks; content-control tags from our exporter round-trip
  callouts and code languages.
* `Importers/XlsxImporter` + `Importers/Xlsx/*` – Excel: a heading plus one table per worksheet (streamed row by row
  with `OpenXmlReader`), cells as Excel displays them (shared/rich strings, number and date formats via
  `XlsxNumberFormat`, booleans, errors, cached formula results or formula text), merged cells as spans, header-row
  detection, hidden sheets/rows/columns skipped, hyperlinks, sheet selection and row cap.
* `Importers/PptxImporter` + `Importers/Pptx/*` – PowerPoint: slides as sections separated by a divider (or page
  breaks, or nothing; title placeholders → headings, "Slide N" for untitled slides, "(cont.)" slides folded into
  the slide before, bold larger paragraphs → headings below the title), body placeholders → bullet/numbered lists nested by indent
  level, text boxes → paragraphs, monospace shapes → code, tables with merges, pictures as resources, optional speaker
  notes callout; shapes read in position order; hidden slides skipped; charts/SmartArt/OLE reported as skipped.
* `Importers/RtfImporter` + `Importers/Rtf/*` – hand-written byte tokenizer: code pages per font charset (incl. CJK
  double-byte) and `\u` escapes, headings from outline level/style or size/bold, Word and WordPad lists, tables with
  merged cells, PNG/JPEG pictures, hyperlink fields, bookmarks, footnotes, monospace → code, page breaks, `\info` metadata.
* `Importers/ImageImporter`, `CsvImporter`, `JsonImporter`, `SourceCodeImporter` + `Importers/Data/*` – pictures
  (SkiaSharp decode, EXIF orientation, transcoding), RFC 4180 CSV → table, pretty-printed JSON and source files →
  highlighted code documents.
* `Exporters/PdfExporter` – theme-driven PDF via `IPdfRenderer`.
* `Exporters/MarkdownExporter` + `Exporters/Markdown/*` – GFM writer with 18 options (front matter, heading style,
  markers, wrapping, tables, images to a side folder / data URLs, reference links…); round-trip stable with the importer.
* `Exporters/HtmlExporter` + `Exporters/Html/ThemeStylesheet` – standalone HTML with a stylesheet generated from the
  theme (print rules included), highlighter spans, optional embedded fonts, cover and contents.
* `Exporters/DocxExporter` + `Exporters/Docx/*` – Word document whose named styles, numbering, table styles, page
  setup, header/footer fields, cover and TOC field derive from the theme.
* `Exporters/XlsxExporter` + `Exporters/Xlsx/*` – every table becomes a worksheet named after its caption or heading:
  header rows and borders styled from the theme's table settings, typed numbers/percentages/booleans/ISO dates,
  merged spans, auto-fit column widths, frozen header, optional filter buttons; documents without tables become one
  "Document" sheet.
* `Exporters/PptxExporter` + `Exporters/Pptx/*` – PowerPoint deck with a master, two layouts and a theme part built
  from the `PdfTheme` (heading/body fonts, colours, background): title slide, one slide per heading section (split at
  H1, H2 or page breaks) with "(cont.)" overflow slides, bullet/numbered lists, code panels, tables with spans,
  pictures, 16:9 or 4:3; output validates against the Open XML schema.
* `Exporters/EpubExporter` + `Exporters/Epub/*` – EPUB 3.3 package (EPUB 2 compatible): chapters split by heading or
  page break, nav + NCX, rendered cover image, theme stylesheet that can defer to the reader's night mode.
* `Exporters/PageImagesExporter` (PNG / JPEG / WebP) – one image per page; rasterises the original PDF when the
  importer kept it (`Document.SourceFile`), otherwise renders through the theme.
* `Exporters/PlainTextExporter` – readable text with underlined/uppercase headings and aligned tables.

Themes imported from JSON pass through `Themes/ThemeValidator` (fills missing sections, assigns ids, clamps numbers,
validates colours).

## Fonts (`Core/Fonts`)

Inter, Literata and JetBrains Mono (SIL OFL) are embedded as static TTF instances (Regular/Medium/SemiBold/Bold +
italics) and registered with the layout engine's `FontManager` on first use, so PDFs are byte-for-byte portable
across OSes. Each PDF embeds subsets of the faces it uses, holding only the glyphs it draws. The UI uses the
variable versions from `UI/wwwroot/fonts`.

## Storage (`Core/Storage`)

`AppPaths` resolves the data folder in this order: `ALLINONEPDF_DATA_DIR`; **portable mode** (a `portable.txt`
marker beside the executable, whose first line may name a relative data folder, or the PortableApps.com
`App/AllInOnePdf` layout with a sibling `Data` folder); otherwise `%APPDATA%\AllInOnePdf` on Windows or
`~/.config/AllInOnePdf` on Linux. The default output folder is `Data/Output` when portable, else
`Documents/AllInOne PDF`. The desktop host also keeps the webview profile in `<data>/browser` (Photino's temporary
files path and the `WEBVIEW2_USER_DATA_FOLDER` variable) and picks up a fixed-version WebView2 runtime from
`WebView2Runtime/` beside the executable. `build/publish.ps1` produces the portable packages: one launcher at the
root (`AllInOnePdfPortable.exe` from `src/AllInOnePdf.Launcher`, a .NET Framework 4.8 exe so it needs nothing
installed, or `AllInOnePdf.sh` on Linux) with the self-contained app in `App\AllInOnePdf` / `app` and the data in
`Data` beside it. `JsonFileStore` does atomic JSON writes. `FileConversionHistory` keeps the last 200 conversions behind a write lock.
Publications live in `<data>/publications`. `IDraftStore` (`FileDraftStore`, `<data>/drafts`) holds the editors'
crash-recovery drafts: the theme builder and the Creator write their unsaved work there a moment after each change
and offer it back when the same item is opened again; saving deletes the draft, and drafts older than 30 days are
cleaned up.

## Hosts

### Desktop (`AllInOnePdf.Desktop`)
Photino.Blazor hosts Blazor inside the OS webview (WebView2 on Windows, WebKitGTK on Linux). No HTTP port is opened;
Blazor runs in-process. `StaticWebAssetsFileProvider` reads the SDK's `*.staticwebassets.runtime.json` manifest so
`_content/...` assets from the UI library and Fluent UI resolve in Debug builds (published builds copy them to
`wwwroot`). `PhotinoHostServices` implements `IHostServices` with native open/save/folder dialogs and shell actions.

**Title bar (Windows).** The app's 48 px `AppHeader` doubles as the window's title bar. Photino's chromeless mode is
not used: on Windows it creates a bare `WS_POPUP` window that cannot be resized, snapped or moved with Win+Arrow, and
it covers the taskbar when maximized. Instead `WindowFrame` subclasses the normal window and answers `WM_NCCALCSIZE`
so the client area also covers the caption, keeping the invisible left, right and bottom resize borders. The flow:
`aio.titleBar` (app.js) watches pointer events on the header → after 3 px of movement on an empty part (or on one of
the 6 px top-edge handles) it calls `AppHeader.BeginWindowDrag` → `IHostServices.BeginWindowDragAsync` →
`WindowFrame.BeginDrag`, which releases the mouse capture and posts `WM_NCLBUTTONDOWN` with `HTCAPTION`/`HTTOP…`, so
Windows runs its own move/size loop (snapping, dragging out of maximized). Caption buttons post `WM_SYSCOMMAND`
(minimize, maximize/restore, close, as Alt+F4 does); `WM_SIZE` raises `IHostServices.WindowStateChanged` so the
Maximize/Restore glyph follows. If a host cannot hand the drag over, the JS moves the window itself through
`MoveWindowByAsync` with per-frame deltas. `Program.cs` reads `UseSystemTitleBar` before the window is created and
follows the preference at runtime (a flag plus `SWP_FRAMECHANGED`). Other platforms, and the browser host
(`SupportsWindowControls == false`), keep the system title bar.

### Web (`AllInOnePdf.Web`)
Blazor Server. Binds to `http://127.0.0.1:5180` unless `Urls`/`ASPNETCORE_URLS` is set. Used for development
previews (visible in a browser) and for running on a Linux server for a trusted network. `BrowserHostServices`
replaces native dialogs with uploads/downloads.

### `IHostServices`
The UI never touches host APIs directly. It asks `IHostServices` for dialogs, saving, revealing files and opening
URLs, and checks `SupportsNativeDialogs` / `SupportsShellOpen` to adapt (e.g. show `FluentInputFile` upload on web,
"Browse…" on desktop).

## Security posture

* **Offline by design**: no network calls, no telemetry, no remote images, no runtime downloads.
* **No external processes** for conversion: no LibreOffice, no headless browser. Everything is in-process managed
  code plus the pdfium, Skia and HarfBuzz natives shipped inside NuGet packages.
* **Untrusted input**: HTML in sources is never rendered raw; links with `javascript:`/`data:` schemes are neutralised;
  output file names are sanitised and confined to the chosen output folder; image embedding is size-capped.
* **Web host** binds to loopback by default, sets `nosniff`/`DENY`/`no-referrer` headers, a Content-Security-Policy
  that only allows this origin (plus `data:`/`blob:` images), and restricts `AllowedHosts`.
* **Desktop host** serves static assets only from inside the content roots (no `..` escapes), writes crash logs to
  `<data>/logs`, and grants the webview no browser permissions.

## Extending

* **New importer**: implement `IImporter`, register in `Core/ServiceCollectionExtensions.cs`. Declare options with
  `OptionDefinition.*` helpers; the Convert page renders them automatically.
* **New exporter**: implement `IExporter` the same way. Return one or more `OutputFile`s.
* **New theme property**: add it to `PdfTheme` (with a default), honour it in `ThemedComposer` (so `PdfRenderer`
  and the publication renderer both get it), expose it in the theme builder's settings panel, and add the CSS
  equivalent to the fast preview.
* **New layout feature** (an element or a text rule the renderers need): add it to `AllInOnePdf.Layout` following
  `docs/LAYOUT-ENGINE.md`, with tests in `tests/AllInOnePdf.Layout.Tests`, and check the corpus with
  `tools/AllInOnePdf.LayoutCompare` so existing documents still paginate the same.

## Licensing

MIT for this repository. Third-party: Fluent UI Blazor (MIT), Photino (Apache-2.0), Markdig (BSD-2), PdfPig
(Apache-2.0), PDFtoImage (MIT) + pdfium (BSD-3), SkiaSharp (MIT), HarfBuzzSharp (MIT; HarfBuzz itself Old MIT),
AngleSharp (MIT), DocumentFormat.OpenXml (MIT), fonts (SIL OFL 1.1). Every dependency is permissive (MIT, Apache-2.0,
BSD or OFL); the PDF layout engine is this repository's own code. `THIRD-PARTY-NOTICES.md` carries every licence
text and ships in the packages. Keep every new dependency permissive and list it in `Directory.Packages.props`.
