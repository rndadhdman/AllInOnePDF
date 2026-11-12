AllInOne PDF portable (Linux x64)
=================================

Run:        ./AllInOnePdf.sh
App:        app/                (the program and its runtime; nothing to install)
Settings:   Data/               (preferences, themes, history, browser cache) - delete it to reset
Output:     Data/Output/        (default folder for converted files; "Save next to source" is on by default)

Self-contained: the .NET runtime is included; nothing is installed and nothing is written outside this
folder. If the executable bit was lost (for example after copying through Windows), run:
    chmod +x AllInOnePdf.sh app/AllInOnePdf

tar.gz or zip
  AllInOnePdf-linux-x64.tar.gz keeps the executable bits: extract it with 'tar -xzf' and run ./AllInOnePdf.sh.
  AllInOnePdf-linux-x64.zip has the same files, but zip files do not store Unix permissions, so after unzipping run
      chmod +x AllInOnePdf.sh app/AllInOnePdf
  once (or start it with 'sh AllInOnePdf.sh'; the launcher repairs the bit on app/AllInOnePdf itself).

Installing instead
  The .deb (Debian 12+, Ubuntu 22.04+) and .rpm (Fedora 40+) packages install AllInOne PDF system-wide with a menu
  entry and the 'allinonepdf' command; they keep settings in ~/.config/AllInOnePdf instead of a Data folder.

Requirements (system libraries only)
  - WebKitGTK 4.1 and GTK 3, present on most desktops:
      Debian / Ubuntu:  sudo apt install libwebkit2gtk-4.1-0 libgtk-3-0
      Fedora:           sudo dnf install webkit2gtk4.1 gtk3
      Arch:             sudo pacman -S webkit2gtk-4.1 gtk3
  - libicu for locale-aware dates (optional; AllInOnePdf.sh falls back to invariant globalisation when it is missing).
  - fontconfig and freetype (already present on any desktop).

Desktop entry
  Edit allinonepdf.desktop so Exec= and Icon= point at this folder, then copy it to ~/.local/share/applications/.

Diagnostics
  ./app/AllInOnePdf --paths          prints where data is stored
  ./app/AllInOnePdf --check-assets   verifies the bundled interface files

Licence: MIT (app/LICENSE.txt). Third-party notices are listed in the project README.
