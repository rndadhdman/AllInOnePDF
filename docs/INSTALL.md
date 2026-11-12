# Installing AllInOne PDF

Every release on GitHub carries the packages below. All of them contain the app together with its own .NET runtime,
so nothing else needs to be installed apart from the system web view noted for each platform.

## Windows

Windows 10 or 11, 64-bit (x64). There are two packages: an installer and a portable zip. They can live side by side.

### Installer (MSI)

`AllInOnePdf-<version>-win-x64.msi`

1. Double-click the file. The installer is not code-signed yet, so Windows SmartScreen may show
   "Windows protected your PC": choose **More info**, then **Run anyway**.
2. Accept the MIT licence, keep or change the folder (default `C:\Program Files\AllInOne PDF`), and tick
   **Add a shortcut to the desktop** if you want one (it is off by default).
3. Windows asks for administrator permission: the app is installed for every user of the PC.
4. On the last page, **Launch AllInOne PDF** starts the app.

What it does:

* Installs the app to `Program Files\AllInOne PDF` and adds **AllInOne PDF** to the Start menu (for all users) and,
  if you ticked it, to the public desktop.
* Shows up in **Settings › Apps › Installed apps** (with links to the project page and therandomadmin.com).
  Uninstall it from there. Repair and Modify are not offered: uninstall and install again instead.
* A newer MSI upgrades an older one in place; your settings are kept. Installing an older MSI over a newer one is
  refused (uninstall first).
* It does not register file associations: open files from inside the app.
* Silent install for administrators: `msiexec /i AllInOnePdf-<version>-win-x64.msi /qn` (add
  `INSTALLDESKTOPSHORTCUT=1` for the desktop shortcut, `INSTALLFOLDER="D:\Apps\AllInOne PDF"` for another folder).

Your data is not in the program folder. Settings, themes, history, Creator publications and the web view's cache are
kept per user in `%APPDATA%\AllInOnePdf`, and converted files go to `Documents\AllInOne PDF` by default.
**Uninstalling removes only the program folder and the shortcuts; that data stays.** Delete
`%APPDATA%\AllInOnePdf` yourself if you want a clean slate. `"C:\Program Files\AllInOne PDF\AllInOnePdf.exe" --paths`
prints the folders in use.

**WebView2.** The app draws its window with the Microsoft Edge WebView2 Runtime, which is part of Windows 11 and is
installed on Windows 10 by Windows Update. The installer does not include it and installs fine without it; if it cannot
find the runtime, the last page of the installer says so. Get it from
[Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/) ("Evergreen Bootstrapper") if the app does not
open.

### Portable (zip)

`AllInOnePdfPortable-win-x64.zip`: nothing is installed and nothing is written outside the folder.

1. Unzip it anywhere (a USB stick works). Windows may block downloaded files: right-click the zip,
   **Properties**, tick **Unblock** before extracting.
2. Run `AllInOnePdfPortable.exe`.

