# Working on this repo (team conventions)

Read `docs/ARCHITECTURE.md` and `docs/DESIGN.md` first. This file is the practical checklist.

## Build, run, test

```bash
dotnet build AllInOnePdf.slnx                 # everything
dotnet test  tests/AllInOnePdf.Core.Tests     # unit tests (Core)
dotnet test  tests/AllInOnePdf.Publishing.Tests   # unit tests (Creator engine); run the two one after the other
dotnet run   --project src/AllInOnePdf.Web --launch-profile http   # web host → http://127.0.0.1:5180
dotnet run   --project src/AllInOnePdf.Desktop                      # desktop window (Windows/Linux)
```

Use the **web host** to look at UI while developing (it is the same components as the desktop app). Hot reload:
`dotnet watch --project src/AllInOnePdf.Web --launch-profile http`.

Exported Word, Excel and PowerPoint files can be checked against an independent office suite without installing
one: `.\build\check-office.ps1 -Folder <folder of .docx/.xlsx/.pptx>` opens each file with LibreOffice headless in
Docker (`docker/Dockerfile.libreoffice`) and converts it to PDF, reporting any file that fails to open.

## Areas (who touches what when several people work at once)

| Area | Folders / files |
|------|-----------------|
| App shell, theming, settings | `UI/Layout/*`, `UI/Components/Shell/*`, `UI/Pages/Settings*`, `UI/wwwroot/css/app.css`, `UI/wwwroot/js/app.js`, `UI/Services/Preferences.cs`, `UI/Services/ThemeService.cs`, `UI/Services/Shared/*` |
| Convert workspace + history | `UI/Pages/Home*`, `UI/Pages/History*`, `UI/Components/Converting/*`, `UI/Services/Convert*` |
| Reader | `UI/Pages/Reader*`, `UI/Components/Reader/*`, `UI/Services/Reader*` |
| Themes gallery + builder | `UI/Pages/Themes*`, `UI/Pages/ThemeBuilder*`, `UI/Components/ThemeBuilder/*`, `UI/Services/ThemeBuilder*` |
| Creator (page builder UI) | `UI/Pages/Creator*`, `UI/Components/Creator/*`, `UI/Services/Creator*` |
| Publishing engine | `src/AllInOnePdf.Publishing/*` (model, packages, store, templates, renderer), `tests/AllInOnePdf.Publishing.Tests/*`; spec in `docs/CREATOR.md` |
| Core engines | `Core/Importers/*`, `Core/Exporters/*`, `Core/Rendering/*`, `Core/Highlighting/*`, `Core/Themes/*` |
| Hosts, docs, CI | `Desktop/*`, `Web/*`, `docs/*`, `.github/*`, `docker/*` |

Rules when working as part of a team:

1. **Stay inside your area.** Shared helpers go under `UI/Services/Shared/` or `UI/Components/Shared/`; say so in
   your report. Never edit another owner's files.
2. **Core contracts are stable.** If Core lacks something, write an adapter in your area and list the gap in your
   report; the director decides whether Core changes.
3. **Don't add NuGet packages or JS libraries.** Fluent UI + what's in `Directory.Packages.props` is the toolbox.
4. **Work on your own branch in your own copy of the repo** (outside contributors: a fork) and open a pull request;
   the maintainer merges. When several AI agents work in parallel on one machine, each gets its own clone so their
   builds do not collide on `obj/`; on the maintainer's machine the repo lives on a drive without file ownership,
   where git refuses new worktrees, so those clones are made from a bundle (`git bundle create` →
   `git clone <bundle>`) and their branches fetched back. Use absolute paths inside your copy for every command.
5. **It must build warning-clean in your files** (`dotnet build AllInOnePdf.slnx`) and must not break the tests.
6. **Keep test data out of the real app data**: set `ALLINONEPDF_DATA_DIR` to a temp folder before running a host.
7. The web host serves RCL static assets (`_content/…`) only when `ASPNETCORE_ENVIRONMENT=Development` (or from a
   published build), so smoke runs need that variable.

## Coding conventions

* C# 13/.NET 10, nullable on, file-scoped namespaces, `var` when obvious, async all the way (`CancellationToken` on
  anything that can take > 50 ms). Private fields `_camelCase`.
* Razor components: PascalCase file names, one component per file, code in `@code` unless it exceeds ~80 lines
  (then a partial class `X.razor.cs`). Parameters first, injected services next, state after.
* CSS isolation (`Component.razor.css`) for component styles. Global tokens only in `app.css`. Prefer Fluent design
  tokens (`var(--neutral-layer-1)`, `var(--accent-fill-rest)`) over literal colours so dark mode just works.
* Services: register in `UI/ServiceCollectionExtensions.cs` (use `TryAdd*`). Scoped = per circuit/window, singleton
  = shared with background work. UI state goes in `AppState` or a scoped service you own.
* Background work (`ConversionQueue.Changed`, timers) may fire off the renderer thread: wrap UI updates in
  `InvokeAsync(StateHasChanged)`.
