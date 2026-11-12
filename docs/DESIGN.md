# Design language

The app should feel like Microsoft Intune / the Azure portal: calm, dense-but-airy admin UI built on **Fluent 2**,
with first-class dark mode. Reading surfaces are tuned to be easy on the eyes. The theme builder should feel like
building a WordPress theme and then fine-tuning it in Divi 5: pick a preset, see it live, click what you want to
change, change it, watch it update.

## Foundations

| Token | Value | Notes |
|-------|-------|-------|
| UI font | Segoe UI Variable → Inter → system-ui | Fluent default on Windows; Inter is bundled for Linux parity. |
| Reading font | Literata (serif), Inter (sans), JetBrains Mono (code) | Bundled, variable, OFL. |
| Accent | Fluent brand blue `#0F6CBD` (light) / `#479EF5` (dark) | Via `FluentDesignTheme OfficeColor.Default`. Users may change accent in Settings. |
| Neutral layers | Fluent `--neutral-layer-1..4`, `--neutral-stroke-*`, `--neutral-foreground-*` | Never hard-code greys; use tokens so dark mode works for free. |
| Spacing | 4 px grid: 4, 8, 12, 16, 24, 32, 48 | Page padding 24 px. |
| Radius | 4 px controls, 8 px cards, 12 px large surfaces | `--aio-radius-sm/md/lg` |
| Elevation | Fluent shadows 2/8/16 | `--aio-shadow-2/8/16`; cards use 2, flyouts 8, dialogs 16. |
| Type scale | Fluent `Typography`: H2 28/36 page titles, H4 20/28 section titles, Body 14/20, Caption 12/16 | Use `FluentLabel Typo=...`. |
| Motion | 150–250 ms ease-out; respect `prefers-reduced-motion` | Subtle. Never bounce. |

## Shell (Agent A)

* **Header** 48 px: product mark + name left, global actions right (colour mode menu, settings). Flat, `--neutral-layer-2`.
* **Left nav** 220 px, collapsible to icons: Reader, Create, Convert, Themes, History, Settings. Active item gets the
  accent indicator bar, exactly like Intune. Keyboard navigable.
* **Content** scrolls independently; pages start with a **page header**: H2 title, one-line description (max 80ch),
  and a **command bar** at the right edge of the page (primary action as `Appearance.Accent`, the rest
  `Outline`/`Stealth`).
* **Layout: pages fill the window.** `.aio-page` has no width cap. Running text keeps a measure (page description
  80ch, setting descriptions 64ch, Help prose 82ch, reading 60 to 75ch); grids, lists and tables take more columns as
  the window widens. Settings: section list on the left (180 px), cards in one column, two columns once the layout is
  1440 px wide. Help: contents list above (narrow) or on the left from 1000 px, and from 1560 px the guide sections in a
  50rem column with the reference sections (shortcuts, formats, data, problems) filling the rest; shortcut groups and
  topics tile inside a wide card. Breakpoints are container queries on the page, so the navigation state counts.
  Empty states stay centred as one unit.
* **Dark mode**: `FluentDesignTheme` with System/Light/Dark, persisted. Every custom surface must use tokens or the
  reader palettes. Check contrast ≥ 4.5:1 for text in both modes.
* **Density**: optional compact mode (reduced paddings) stored in preferences.
* **Feedback**: `FluentToast` for results, `FluentMessageBar` for inline warnings, `FluentDialog` for confirmations,
  `FluentProgress`/`FluentProgressRing` for work in progress. Empty states use `EmptyState`.

## Convert workspace (Agent B)

The home page. Zero-state is a large, friendly **drop zone** ("Drop files here or Browse") spanning the content
area. After files are added it becomes a compact bar and the **file list** takes over:

* Each file row/card: type icon, name, size, detected format, **target format** select (PDF first, then others;
  planned formats shown disabled with "coming soon"), status, actions (options, remove).
* **Options drawer/panel** (`FluentPanel` or an inline right pane): renders the exporter/importer
  `OptionDefinition`s generically — Boolean → `FluentSwitch`, Integer/Number → `FluentNumberField` or
  `FluentSlider`, Choice → `FluentSelect`/`FluentRadioGroup`, Color → colour swatch + text, Theme → theme picker
  with small previews, Text → `FluentTextField`. Group by `Group`; hide `Advanced` behind "Show advanced".
* **Output**: folder picker (desktop) or "download" mode (web), "save next to source" toggle, overwrite policy.
* **Convert all** primary action; queue shows per-file progress bars and a global summary; results offer Open,
  Reveal in folder, Read in Reader, Convert again.
* History page lists `IConversionHistory` entries with filters and "repeat with same options".

## Reader (Agent C)

Reading is the app's second purpose, so the Reader must be genuinely pleasant:

* Column measure **60–75 ch** (default 68), font **18–20 px** serif, line-height **1.6–1.8**, paragraph spacing
  ~1 em, headings with generous top margin. No pure black on pure white.
* Four palettes: **Paper** (warm off-white), **Sepia**, **Dusk** (soft dark grey, default in dark mode) and **Night**
  (near-black for OLED). Defined as `.aio-reader-*` classes in `app.css`.