Settings and history are kept in the `Data` folder beside it (delete it to reset); converted files go to
`Data\Output` by default. The folder follows the PortableApps.com format, so it can be copied into
`<drive>\PortableApps\`. On a PC without WebView2, drop Microsoft's "Fixed Version" runtime into
`App\AllInOnePdf\WebView2Runtime\` and the app uses it. See `README.txt` in the zip for details.

## Linux

AllInOne PDF for Linux (x86-64) comes in four forms. All of them include the .NET runtime, so nothing else needs
to be installed apart from a few system libraries that every desktop already has.

| Download | For | Settings live in |
|----------|-----|------------------|
| `allinonepdf_<version>_amd64.deb` | Debian 12+, Ubuntu 22.04+ and derivatives (Mint, Pop!_OS, elementary) | `~/.config/AllInOnePdf` |
| `allinonepdf-<version>-1.x86_64.rpm` | Fedora 40+ (and other distributions with WebKitGTK 4.1) | `~/.config/AllInOnePdf` |
| `AllInOnePdf-<version>.flatpak` | Any distribution with Flatpak | `~/.var/app/com.therandomadmin.AllInOnePdf/config/AllInOnePdf` |
| `AllInOnePdf-linux-x64.tar.gz` / `.zip` | Any distribution, no installation (USB stick, home folder) | `Data/` beside the app |

### Debian and Ubuntu (deb)

```bash
sudo apt install ./allinonepdf_1.2.0_amd64.deb
```

Use `apt install ./file.deb` (with the `./`) rather than `dpkg -i`: apt downloads the libraries the package needs.
A double-click in the Software or GDebi app does the same.

### Fedora (rpm)

```bash
sudo dnf install ./allinonepdf-1.2.0-1.x86_64.rpm
```

The packages are not signed, so dnf says it skipped the OpenPGP check for the local file; that is expected.

### After installing a deb or rpm

- **AllInOne PDF** appears in the applications menu (Office), and PDFs, Markdown, Word, Excel, PowerPoint, EPUB,
  RTF, HTML and text files offer it under *Open with*.
- From a terminal: `allinonepdf`, or `allinonepdf file.md` to open a file.
- The program lives in `/opt/allinonepdf`; `/usr/bin/allinonepdf` is a small launcher script.
- `allinonepdf --paths` prints where your data goes, `allinonepdf --check-assets` checks the installation, and
  `allinonepdf --help` lists the options (`man allinonepdf` has the same).

Where your data goes:

| What | Where |
|------|-------|
| Settings, themes, history, Creator publications, browser cache, crash logs | `~/.config/AllInOnePdf` |
| Converted files (default output folder) | `~/Documents/AllInOne PDF` (or `~/AllInOne PDF` without a Documents folder) |

To start fresh, close the app and delete `~/.config/AllInOnePdf`. Set `ALLINONEPDF_DATA_DIR` to keep the data
somewhere else.

What the packages depend on:

| Library | Debian / Ubuntu package | Fedora package |
|---------|-------------------------|----------------|
| WebKitGTK 4.1 (the app window) | `libwebkit2gtk-4.1-0` | `webkit2gtk4.1` |
| GTK 3 | `libgtk-3-0` | `gtk3` |
| Desktop notifications | `libnotify4` | `libnotify` |
| Fonts for PDF rendering | `libfontconfig1`, `libfreetype6` | `fontconfig`, `freetype` |
| ICU (locale-aware dates; recommended, optional) | `libicu70` ... `libicu78` | `libicu` |

Without ICU the launcher starts the app with invariant globalisation (dates and numbers in a neutral format)
instead of failing.

Updating: install the newer package the same way; it replaces the old one and keeps `~/.config/AllInOnePdf`.

Uninstalling:

```bash
sudo apt remove allinonepdf      # Debian / Ubuntu
sudo dnf remove allinonepdf      # Fedora
```

This removes `/opt/allinonepdf`, the launcher, the menu entry and the icons. Your settings and converted files stay;
delete `~/.config/AllInOnePdf` (and the output folder, if you like) to remove them too.

### Flatpak

`AllInOnePdf-<version>.flatpak` is one file that runs on any Linux distribution with Flatpak, in a sandbox, on the
GNOME runtime (which brings WebKitGTK and GTK, so nothing else needs to be installed).

The bundle uses the GNOME runtime from Flathub, which Flatpak downloads the first time (about 500 MB, shared with
other GNOME apps). Flathub must be configured once; most distributions with Flatpak already have it:

```bash
flatpak remote-add --if-not-exists --user flathub https://dl.flathub.org/repo/flathub.flatpakrepo
```

Then install the bundle (for the current user; drop `--user` to install for everyone, which asks for your password):

```bash
flatpak install --user AllInOnePdf-1.2.0.flatpak
```

Software centres such as GNOME Software and KDE Discover also open `.flatpak` files with a double-click. To update,
install the newer bundle the same way: it replaces the old version and keeps your settings.

Start **AllInOne PDF** from the application menu, or from a terminal:

```bash
flatpak run com.therandomadmin.AllInOnePdf
flatpak run com.therandomadmin.AllInOnePdf --help     # the options
flatpak run com.therandomadmin.AllInOnePdf --paths    # where data goes
```

Settings, themes, history, publications and drafts are kept in the app's own folder,
`~/.var/app/com.therandomadmin.AllInOnePdf/config/AllInOnePdf`. Converted files go next to the source file by
default, or to `~/Documents/AllInOne PDF` when you choose the default output folder. Setting `ALLINONEPDF_DATA_DIR`
moves the data folder, as with the other packages:
`flatpak override --user --env=ALLINONEPDF_DATA_DIR=/path com.therandomadmin.AllInOnePdf`.

Permissions: the app is offline by design, so the Flatpak has no network access. It may read and write files in your
**home folder** (it converts files from anywhere there and writes the result beside the source), and it uses the
display (Wayland or X11) and the GPU. Files outside the home folder, such as USB sticks under `/media` or
`/run/media` or a second disk under `/mnt`, are not visible to the app. To allow them, use
[Flatseal](https://flathub.org/apps/com.github.tchx84.Flatseal) (Filesystem › Other files, add the folder) or a
terminal:

```bash
flatpak override --user --filesystem=/media --filesystem=/run/media com.therandomadmin.AllInOnePdf
flatpak override --user --reset com.therandomadmin.AllInOnePdf    # back to the defaults
```

Uninstall:

```bash
flatpak uninstall --user com.therandomadmin.AllInOnePdf
flatpak uninstall --user --delete-data com.therandomadmin.AllInOnePdf   # also removes the settings folder
flatpak uninstall --unused                                              # removes the GNOME runtime if nothing else uses it
```

### Portable (tar.gz or zip)

1. Extract it anywhere:
   ```bash
   tar -xzf AllInOnePdf-linux-x64.tar.gz
   ```
2. Run `./AllInOnePdf-linux-x64/AllInOnePdf.sh`.

The **zip** has the same files, but zip files do not keep Unix permissions. After unzipping, run once:

```bash
cd AllInOnePdf-linux-x64
chmod +x AllInOnePdf.sh app/AllInOnePdf
```

(`sh AllInOnePdf.sh` also works; the launcher restores the bit on `app/AllInOnePdf` itself.) The portable copy
keeps everything in its own `Data` folder and writes nothing elsewhere. It needs the same libraries as the packages:
`sudo apt install libwebkit2gtk-4.1-0 libgtk-3-0` or `sudo dnf install webkit2gtk4.1 gtk3`. To get a menu entry,
edit `allinonepdf.desktop` so `Exec=` and `Icon=` point at the folder and copy it to `~/.local/share/applications/`.
A portable copy is removed by deleting its folder.

## Building the packages yourself

```powershell
.\build\publish.ps1 -Rid win-x64 -Version 1.2.0                        # Windows portable zip
.\build\publish.ps1 -Rid linux-x64 -Version 1.2.0                      # Linux portable folder, tar.gz and zip
.\build\publish-install.ps1 -Rid win-x64 -Version 1.2.0 -Output publish\install-win-x64
.\build\msi\build-msi.ps1 -PublishDir publish\install-win-x64 -Version 1.2.0 -Output publish
.\build\publish-install.ps1 -Rid linux-x64 -Version 1.2.0 -Output publish\install-linux-x64
.\build\linux\build-packages.ps1 -PublishDir publish\install-linux-x64 -Version 1.2.0 -Output publish
.\build\flatpak\build-flatpak.ps1 -PublishDir publish\install-linux-x64 -Version 1.2.0 -Output publish
```

`build-msi.ps1` uses WiX 5 as a repo-local dotnet tool (`dotnet tool restore`). `build-packages.ps1` and
`build-flatpak.ps1` need Docker: the deb is built with `dpkg-deb --root-owner-group` in Ubuntu 24.04 and the rpm with
`rpmbuild` in Fedora 41 (`docker/Dockerfile.deb-build`, `docker/Dockerfile.rpm-build`); the Flatpak with
flatpak-builder. The desktop entry and AppStream metainfo in `build/linux/share` are shared by the deb, the rpm and
the Flatpak. Check the Linux results with `lintian` (overrides ship in the package) and
`rpmlint -r build/linux/allinonepdf.rpmlintrc`. The release workflow runs all of this on every `v*` tag.
