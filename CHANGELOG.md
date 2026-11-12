# Changelog

All notable changes to AllInOne PDF are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [semantic versioning](https://semver.org/). The release workflow turns the section whose
heading matches the pushed tag (`v1.0.0` → `## [1.0.0]`) into the GitHub release notes, so keep one section per
release and write it for users, not for developers.

This repository starts at 1.2.1. Versions 1.0.0 to 1.1.1 were released from the project's earlier private repository;
their notes are kept below.

## [Unreleased]

## [1.2.1] - 2026-11-07

(1.2.0 was never released: its tag was created at the wrong commit and withdrawn.)

Every page uses the whole window, the Reader opens files the way Office does (Recent list, file-type rows), and the
wording across the app says what things do.

### Changed

- Pages fill the window. A maximized window used to show every page in a column of at most 1400 px with the rest
  empty; now the content uses the width: card and template grids take more columns, Settings shows its cards in two
  columns and Help puts its contents list beside the text on wide windows, while running text keeps a readable line
  length.
- Reader: the Open page works like opening a document in Office. The files you opened are listed under
  **Recent** (name, folder and time, with remove), and **File types** lists each type with its icon and extensions
  as rows instead of pill buttons; on the desktop a row opens the file dialog for that type. A **Close** button on the
  reading toolbar returns to the Open page.
- Wording across the app was reviewed against the writing profile in `docs/quality/`: descriptions say what a page
  or setting does instead of selling it, terms are used consistently (Structure panel, Colour mode, Recent, File
  types), counts read correctly in the singular, and error messages say what happened and what to do.

## [1.1.1] - 2026-11-07

Installers for every platform: a Windows MSI, a Debian/Ubuntu .deb, a Fedora .rpm and a Flatpak, next to the portable
packages, which now include a zip for Linux as well. The app itself is the same as 1.1.0 apart from a small fix to
the portable Linux launcher.

### Added

- A Windows installer, `AllInOnePdf-<version>-win-x64.msi`: installs AllInOne PDF for all users in Program Files,
  adds it to the Start menu (and to the desktop if you tick the box), and uninstalls from Settings › Apps. Your
  settings stay in your user profile when you uninstall. The portable zip is still available.
- Linux installers: a .deb for Debian 12+ and Ubuntu 22.04+ and an .rpm for Fedora 40+. They put AllInOne PDF in
  the applications menu, offer it under *Open with* for the files it reads, add the `allinonepdf` command and pull in
  the libraries it needs. Settings go to `~/.config/AllInOnePdf`.
- The portable Linux build is also offered as a zip next to the tar.gz. Zip files do not keep Unix permissions, so
  run `chmod +x AllInOnePdf.sh app/AllInOnePdf` once after unzipping (README.txt says so too).
- AllInOne PDF is available as a Flatpak for Linux: install `AllInOnePdf-<version>.flatpak` from the release with
  `flatpak install`, and it runs sandboxed on the GNOME runtime from Flathub with access to your home folder and no
  network. `docs/INSTALL.md` describes every package.

### Fixed

- Portable Linux build: the launcher finds `ldconfig` in `/sbin` too, so it detects WebKitGTK and ICU for users whose
  PATH does not include it (Debian), instead of always falling back to invariant globalisation.

## [1.1.0] - 2026-11-07

The app draws its own title bar on Windows, every Creator box can be shifted and layered, and sections can be laid
out in **Free flow**: a page-sized canvas where you drag modules anywhere, resize them and stack them, next to the
Structure layout of rows and columns. The slider loop that made the border width jump without end is fixed.

### Added

- On Windows the app now draws its own title bar, like the Windows Settings app: the app's icon and name on the left,
  the theme and Settings buttons, and Minimize, Maximize/Restore and Close on the right. Drag any empty part of the
  bar to move the window (it snaps to the screen edges as usual) and double-click it to maximize or restore. The
  window still resizes from every edge.
- Settings › Appearance › **Use the system title bar** brings back the Windows title bar. It switches straight away.
- Settings › About has a **therandomadmin.com** button that opens the author's website.
- PDF engine: elements can be shifted, layered and placed freely on a page; the Creator uses this for its Position
  settings and the Free flow layout.
- Creator, Design tab: a Position group for sections, rows, columns and modules. Offset X and Offset Y move a box
  without moving its neighbours, so it can overlap them, and Z level decides which of two overlapping boxes is on
  top. The design canvas, Preview and the exported PDF all show it.
- Creator: **Free flow**. Next to Structure (rows and columns that flow from page to page), a section can now be a
  free canvas one page tall where you drag modules anywhere, resize them with handles and stack them over each other.
  Modules snap to the page edges, the centre and each other (hold Alt to place freely), arrow keys nudge them, and
  Ctrl+] / Ctrl+[ change which one is on top. The **Structure | Free flow** switch above the canvas sets the layout of
  new sections and offers to convert the existing ones, keeping modules where they are on the page; every section can
  also be switched on its own. A small warning marks a module whose content does not fit its box.
- Creator: Preview and the exported PDF draw a free-flow section as a canvas, one page tall unless you give it a
  height, where every module sits exactly where you placed it, at the size you gave it, with higher Z levels painted
  on top. A canvas that does not fit the rest of a page starts on the next one, and text that does not fit its box is
  cut off at the box's edge. Export as Word, HTML, EPUB or Markdown and Read in Reader read a canvas from top to
  bottom and left to right.
- Creator: a **Poster** template, a one-page free-flow A4 poster with a big headline, a picture with a quote
  overlapping its lower edge, a short text and a button.

### Changed

- Settings › About: **Source code** is now **GitHub**, styled like the other About buttons.

### Fixed

- Creator: dragging the Border width slider (or any other slider in the inspector) no longer makes the value jump
  back and forth without end; typing a value and the arrow keys behave too, and a drag is one undo step.
- Theme builder and Convert options: sliders and number fields no longer jump back and forth while you drag or hold
  an arrow key. A typed value is rounded to the field's step and kept in range, and the box shows the result.
- Creator: corner radius now shows on the design canvas. A rounded box clips what is inside it, so a picture in a
  rounded module has rounded corners, as in the PDF. The two radius fields of an image say what they round: "Rounds
  the picture" and "Rounds the box".
- Creator: a border without a colour of its own is drawn in the theme's border colour in the PDF, as the canvas
  already showed, instead of being left out.

## [1.0.1] - 2026-11-06

The Creator follows the real PDF pages, unsaved publications wait for you in the gallery, and the 57 findings of the
first full QA pass (`docs/QA-2026-11-06.md`) are fixed: import fidelity for PDF, EPUB and PowerPoint, Convert's queue
and messages, the Reader, Themes and the Creator's canvas.

### Added

- Creator, Design mode: the canvas is a continuous scroll of page sheets that follow the real PDF. About half a
  second after each edit the publication is laid out in the background, and a new sheet starts wherever the PDF starts
  a new page: between sections, between rows, between the modules of a single-column row. A module or a side-by-side
  row the PDF splits inside stays where it starts with a "Continues on page n" mark. Each sheet is labelled "Page n of
  m", and an "Add page" button next to "Add section" starts a section on a new page.
- Creator, Structure panel: a dotted "Page n" line marks where each PDF page begins, and a module or row that runs
  over a page shows "p. 1–2".
- Creator: Export as Excel, PowerPoint and page images (PNG, JPEG or WebP, one image per page, zipped when there
  are several) next to Word, HTML, EPUB, Markdown and plain text.
- Help describes the page sheets, the "Continues on page n" marks, "Add page", the tree's page markers and the
  Preview keys, and lists the drafts folder under "Your data".

### Fixed

- PDF import: a cover page no longer comes back as content; its title, subtitle, author and date go into the
  document's details, and the date is the document's own rather than the moment the PDF was made. A contents page
  with a single entry is left out as well.
- PDF import: a table that runs over several pages comes back as one table, without the repeated header rows, with
  rows split by a page break joined and its column alignment kept; header cells are no longer wrapped in bold.
- PDF import: long code lines that the PDF wrapped are joined again, and code blocks keep their language (PDFs made
  by this version carry it invisibly).
- PDF import: a long web address used as link text comes back intact, and strikethrough, highlight and footnotes are
  recognised again.
- PDF import: an empty or damaged file reports a plain message; the password option is shown without "Show
  advanced", and the "Keep source PDF" description no longer mentions PDF → PDF.
- EPUB import: task list items keep their text on one line without the box symbol, footnotes lose the stray "1"
  back-link, and the subtitle is kept (EPUB export now also writes it into the book's details).
- PowerPoint import: headings inside slides come back as headings, slides are separated by a divider (`---`) by
  default (page breaks remain an option), and "(cont.)" slides join the slide they continue.
- Creator: the page count in Design, the tree and Preview now matches Export PDF. An image module without an image
  takes no space in any of them (the preview used to reserve a grey box for it, so the Flyer showed two pages and
  exported one).
- Creator: Theme… keeps the publication's page size, orientation, margins and the header and footer text; only the
  look changes.
- Convert: dropping several large files on the empty page no longer loses some of them, and a file the browser
  stops sending mid-way is reported instead of vanishing.
- Convert: the upload progress block no longer sits over "Set target for all" and "Clear completed" after a second
  drop.
- Reader: "Convert this" now adds the open document to Convert (the file itself, or its Markdown for the sample and
  for publications read from the Creator) instead of only opening the page.
- History on the web host explains why entries cannot be opened or converted again there.
- Creator, Preview mode: previous and next buttons beside the page counter, Page Up and Page Down, the left and
  right arrow keys, Home and End, and the mouse wheel past the bottom or top of a page all turn pages.
- Creator gallery: publications you closed without saving now wait for you under "Continue where you left off", with
  their name, template, a preview and how long ago you last changed them. Continue picks up exactly where you
  stopped; Discard throws the changes away. A saved publication with unsaved changes is marked on its own card.
- Settings: the section list highlights the section you have scrolled to.
- Settings in the browser: the output folder, "Save next to source" and the open/reveal switches, which only apply in
  the desktop app, are replaced by a line saying where downloads go.
- Third-party notices open at once, without repeating the dialog title or telling you how to open them; each
  component's licence text is a section you can expand.
- Keyboard: a "Skip to page content" link is the first tab stop, and Convert's hidden file input is no longer a tab
  stop of its own.
- Screen readers and voice control: buttons such as Clear all, Clear history, Copy diagnostics, Read, Releases and
  Source code are named by their visible text; the longer explanation stays in the tooltip.
- Clear history, Reset settings and replacing existing files ask with a warning icon instead of a green question
  mark.
- Convert: with many files the list stays responsive; a file's row only redraws when something on it changes.
- Convert: the status line adds up ("3 of 5 converted · 1 failed · 1 converting"), counts every converted file in the
  list, and tells converting files from waiting ones. A large file that spends seconds on one step shows a moving
  bar.
- Convert: one "files converted" message per batch instead of one per file, and it no longer covers Download all,
  which now sits above the list.
- Convert: files whose names differ only by extension (`notes.docx`, `notes.html`) no longer produce outputs with the
  same name; the extension is added (`notes-docx.pdf`).
- Convert: Markdown or HTML with pictures downloads as one zip that keeps the pictures folder, so the image links work.
  The row's download button says what it downloads ("Download", "Download zip", "Download 7 files").
- Convert: "Set target for all" closes with Escape and no longer needs a second click after picking a target.
- Convert: the options panel's two tabs are spaced and underlined properly; the PDF password is no longer hidden
  under "Show advanced" and is masked; "Keep source PDF" describes what it does.
- Convert: an empty file is flagged before converting and on its result; an empty or damaged PDF says so without
  the PDF reader's internal message; an unsupported type says "Cannot read .ttf files" and is announced once.
- Reader: documents without text (pictures) show "No text" instead of "0 words · 1 min read".
- Reader: the font names in the Text panel are no longer cut off.
- Themes: search also looks at tags, so "dark" finds every dark theme (your own too, by page colour); your own themes
  are listed first under "Your themes"; Clean no longer carries a "default" tag next to the real Default badge; the
  card buttons say which theme they act on to screen readers and voice control.
- Themes import: values that were out of range or not colours are listed in a message after the import, and a JSON
  file that is not a theme says so instead of calling the JSON malformed.
- Theme builder: a header or footer text with an unclosed `{` or an unknown token shows a message; the Fast preview
  prints a real page count for `{pages}` instead of "n"; "Start from…" shows one message; below 1100 px the
  Settings drawer opens below the preview bar so "Start from…" stays usable.
- Theme builder and Creator: the unsaved-changes dialog offers "Save and leave", and leaving a new theme or template
  you did not change no longer asks at all.
- Creator: the canvas header and footer print the real page count for `{pages}` and follow "Show on the first page";
  the Report template has no page number on its title page; "Add row" appears only when you point at the gap between
  rows; Esc clears the selection on a narrow window even right after a panel closed; zoom is in the More menu below
  1100 px; the builder no longer scrolls sideways at 1000 px and End in Preview no longer nudges it up.
- Creator: "Reset to saved…" asks first; Save as… on a publication that was never saved no longer mentions an
  original; an imported package whose name is already in the gallery is named "… (imported)"; an empty background
  reads "None" instead of showing the page colour as if it were set.

### Changed

- Convert: a PDF starts with Markdown as its target instead of EPUB. A target remembered from the last file of the
  same type is shown on the row ("Last used for Markdown files").
- Reader: changes made while reading (palette, size, font, outline) last while the app is open and no longer
  overwrite your reading defaults in Settings; "Reset to defaults" in the Text panel goes back to those defaults.
- Settings: the accent colours have colour names (Lavender, Cobalt, Teal…) instead of product names.

## [1.0.0] - 2026-11-06

The first complete release: convert, read, style and now lay out PDFs, all offline and in pure .NET, on Windows
and Linux.

### Convert

- Drop files, pick a target, tweak options, convert. A queue with live progress, cancel and retry, and a history
  with "convert again".
- Every source becomes every target through one neutral document model: Markdown, PDF, HTML, Word (.docx),
  Excel (.xlsx), PowerPoint (.pptx), RTF, EPUB, plain text, PNG/JPEG/WebP/GIF/BMP images, CSV, JSON and source
  code in, and PDF, Markdown, HTML, Word, Excel, PowerPoint, EPUB, text and page images out.
- PDF import with layout analysis: headings from bookmarks or font statistics, lists, tables, code, callouts,
  images and links; running headers, footers and printed contents pages are removed; page ranges and passwords.
- Markdown with CommonMark and GitHub extensions, syntax highlighting for about twenty languages, numbered
  headings, keep-with-next, a contents page with dot leaders and header/footer tokens.
- Office formats through the Open XML SDK, no Office installation needed; RTF through a hand-written parser.

### Read

- A reflowable reading view with Paper, Sepia, Dusk and Night palettes, adjustable type, an outline with
  scroll-spy, focus mode, keyboard shortcuts and a page-accurate print preview. Opens any readable format.

### Themes

- 28 built-in PDF themes: document styles (Clean, Manuscript, Technical, Compact, GitHub, Swiss, Academic, Tufte,
  Newsprint, Contract, Handout, Typewriter, Print, Large Print), tinted papers (Sepia, Solarized Light, Catppuccin
  Latte, Gruvbox Light, Rosé Pine Dawn) and dark screens (Night, GitHub Dark, Solarized Dark, Nord, Dracula,
  Catppuccin Mocha, Gruvbox Dark, Tokyo Night, Everforest).
- A theme builder with a fast HTML preview and an exact PDF preview, click-to-select, undo and redo, presets,
  validation warnings and JSON import and export. Page, typography, six heading levels with alignment and rules,
  colours, code with highlight palettes, tables, quotes, callouts, lists, links, images, header and footer
  templates, cover pages and contents settings.
- Header, footer and cover dates follow the document's language (a German document prints `20.06.2026`
  everywhere); documents without a language follow the machine's region.

### PDF engine

- PDFs are laid out by the app's own engine, built on SkiaSharp and HarfBuzz: page flow, tables that continue
  across pages, shaped text with kerning, hyphenation and justification, images, SVG, links, outlines and a text
  layer that extracts cleanly.
- Fonts are embedded as subsets (only the glyphs a document uses), so a typical PDF is well under 100 KB.
- Every dependency is under a permissive licence (MIT, Apache-2.0, BSD or OFL): no revenue limits or paid tiers for
  anyone, including public-sector and publicly traded organisations.

### Create

- The Creator: a page builder for PDFs. Sections hold rows, rows split into columns, columns hold modules (text in
  Markdown, headings, images, dividers, spacers, quotes, callouts, code, tables, buttons, a contents list and page
  breaks). Drag and drop, a hover toolbar, a structure tree, an inspector with content and design tabs, undo for
  everything, and keyboard shortcuts.
- Any theme is a publication's style, with per-publication page, header and footer settings. Seven templates to
  start from. A design canvas and an exact page preview. Export to PDF or any other format, read in the Reader,
  and portable `.aiopub` project packages.

### App

- A Fluent, Intune-style interface with light and dark modes, a collapsible navigation and a settings page.
- Portable packages for Windows (PortableApps.com layout with a single launcher exe) and Linux (tarball with a
  launcher script); all data lives beside the app in portable mode, or in the user's profile otherwise.
- Offline by design: no network calls, no telemetry, no remote images, no external processes. Bundled Inter,
  Literata and JetBrains Mono fonts make output identical on every OS.
- Crash logs under the data folder, `AllInOnePdf --paths` to show where data goes.
- Autosave drafts and crash recovery in the theme builder and the Creator.
- A Help page, and an About section in Settings with version, diagnostics to copy into a problem report, and the
  third-party notices.

[Unreleased]: https://github.com/rndadhdman/AllInOnePDF/compare/v1.2.1...HEAD
[1.2.1]: https://github.com/rndadhdman/AllInOnePDF/releases/tag/v1.2.1