* **Reading toolbar** (collapses when idle): font family (Literata/Inter/JetBrains Mono/system), size, line height,
  width, justify, palette, focus mode (hides chrome), table of contents toggle.
* **Outline** sidebar from `Document.Headings` with scroll-spy; **progress** indicator; remembers position per file.
* Two modes: **Reflow** (HTML via `IDocumentHtmlRenderer`) and **Pages** (PDF page images via `IPdfRasterizer`,
  with zoom/fit-width, for files that are PDFs or when the user wants print layout).
* Open from: the Convert page ("Read"), a native Open dialog (desktop), drag-drop, or `FluentInputFile` (web).
* Keyboard: `j/k` or arrows scroll, `+/-` size, `t` toggle outline, `f` focus mode, `Esc` exits focus.

## Themes gallery and builder (Agent D)

**Gallery** (`/themes`): cards with a live thumbnail of page 1 (rasterised at low DPI, cached), name, description,
tags, "Built-in" badge; actions: Use as default, Edit (built-ins → Duplicate & edit), Duplicate, Export, Delete,
Import JSON, New from scratch. Filter by tag; search by name.

**Builder** (`/themes/{id}`) — the Divi feel:

* Layout: **preview** on the left (fills available space), **settings panel** on the right (360 px), **top bar**
  with theme name (editable), dirty dot, Undo/Redo, preview mode toggle (Fast HTML / Exact PDF pages), sample
  document picker (built-in sample or the currently open document), Save / Save as / Export / Reset.
* Settings panel groups (accordion, one open at a time, searchable): **Page** · **Typography** · **Headings**
  (H1–H6 sub-tabs) · **Colours** · **Code** · **Tables** · **Quotes & callouts** · **Lists** · **Links** ·
  **Images** · **Header & footer** · **Cover** · **Table of contents**. Every control is bound to `PdfTheme` and
  re-renders the preview with a ~250 ms debounce.
* **Click-to-select**: clicking an element in the fast preview (heading, paragraph, code block, table…) highlights it
  with an accent outline and opens the matching settings group. Hovering shows a subtle label ("Heading 2").
* **Undo/redo** stack of theme snapshots (JSON); Ctrl+Z / Ctrl+Y.
* **Presets and variants**: "Start from…" dropdown applies a built-in as a base while keeping the name.
* Controls: `FluentSlider` + numeric input for sizes/spacings (with units pt/mm), `FluentSelect` for enums, colour
  swatches with a text field (hex) and a palette of theme colours, `FluentSwitch` for toggles, font picker listing
  bundled fonts first (with a "portable" badge) then system fonts.
* Validation: warn when contrast is poor, when margins exceed the page, when a system font is chosen.

## Creator (publishing)

The Creator (`/create`) is where the app becomes a publisher: a Divi-style builder for PDFs, specified in
`CREATOR.md`. It must feel like the theme builder's sibling, not a separate app:

* **Gallery**: "Start from a template" strip (Blank, Newsletter, Flyer, Report, Menu, Resume, Invoice) with
  rendered thumbnails, then "Your publications" cards (thumbnail, name, theme, modified; Open · Duplicate · Export
  package · Delete). Import `.aiopub` packages.
* **Builder** (`/create/{id}`): the same chrome as the theme builder (top bar with name, dirty dot, Undo/Redo,
  Design | Preview toggle, zoom, Theme…, Export PDF as the accent action, a More menu, Save). Left panel 280 px:
  **Structure** tree and **Insert** tiles. Centre: the **design canvas**, a continuous sheet in the page's width
  where sections stack, rows are column grids and modules render with the theme's own CSS; hovering shows a
  floating toolbar (move · settings · duplicate · delete), "+" affordances add rows and sections, drag and drop
  moves modules, rows and sections with an insertion line. Right panel 360 px: the **Inspector** with Content and
  Design tabs for the selection; nothing selected shows the document settings (metadata, theme, page, header and
  footer). **Preview** mode shows the real PDF pages.
* Every change is one undo step (typing coalesces); Delete, Ctrl+D, Ctrl+Z/Y, Ctrl+S, Esc and Alt+↑/↓ work on the
  selection; everything on the canvas is focusable and labelled; structural changes are announced.
* Below 1100 px both side panels become drawers, as in the theme builder.

## Accessibility

Keyboard reachable everything, visible focus rings (Fluent default), labels on every control, `aria-live` for
progress and toasts, respects reduced motion, text contrast ≥ 4.5:1 in both modes, minimum hit target 32 px.

## Do / Don't

* Do use Fluent components and tokens before writing custom CSS. Don't restyle Fluent internals.
* Do keep pages fast: virtualise long lists, debounce previews, run work off the UI thread.
* Do write component CSS in `.razor.css` (isolation). Don't add global rules outside `app.css` tokens.
* Don't introduce new JS libraries; small helpers go in `wwwroot/js/app.js`.
* Don't block on native dialogs from render; call `IHostServices` from event handlers.
