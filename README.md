# AllInOne PDF

AllInOne PDF converts documents to and from PDF, shows them in a reading view, styles PDFs with themes you can edit,
and lays out publications in a page builder. It runs on **Windows and Linux** as a desktop app, and the same interface
runs as a local web app. It is written in .NET only and works offline: PDFs are laid out by the app's own engine,
without a browser engine or an office suite, and nothing is sent over the network.

Releases and their notes are on the [Releases page](https://github.com/rndadhdman/AllInOnePDF/releases) and in
[`CHANGELOG.md`](CHANGELOG.md).

## Features

The app has five tabs: Reader, Create, Convert, Themes and History, plus Settings and Help.

* **Reader**: a reading view that reflows the text, with Paper, Sepia, Dusk and Night palettes, adjustable font,
  size, line height and line width, an outline of the headings that follows the scroll position, focus mode and
  keyboard shortcuts. The Pages view shows the PDF pages Convert would make.
* **Create**: a page builder for PDFs. A publication is a stack of sections; a section holds rows split into
  columns, and columns hold modules: text (Markdown), heading, image, button, quote, callout, table, code, divider,
  spacer, contents and page break. A section uses Structure (rows and columns that flow onto the next page) or Free
  flow (modules placed anywhere on a canvas). Templates to start from (newsletter, flyer, report, menu, résumé,
  invoice), undo and redo, a page-by-page preview of the real PDF, export to PDF or any other output format, and
  publications saved as `.aiopub` packages. The look comes from a theme in the gallery; page size, margins, header
  and footer are set per publication.
* **Convert**: add files, choose a target format and options per file, and convert them in a queue with progress,
  cancel and retry. History lists past conversions and converts them again with the same options. The desktop app
  saves to a folder or next to the source file; the web app downloads the results.
* **Themes**: 28 built-in PDF themes. Document styles (Clean, Manuscript, Technical, Compact, GitHub, Swiss, Academic,
  Tufte, Newsprint, Contract, Handout, Typewriter, Print, Large Print), tinted papers (Sepia, Solarized Light,
  Catppuccin Latte, Gruvbox Light, Rosé Pine Dawn) and dark screens (Night, GitHub Dark, Solarized Dark, Nord, Dracula,
  Catppuccin Mocha, Gruvbox Dark, Tokyo Night, Everforest). The theme builder edits a copy: page, typography, six
  heading levels, colours, code, tables, quotes, callouts, lists, links, images, header and footer templates, cover
  page and table of contents. It has a fast HTML preview and an exact PDF preview, opens an element's settings when you
  click it in the preview, and has undo and redo and JSON import and export.
* **Markdown → PDF**: CommonMark + GitHub extensions (tables, task lists, alerts, footnotes, math), syntax-highlighted
  code for 20+ languages, numbered headings, keep-with-next, dot-leader contents, `{page}`/`{pages}`/`{section}`
  header and footer tokens, cover pages.
* **PDF → Markdown / text**: layout analysis with PdfPig: headings from bookmarks or font statistics, paragraphs,
  nested and task lists, code blocks with indentation, ruled and aligned tables, callouts, images, links; running
  headers/footers and printed contents pages removed; page ranges; encrypted PDFs with a password.
* **HTML, Word, EPUB, images and data**: HTML import/export (standalone themed HTML), Word import/export with
  theme-derived styles, EPUB 3 export (chapters, nav, cover image, optional embedded fonts) and import, PDF → PNG /
  JPEG / WebP page images, images → PDF, CSV → tables, JSON and source code → highlighted code documents.
* **Excel, PowerPoint and RTF**: Excel workbooks → one table per sheet with cells as displayed (number and date
  formats, merged cells, header detection) and tables → theme-styled worksheets with typed values and frozen headers;
  PowerPoint decks → one section per slide (titles, bullets, tables, pictures, optional speaker notes) and documents →
  decks with one slide per heading section in the theme's fonts and colours; RTF → headings, lists, tables, pictures
  and links through a hand-written parser. All with the Open XML SDK, no Office installation needed.
* **Portable fonts**: Inter, Literata and JetBrains Mono are bundled so output is identical on every OS.

## Formats today

Every source can become every target because all converters share one document model.

| Source ↓ / Target → | PDF | Markdown | HTML | Word | Excel | PowerPoint | EPUB | Text | PNG / JPEG / WebP |
|---------------------|:---:|:--------:|:----:|:----:|:-----:|:----------:|:----:|:----:|:-----------------:|
| Markdown            | ✓   | –        | ✓    | ✓    | ✓     | ✓          | ✓    | ✓    | ✓ (pages)         |
| PDF                 | –   | ✓        | ✓    | ✓    | ✓     | ✓          | ✓    | ✓    | ✓ (original pages)|
| HTML                | ✓   | ✓        | –    | ✓    | ✓     | ✓          | ✓    | ✓    | ✓                 |
| Word (.docx)        | ✓   | ✓        | ✓    | –    | ✓     | ✓          | ✓    | ✓    | ✓                 |
| Excel (.xlsx)       | ✓   | ✓        | ✓    | ✓    | –     | ✓          | ✓    | ✓    | ✓                 |
| PowerPoint (.pptx)  | ✓   | ✓        | ✓    | ✓    | ✓     | –          | ✓    | ✓    | ✓                 |
| Rich Text (.rtf)    | ✓   | ✓        | ✓    | ✓    | ✓     | ✓          | ✓    | ✓    | ✓                 |
| EPUB                | ✓   | ✓        | ✓    | ✓    | ✓     | ✓          | –    | ✓    | ✓                 |
| Plain text          | ✓   | ✓        | ✓    | ✓    | ✓     | ✓          | ✓    | –    | ✓                 |
| Images (PNG/JPEG/WebP/GIF/BMP) | ✓ | ✓ | ✓  | ✓    | ✓     | ✓          | ✓    | ✓    | ✓                 |
| CSV, JSON, source code | ✓ | ✓      | ✓    | ✓    | ✓     | ✓          | ✓    | ✓    | ✓                 |

Not yet: RTF export, TIFF, legacy binary Office files (.doc, .xls, .ppt). Scanned PDFs are not turned into text: the app has no
text recognition (OCR), because an OCR engine would need native code and the app is .NET only.

## Quick start

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download). Windows needs the WebView2 runtime (present on
Windows 11); Linux needs `libwebkit2gtk-4.1-0` (Ubuntu/Debian: `sudo apt install libwebkit2gtk-4.1-0 libgtk-3-0`).

