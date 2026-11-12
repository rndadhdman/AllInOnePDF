# Status and handoff (6 November 2026)

Where the project stands, what is in flight, what still needs testing, and what comes next. Written for whoever
picks the work up (a person or a fresh Claude session). Companion documents: `ARCHITECTURE.md` (how the code is
organised), `CREATOR.md` (the page builder), `LAYOUT-ENGINE.md` (the PDF engine specification), `AGENTS.md`
(team rules), `CHANGELOG.md`.

**About the history.** The public repository starts at version 1.2.1 with a single commit (12 November 2026).
Earlier development, 225 commits and the releases 1.0.0 to 1.1.1, happened in a private repository. Commit hashes,
tags and workflow run numbers quoted in this file and in `LAYOUT-ENGINE.md` refer to that private history and do not
exist in the public one; the `ac57dad` checkout needed to regenerate the fidelity reference set is available to the
maintainer only.

## 1. Where we are

* **The app is feature-complete for a 1.0**: Convert (every format through one document model), Read, Themes
  (28 built-ins, a Divi-style builder), Create (the Creator page builder with templates, drag and drop, inspector,
  exact preview, exports, `.aiopub` packages). Autosave/recovery in both builders, Help page, About with
  diagnostics, shipped third-party notices, a tag-driven release workflow, CI green on Windows and Linux.
* **`main` renders PDFs with the app's own engine** (`src/AllInOnePdf.Layout`, merged as `4291860` on 6 November
  2026 after two fidelity rounds; section 2 has the numbers). The renderers (`Core/Rendering/ThemedComposer.cs`,
  `PdfRenderer.cs`, `Publishing/Rendering/PublicationLayout.cs`, `PublicationRenderer.cs`) use its fluent API, which
  mirrors the QuestPDF subset they were written against.
* **QuestPDF is gone** (merge `245b300`: package, notices, About text, docs; `QuestPdfRenderer` is now
  `PdfRenderer`). 2026.9.1 was under the QuestPDF Community License 3.0 (not MIT: public-sector bodies and publicly
  traded companies were excluded), which is why the engine was written. Every dependency is now MIT, Apache-2.0, BSD
  or OFL, and the licence question that hung over the public/private repo decision no longer exists. The last commit
  that still built with QuestPDF is `ac57dad`; the fidelity reference set can only be regenerated from a checkout of
  that commit or earlier. CI is green on Windows and Linux with the engine (run 42273565486).
* **Round 3 (7 November 2026, after v1.0.1, unreleased)**, from David's list "title bar into the program, About links,
  border width glitch, border radius, z-indexing, free flow mode": the Windows window draws its own title bar
  (`Desktop/WindowFrame.cs` subclasses the window procedure and removes the caption with `WM_NCCALCSIZE`; drag and
  top-edge resize are handed to the OS; Settings › Appearance › Use the system title bar switches back live); About
  links to GitHub and therandomadmin.com; the engine gained `TranslateX/Y`, `ZIndex` (deferred painting per page),
  `Freeform` (absolute placement with clipping and overflow reporting) and `TrackedBoxes`; every Creator box has
  Divi-style Position (offset X/Y, Z level) in the canvas and the PDF; sections have a layout, Structure or **Free
  flow** (a page-sized canvas with modules at x/y/w/h/z: drag, resize, snap, nudge, Z-order shortcuts, overflow
  badge), with a publication-level "Structure | Free flow" switch that offers to convert existing sections through
  `SectionLayoutConverter` using the render's boxes; a Poster template; the Fluent slider/number-field echo loop (the
  "border width glitches to death" bug) is fixed with shared native `RangeInput`/`NumberInput` in the Creator, the
  theme builder and Convert options. Corpus unchanged (0 of 398 pages moved; Poster is a new page without reference).

## 2. The engine replacement, done

### What exists

* `src/AllInOnePdf.Layout`: QuestPDF-compatible fluent API on SkiaSharp + HarfBuzzSharp (both MIT).
  `Infrastructure/*` is the contract; `Fluent/*` the API; `Elements/*` containers, tables, images, SVG, lines;
  `Text/*` fonts, shaping, line breaking, `TextBlock`; `Drawing/*` page loop, Skia canvas, PDF writer, TrueType
  subsetter (`TrueTypeSubsetter`, `FontSubsets`: the measuring pass collects the glyphs each face draws, the drawing
  pass embeds fonts cut down to them with glyph ids unchanged; CFF and variable fonts are embedded whole).