* Host differences: ask `IHostServices` (`SupportsNativeDialogs`, `SupportsShellOpen`) and adapt. On the web,
  use `FluentInputFile` for input and `IHostServices.SaveFileAsync` for output.
* Text: sentence case, no exclamation marks, plain words ("Convert", "Save as…", "Choose folder"). Errors say what
  happened and what to do next.

## Fluent UI Blazor v4 crib sheet

Package `Microsoft.FluentUI.AspNetCore.Components` 4.14 (+ `.Icons`). Namespaces are already in `_Imports.razor`.

* Layout: `FluentLayout`, `FluentHeader`, `FluentBodyContent`, `FluentFooter`, `FluentStack`, `FluentGrid`/`FluentGridItem`, `FluentSpacer`, `FluentDivider`, `FluentCard`, `FluentPanel` (side drawer via `IDialogService.ShowPanelAsync`), `FluentTabs`/`FluentTab`, `FluentAccordion`/`FluentAccordionItem`, `FluentSplitter`.
* Navigation: `FluentNavMenu`, `FluentNavLink`, `FluentNavGroup`, `FluentBreadcrumb`, `FluentAnchor`, `FluentToolbar`.
* Inputs: `FluentButton` (`Appearance.Accent|Neutral|Outline|Stealth|Lightweight`, `IconStart`), `FluentTextField`, `FluentTextArea`, `FluentNumberField<T>`, `FluentSlider<T>`, `FluentSwitch`, `FluentCheckbox`, `FluentRadioGroup`, `FluentSelect<T>`/`FluentOption<T>`, `FluentCombobox`, `FluentAutocomplete`, `FluentSearch`, `FluentInputFile` (drag-drop + browse; `Mode="InputFileMode.Buffer|Stream"`), `FluentMenu`/`FluentMenuItem`, `FluentMenuButton`.
* Data: `FluentDataGrid<T>` with `PropertyColumn`/`TemplateColumn`, `FluentListbox`, `FluentTreeView`, `FluentBadge`, `FluentCounterBadge`, `FluentLabel Typo="Typography.H1…Body"`, `FluentSkeleton`.
* Feedback: `IToastService.ShowSuccess/ShowError/ShowProgress`, `IDialogService.ShowDialogAsync/ShowConfirmationAsync/ShowPanelAsync`, `FluentMessageBar`, `FluentProgress`, `FluentProgressRing`, `FluentTooltip`, `FluentOverlay`.
* Icons: `new Icons.Regular.Size20.ArrowSwap()` (also `Filled`, sizes 12/16/20/24/28/32/48). Use `FluentIcon Value="@icon"`. `Icons` is a **namespace** in 4.11+; `_Imports.razor` already aliases it (`@using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons`). In `.cs` files add `using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;`.
* Theme: `FluentDesignTheme` (`Mode`, `OfficeColor` or `CustomColor`, `StorageName`). `GlobalState` service exposes the current luminance.
* Providers (`FluentToastProvider`, `FluentDialogProvider`, `FluentTooltipProvider`, `FluentMessageBarProvider`, `FluentMenuProvider`) are already in `MainLayout`.

Docs: https://www.fluentui-blazor.net (v4). Check a component's parameters there rather than guessing.

## Quality profiles (mandatory)

`docs/quality/` holds three instruction profiles that every change is checked against. Read the ones that apply
before you start and run your result through them before you report:

* `anti_ai_writing.json` for every string a user reads (labels, descriptions, hints, dialogs, toasts, errors, Help,
  docs): select the `application_copy` mode (or `documentation` for docs). Say what the control does, in the words
  of the person using it; no promotional adjectives, no rule-of-three padding, no "not just X but Y", no em dashes as a
  tic, no filler subtitles that repeat the label, stable terms for the same thing, sentence case, no exclamation marks.
* `anti_ai_web_ui.json` for every screen: select one mode (`redesign`, `component`, `design_review` or
  `responsive_qa`), design from this product's users and tasks, prefer Fluent UI Blazor components and Windows
  conventions, keep every state (empty, loading, error, long content) real, and judge cards, pills, grids and heroes
  by the job they do. Verify with the actual build at several window widths (1000, 1400, 1920 and 2560 px).
* `anti_ai_coding.json` for the code: select `implementation`, `bug_fix` or `refactor`, inspect the repository before
  changing it, keep the diff scoped, and report exactly what was run and what was not.

The profiles are review prompts, not word lists: fix the problem under a signal, never swap words to look different.

## Layout rule: pages fill the window

`.aio-page` has no width cap. At 1920 or 2560 px a page uses the width: grids, lists and tables take more columns,
side-by-side panels widen, and only running text keeps a measure (about 90 characters) through its own max-width.
No page leaves a large empty region to the right of its content at any supported window size.
Pages with sections (Settings, Help) put a section list on the left and lay the sections out in two columns once each
column can hold about 600 px; size these breakpoints with a container query on the page, not the viewport, so the
collapsed or expanded navigation counts. The page header's command bar sits at the page's right edge.

## Reporting back

End your work with a short report: what you built (routes, components, services), how to try it, what you could not
finish, gaps you found in Core or in other areas, and any follow-ups you recommend. Keep it under 300 words.