```bash
git clone https://github.com/rndadhdman/AllInOnePDF.git
cd AllInOnePDF
dotnet run --project src/AllInOnePdf.Desktop
```

Web mode (same UI in your browser, binds to localhost only):

```bash
dotnet run --project src/AllInOnePdf.Web --launch-profile http
# → http://127.0.0.1:5180
```

## Install

Every release on the [Releases page](https://github.com/rndadhdman/AllInOnePDF/releases) ships these packages
(see [docs/INSTALL.md](docs/INSTALL.md) for the details of each):

| Package | For | Notes |
|---------|-----|-------|
| `AllInOnePdf-<version>-win-x64.msi` | Windows 10/11, 64-bit | Installs to Program Files with a Start menu entry; data in `%APPDATA%\AllInOnePdf`. Unsigned: SmartScreen warns once. |
| `AllInOnePdfPortable-win-x64.zip` | Windows, no install | PortableApps.com layout; everything stays in the folder. |
| `allinonepdf_<version>_amd64.deb` | Debian 12+, Ubuntu 22.04+ | `sudo apt install ./allinonepdf_<version>_amd64.deb` pulls WebKitGTK 4.1 and GTK 3. |
| `allinonepdf-<version>-1.x86_64.rpm` | Fedora 40+ | `sudo dnf install ./allinonepdf-<version>-1.x86_64.rpm`. |
| `AllInOnePdf-<version>.flatpak` | Any Linux with Flatpak | `flatpak install AllInOnePdf-<version>.flatpak` (the GNOME runtime comes from Flathub). |
| `AllInOnePdf-linux-x64.tar.gz` / `.zip` | Linux, no install | `./AllInOnePdf.sh`; after the zip run `chmod +x AllInOnePdf.sh app/AllInOnePdf`. |

The installers are built by `build/publish-install.ps1` (a plain self-contained publish) followed by
`build/msi/build-msi.ps1` (WiX, a repo-local dotnet tool), `build/linux/build-packages.ps1` (dpkg-deb and rpmbuild in
Docker) and `build/flatpak/build-flatpak.ps1` (flatpak-builder in Docker).

## Portable builds

One script produces self-contained, no-install packages: a single launcher at the root, the app in a subfolder
beside it, and every setting in `Data`:

```bash
pwsh build/publish.ps1            # both platforms (Docker is used only to tar the Linux build)
```

| Package | Layout | Run |
|---------|--------|-----|
| `publish/AllInOnePdfPortable-win-x64.zip` | PortableApps.com format: `AllInOnePdfPortable.exe`, `App\AllInOnePdf\` (the app), `App\AppInfo\appinfo.ini`, `Data\` | `AllInOnePdfPortable.exe` |
| `publish/AllInOnePdf-linux-x64.tar.gz` and `.zip` | `AllInOnePdf.sh`, `app/` (the app), `Data/` | `./AllInOnePdf.sh` (the zip loses the executable bit: `chmod +x AllInOnePdf.sh app/AllInOnePdf` first) |

`AllInOnePdfPortable.exe` is a 20 KB launcher (`src/AllInOnePdf.Launcher`) that starts `App\AllInOnePdf\AllInOnePdf.exe`
and passes the command line through. It targets the .NET Framework 4.8 that ships with Windows 10 and 11, so it needs
no runtime of its own; the app carries its own .NET runtime inside `App`.

Portable mode is switched on by a `portable.txt` file next to the app executable (or by the PortableApps.com folder
layout). Preferences, themes, history, the browser cache and the default output folder then live in `Data`; nothing
is written to the user profile. Delete `Data` to reset. `AllInOnePdf --paths` shows where data goes.

Requirements on the target machine: Windows 10/11 with the Edge WebView2 Runtime (standard on Windows 11; a
fixed-version runtime dropped into `App\AllInOnePdf\WebView2Runtime\` is used automatically otherwise), or Linux with
WebKitGTK 4.1 + GTK 3 (`libwebkit2gtk-4.1-0`). Plain `dotnet publish -r <rid> --self-contained` still works for a
non-portable copy.

## Releases

Versions are cut from tags. Add a `## [1.2.3] - YYYY-MM-DD` section to `CHANGELOG.md`, then push the tag `v1.2.3`:
the Release workflow builds and tests on Windows and Linux, stamps the version into the binaries and packages, and
publishes a GitHub release with every package from the Install table, `SHA256SUMS.txt` and that changelog section as
the notes. A tag with a pre-release label (`v1.1.0-rc.1`) becomes a pre-release. Running the workflow by hand
("Run workflow" with an existing tag and "Create the GitHub release" off) builds the packages as workflow artifacts
without touching the release. `Directory.Build.props` carries the version for local builds;
`build/publish.ps1 -Version 1.2.3` stamps one by hand.

## Verify on Linux with Docker

```bash
docker build -f docker/Dockerfile.linux-test -t allinonepdf-test .
```

Builds and runs the test suite on Linux. `docker/Dockerfile.web` produces a runnable image of the web host.

## Project layout

```
src/AllInOnePdf.Core      document model, converters, themes, PDF rendering (the layout engine), rasterizer (pdfium)
src/AllInOnePdf.Layout    the PDF layout engine: fluent document model, text shaping, pages, PDF output (SkiaSharp + HarfBuzz)
src/AllInOnePdf.Publishing  the Creator's publication model, .aiopub packages, templates and page renderer
src/AllInOnePdf.UI        Blazor UI (Fluent UI Blazor), shared by both hosts
src/AllInOnePdf.Desktop   Photino desktop host (Windows / Linux)
src/AllInOnePdf.Launcher  AllInOnePdfPortable.exe: the one file at the root of the Windows portable package
src/AllInOnePdf.Web       Blazor Server host (dev preview, optional server mode)
tests/                    xUnit tests
docs/                     ARCHITECTURE.md · DESIGN.md · AGENTS.md · CREATOR.md · LAYOUT-ENGINE.md · INSTALL.md · STATUS.md
docs/quality/             the writing, interface and coding profiles every change is checked against
```

## Security posture

No network access, no telemetry, no external processes, no runtime downloads. Source HTML is never rendered raw and
remote images are never fetched. Output file names are sanitised and confined to the chosen folder. Imported theme
JSON is validated and clamped. The web host binds to loopback by default and sends a strict Content-Security-Policy.
See `docs/ARCHITECTURE.md`.

## Data and settings

Preferences, user themes, publications, history, crash logs and the editors' recovery drafts live in
`%APPDATA%\AllInOnePdf` (Windows) or `~/.config/AllInOnePdf` (Linux), or in `Data` beside a portable install:

| Folder or file | Holds |
|----------------|-------|
| `preferences.json` | Settings |
| `themes/` | Your themes as JSON |
| `publications/` | Creator projects as `.aiopub` packages |
| `drafts/` | Unsaved work the theme builder and the Creator autosave, offered back after a crash; cleared on save |
| `history.json` | Recent conversions |
| `logs/` | Crash logs |
| `cache/`, `browser/` | Thumbnails and the webview profile; safe to delete |

Set `ALLINONEPDF_DATA_DIR` to use another folder (for tests, reviews and containers), or run
`AllInOnePdf --paths` to see where data goes. Deleting the folder resets the app.

## Contributing

Issues and pull requests are welcome. [CONTRIBUTING.md](CONTRIBUTING.md) explains how to build and test, where the code
lives, the project rules (offline, pure .NET, permissive licences) and what a pull request needs. Report security
problems privately as described in [SECURITY.md](SECURITY.md).

## Licensing

This project is MIT licensed (see `LICENSE`). It depends on:

| Component | License |
|-----------|---------|
| Fluent UI Blazor | MIT |
| Photino.NET / Photino.Blazor | Apache-2.0 |
| Markdig | BSD-2-Clause |
| PdfPig | Apache-2.0 |
| PDFtoImage (pdfium) | MIT (pdfium: BSD-3) |
| SkiaSharp | MIT |
| HarfBuzzSharp | MIT (HarfBuzz: Old MIT) |
| AngleSharp, DocumentFormat.OpenXml | MIT |
| Inter, Literata, JetBrains Mono | SIL Open Font License 1.1 |

Every dependency is permissively licensed (MIT, Apache-2.0, BSD or OFL). PDFs are laid out by the app's own engine
(`src/AllInOnePdf.Layout`, MIT) on SkiaSharp and HarfBuzz.

Full licence texts, versions and copyright holders for everything the packages ship are in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md), which the app also shows under Settings › About › Third-party notices.