* `tools/AllInOnePdf.LayoutCompare`: renders the corpus (87 documents, 398 pages at 72 dpi: every theme × two
  samples, four stress documents × six themes, every Creator template) and compares it pixel by pixel with a
  reference set rendered by QuestPDF. `layoutcompare reference <dir>` writes the reference (it needs a QuestPDF build:
  a checkout of `ac57dad` or earlier); `layoutcompare compare <refDir> <outDir>` writes current pages, red diff
  images, the PDFs, `summary.txt` and `summary.json`.
* The reference set lives in **`.corpus/reference`** at the repo root (git-ignored). If it is lost, regenerate it
  from a clone at `ac57dad`: `dotnet run --project tools/AllInOnePdf.LayoutCompare -c Release -- reference <dir>`.

### First measurement on `engine/layout` (commit `ec55106`)

Tests on the new engine: Layout 170/170, Publishing 54/57, Core 835/843. Corpus: mean 3.25 % differing pixels,
357 of 401 pages over the 0.5 % threshold, five documents with a different page count.

| Area | Symptom | Where to look |
|------|---------|---------------|
| PDF text layer | Core `PdfImportRoundTripTests` (manuscript theme) and three `RendererTests`/`ThemedComposerTests` fail on text extraction: words or spaces missing from what PdfPig reads back | `Drawing/SkiaCanvas.DrawGlyphRun` (how glyph runs are written: spaces must be real glyphs, ToUnicode must map every glyph; compare `SKTextBlobBuilder.AllocatePositionedRun` output with QuestPDF's by extracting text from both PDFs) and `Text/TextShaper` (clusters for spaces, soft hyphens) |
| Line metrics | `stress-typography-*` pages differ 7–16 % (justified, hyphenated running text), headings and paragraphs drift down the page | `Text/TextBlock`: baseline and line-height model. The text agent used Skia's proportional ascent/descent; the spec's centring formula is behind `TextBlock.HalfLeading`. Calibrate against `.corpus/reference/stress-typography-clean-p001.png` line positions; also letter spacing after the last glyph, justification distribution, soft-hyphen width |
| Page counts | `sample-academic` 4 vs 5, `welcome-gruvbox-light` and `welcome-github-dark` 2 vs 3 (ours shorter); `stress-table-clean` 11 vs 10 and `stress-table-manuscript` 27 vs 25 (ours longer) | Text lines per page (metrics above) for the first three; table row heights/cell padding/borders and the row-never-splits rule for the tables (QuestPDF splits a cell's content across pages; see `Elements/Table.cs`) |
| Tables | `stress-table-*` 8 % per page | Row height, header repeat spacing, border placement (`Border` is drawn centred on the edge in QuestPDF), cell text baseline |
| Code blocks | `stress-code-academic` 10 % | Mono text metrics, `BreakAnywhere` wrapping, line-number gutter; `ClampLines` ellipsis |
| Images | `ImagePagesExportTests.Background_colour_and_transparency` fails | Page background or transparent page handling in `Drawing/PdfDocumentWriter`/`PageLayout` (PageColor fill vs none) |
| Alignment | `RenderingFidelityTests.Quote_attribution_is_right_aligned` fails | `AlignRight` on a text item inside a column (shrink-wrap vs stretch), or `TextDescriptor.AlignRight` |
| Placeholders/links | Publishing `Missing_image_draws_a_placeholder_only_when_asked`, `Svg_and_bitmap_assets_render_and_buttons_link` | Text extraction again ("Image" label) and `/URI` link annotations from `DrawHyperlink` |

### After round 1 (`engine/layout` at `4da788a`)

Both fidelity branches merged. Every test passes (Layout 176, Publishing 57, Core 843); all 398 page counts match
the reference; corpus mean 0.508 %; 82 pages over 0.5 %; 23 pages over 3 %, all of them `stress-code-*` in the
handout, manuscript and academic themes (3 to 5 %: code-block line layout). Root causes fixed in round 1: spaces
were never written as glyphs (text extraction), proportional instead of half-leading line model, ligatures formed
where QuestPDF forms none, letter-spacing placement, 8-digit hex colours read as RRGGBBAA, table rows not splitting
across pages, border placement, EnsureSpace not moving a short partial. Open: code-block pages, sub-pixel glyph
placement noise (SkiaSharp writes `Td` per kerning point where QuestPDF's Skia writes `TJ`; identical positions,
different rasterisation), and **PDF size: SkiaSharp embeds whole fonts (about 270 KB each) where QuestPDF subsets
them**, so PDFs are roughly ten times larger until a TrueType subsetter is in place (round 2).

### After round 2 (`engine/layout` at `63bab7e`, merged into `main` as `4291860`)

Every test passes (Layout 188, Publishing 57, Core 843); all 398 page counts match; corpus mean **0.252 %**; 58 pages
over 0.5 %; **2 pages over 3 %** (`stress-code-academic` p006 4.73 %, p007 4.53 %); the corpus PDFs total 3.99 MB
against the reference's 4.62 MB. Fixed in round 2: break-anywhere follows Skia's paragraph (spaces hang; a break
before every character inside a span but not at the span's first character; no break before closing punctuation or
`/` after a space), justification gives every word gap the same width whatever font its spaces are in, glyph advances
are measured at the size truncated to 1/64 pt as Skia's scaler does (the 0.2 pt attribution drift), and font
subsetting. What remains is rasterisation, not layout: on the two academic code pages the glyph positions match to
0.00002 pt, but our text origin carries a constant float error (219.99994 pt) while QuestPDF's drifts from page to page
(exactly 220 on those two pages), and PDFium anti-aliases the two differently; `stress-typography-handout` p003–p005
(2.3–2.9 %) differ in how SkiaSharp writes positioned runs (`Td`/`Tj` per word where QuestPDF's Skia writes `TJ`);
the large-print attribution sits 0.068 pt off. None of it is visible; none of it is worth copying QuestPDF's float
drift for. Since the QA fixes of 6 November `template-report-p001` differs from the reference by 2.2 % on purpose:
the Report template's title page no longer prints a page number, and the reference was rendered from the old
template (mean 0.258 %, 59 pages over 0.5 %, the same two over 3 %).

### How to re-run the fidelity loop

1. Build, then run `dotnet run --project tools/AllInOnePdf.LayoutCompare -c Release -- compare .corpus/reference .corpus/out`.
2. Fix one area at a time (text layer first: it also unblocks the round-trip tests; then line metrics, which moves most
   pages; then tables; then the rest). Re-run the compare after each change; look at `.corpus/out/*.diff.png` (red
   = differing pixels) next to the reference PNG.
3. Keep every change inside `src/AllInOnePdf.Layout` unless a renderer genuinely depended on a QuestPDF quirk.
4. Agents: one per area on separate files (text layer/metrics in `Text/*` + `Drawing/SkiaCanvas.cs`; tables in
   `Elements/Table*.cs`; page/alignment in `Elements/*` + `Drawing/PageLayout.cs`), each handed the compare tool and
   the reference folder, each reporting the summary numbers before and after.

## 3. Targets for the merge into `main` (met, with one noted exception)

1. Every corpus document has the same page count as the reference: **398 of 398**.
2. No page over 3 % differing pixels; corpus mean under 0.5 %: **mean 0.252 %; two pages at 4.5–4.7 % accepted** as
   rasterisation noise (section 2 explains why).
3. All test projects green: **Core 843, Publishing 57, Layout 188**.
4. Then: remove `QuestPDF` from `Directory.Packages.props`, `THIRD-PARTY-NOTICES.md` (and the About/README
   licensing text: the app becomes all-MIT/Apache/BSD/OFL), update `ARCHITECTURE.md` (Rendering section) and
   `CHANGELOG.md`, rename `QuestPdfRenderer` to `PdfRenderer` (used by DI, tests and the compare tool): **done**
   (`245b300`). Packages rebuilt with `build/publish.ps1`: `AllInOnePdfPortable-win-x64.zip` 59.6 MB,
   `AllInOnePdf-linux-x64.tar.gz` 58.9 MB (HarfBuzzSharp natives in, no QuestPDF binaries).

## 4. What still needs testing by a person

* The desktop app (Photino) with the new engine on Windows, and the Linux build on a real desktop (never run outside
  Docker). Fonts: the engine's system-font fallback uses `SKFontManager`; check a document with Greek, Cyrillic and
  emoji on both OSes.
* Exported Word, Excel and PowerPoint files in Microsoft Office (LibreOffice already opens all 27 from the samples and
  templates; `build/check-office.ps1` repeats that check).
* Large inputs: a 1,000-page PDF import, a 50 MB image, a Creator project with many images; memory and time.
* Code signing of the Windows package and an installer (SmartScreen will block the unsigned exe).
* ~~Whether the repo goes public~~: public since 12 November 2026, as a new repository with one starting commit; the
  full history stays private (see "About the history" above).
* A PDF from each theme opened in Acrobat Reader, Edge and a Linux viewer: the engine's PDFs are new output (subset
  TrueType fonts as CIDFontType2 with ToUnicode, link annotations, outlines), and only pdfium and PdfPig have read them.

## 5. Ideas for what comes next

* **Engine**: full UAX #14 line breaking; `ImageRasterDpi` resampling; right-to-left text; `Unconstrained`/`Rotate`
  if a theme ever needs them; a golden-image test in `Layout.Tests` for a handful of corpus pages so regressions are
  caught in CI; exact text origins (accumulate translations in double so a box at 220 pt is written as 220, not
  219.99994); subsetting CFF fonts (system fonts on some machines) so those PDFs shrink too.
* **Product**: UI test automation (bUnit + Playwright), Linux packaging (AppImage/.deb), resource limits for huge
  inputs, "check for updates" against GitHub releases, more Creator templates, text flowing between columns.
* **Process**: `v1.0.0` was cut on 6 November 2026 (tag at `333ba90`; release workflow run 42280316241 published
  the Windows zip, the Linux tarball and `SHA256SUMS.txt`), and `v1.0.1` the same day (tag at `9c3b2c4`, run
  42327797576) with the Creator page tracking, the gallery drafts and every finding of the QA pass; `v1.1.0` on
  7 November 2026 (tag at `36faefe`, run 42412925668) with round 3: the title bar, Position, Free flow, the Poster
  template and the slider fix. The next release is a new dated section in `CHANGELOG.md`, the version in
  `Directory.Build.props`, and a `v*` tag.
* **Packaging (7 November 2026, after v1.1.0)**: every release now carries an x64 MSI (`build/msi`, WiX 5 as a
  repo-local dotnet tool, UpgradeCode `07C238E8-5534-4E05-BB19-7F9216748287`, per-machine, ICE60 suppressed for the
  bundled fonts), a `.deb` and an `.rpm` (`build/linux`, dpkg-deb in Ubuntu 24.04 and rpmbuild in Fedora 41 through
  Docker, `/opt/allinonepdf` + `/usr/bin/allinonepdf`, app id `com.therandomadmin.AllInOnePdf`), a Flatpak bundle
  (`build/flatpak`, flatpak-builder in a Docker image with the GNOME 51 runtime cached, `--privileged`), and portable
  zips for both platforms next to the Linux tar.gz. `build/publish-install.ps1` makes the plain publish the installers
  take (no `portable.txt`). The release workflow runs all of it; a manual run with "publish" off is a dry run that
  uploads the packages as artifacts. None of the packages is signed. Checked by installing in containers and by an
  administrative MSI extract only; David installs the MSI and the Flatpak by hand (see `docs/INSTALL.md`).
  **`v1.1.1`** (tag at `9755853`, release run 42422422994) is the first release that ships all seven packages.
* **Quality pass (7 November 2026, released as `v1.2.1`, tag at `e4483ad`, run 42427788516; `v1.2.0` was tagged at
  the wrong commit after the repository on `P:` lost freshly written objects and a stale `refs/heads/main.lock`, its
  run was cancelled and the tag left in place, never released)**: David's three instruction profiles live in
  `docs/quality/` (`anti_ai_writing.json`, `anti_ai_web_ui.json`, `anti_ai_coding.json`) and are mandatory for every
  change (`docs/AGENTS.md`). Five Opus agents, one per tab, applied them: `.aio-page` lost its 1400 px cap and every
  page fills a maximized window (Settings in two columns and Help with a side contents list via container queries,
  Convert rows as aligned columns from 1040 px, History as a sortable table, Themes six to eight cards per row with a
  tag select, the Creator template strip across the width, the theme builder with a section-list column from 1600 px);
  the Reader's Open page is Office-style (drop zone, Recent list, File types rows that open the typed dialog on the
  desktop, a Close button on the toolbar); about 225 user-visible strings were rewritten to say what things do.
  Verified in the web host at 1000/1400/1920/2560 px by the agents and by the director at 1920/2560; the desktop
  window was not inspected visually (David: maximize it and look). Tests: Core 1027, Publishing 103, Layout 204;
  corpus unchanged (0 of 399 pages).

## 6. Session mechanics worth knowing

* Git worktrees under `P:\` fail git's ownership check and changing the global git config was refused; agents work in
  clones on `C:` made from a bundle (`git bundle create` → `git clone <bundle>`), and their branches are fetched back
  with `git fetch <clone> <branch>:<branch>`.
* Build/test both test projects one after the other (`dotnet test` per project); the queue timing test dislikes a
  second test host.
* Set `ALLINONEPDF_DATA_DIR` to a scratch folder and `ASPNETCORE_ENVIRONMENT=Development` when running the web host
  for checks; stop it before building (it locks the DLLs).
