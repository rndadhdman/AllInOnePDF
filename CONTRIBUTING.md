# Contributing to AllInOne PDF

Bug reports, fixes and new features are welcome. This page explains how to build the app, where the code lives,
and what a pull request needs before it can be merged.

## Before you start

* For a bug, open an issue with the **Bug report** form. The app's Settings › About › **Copy diagnostics** button
  copies the version, operating system and data folder, which is usually what is needed to reproduce it.
* For a feature or a larger change, open an issue first and describe what you want to change and why. Some
  directions are settled (see "Project rules" below), and it is better to find that out before writing code.
* Small fixes (typos, a wrong label, an obvious bug with a test) can go straight to a pull request.
* Security problems go through private reporting, not issues: see [SECURITY.md](SECURITY.md).

## Build and run

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) (`global.json` pins 10.0.100 or a later feature
band). The desktop window needs the WebView2 runtime on Windows (part of Windows 11) or WebKitGTK 4.1 on Linux
(`sudo apt install libwebkit2gtk-4.1-0 libgtk-3-0`).

```bash
git clone https://github.com/rndadhdman/AllInOnePDF.git
cd AllInOnePDF
dotnet build AllInOnePdf.slnx
dotnet run --project src/AllInOnePdf.Desktop                      # the desktop window
dotnet run --project src/AllInOnePdf.Web --launch-profile http    # the same UI at http://127.0.0.1:5180
```

The web host is the quickest way to work on the interface: it runs the same components in a browser, and
`dotnet watch --project src/AllInOnePdf.Web --launch-profile http` reloads on save. To keep test data away from your
own settings, set `ALLINONEPDF_DATA_DIR` to a scratch folder before starting either host.

Run the three test projects one after the other (they share a queue timing test that dislikes a second test host):

```bash
dotnet test tests/AllInOnePdf.Core.Tests
dotnet test tests/AllInOnePdf.Publishing.Tests
dotnet test tests/AllInOnePdf.Layout.Tests
```

Docker is only needed for the Linux packages (`build/linux`, `build/flatpak`) and for checking exported Office files
against LibreOffice (`build/check-office.ps1`). [docs/INSTALL.md](docs/INSTALL.md) lists every packaging script.

## Where things are

| To change | Read first | Code |
|-----------|------------|------|
| Formats, conversion, themes | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | `src/AllInOnePdf.Core` |
| How PDFs are laid out | [docs/LAYOUT-ENGINE.md](docs/LAYOUT-ENGINE.md) | `src/AllInOnePdf.Layout` |
| The Creator (page builder) | [docs/CREATOR.md](docs/CREATOR.md) | `src/AllInOnePdf.Publishing`, `src/AllInOnePdf.UI/*/Creator*` |
| Screens, components, styling | [docs/DESIGN.md](docs/DESIGN.md) | `src/AllInOnePdf.UI` |
| Desktop window, title bar | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (hosts) | `src/AllInOnePdf.Desktop` |
| Installers and portable packages | [docs/INSTALL.md](docs/INSTALL.md) | `build/` |

[docs/STATUS.md](docs/STATUS.md) describes the current state of the project, known limits and ideas for what comes
next. [docs/AGENTS.md](docs/AGENTS.md) is the practical checklist (conventions, the Fluent UI crib sheet, the layout
rule) for anyone writing code here, people and AI coding agents alike.

## Project rules

These come from decisions about what the app is. A pull request that breaks one will be asked to change.

* **Offline and pure .NET.** No network calls, no telemetry, no runtime downloads, no external processes, no browser
  engine for rendering, no native engines beyond the ones already shipped (SkiaSharp, HarfBuzz, pdfium). OCR is out
  of scope for that reason.
* **Permissive licences only.** Every dependency is MIT, Apache-2.0, BSD or OFL. Ask in an issue before adding a
  NuGet package; [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) must list anything that ships.
* **Fluent UI Blazor 4.** Use its components and design tokens so light and dark mode keep working; avoid one-off
  styles.
* **Quality profiles.** [docs/quality](docs/quality) holds three instruction profiles. Every string a user reads is
  checked against the writing profile (say what the control does, in the user's words), every screen against the
  interface profile (work at 1000 to 2560 px, keyboard access, real empty and error states), and code against the
  coding profile. They are review prompts, not word lists.

## Pull requests

* One topic per pull request, branched from `main`.
* The solution builds without warnings in the files you touched, and all three test projects pass. CI runs both on
  Windows and Linux for every pull request.
* A change in behaviour comes with a test that would have failed before it.
* User-visible changes add a line to `CHANGELOG.md` under `## [Unreleased]`, written for users.
* Changes to the layout engine must keep `tests/AllInOnePdf.Layout.Tests` green. The maintainer also compares the
  rendered output pixel by pixel against a reference set that is not in the repository and will report any page that
  moved.
* Interface changes: include a screenshot or two (light and dark, a narrow and a wide window) in the description.

Contributions are accepted under the project's [MIT licence](LICENSE).
