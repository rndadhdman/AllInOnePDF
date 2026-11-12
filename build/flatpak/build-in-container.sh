#!/bin/sh
# Runs inside the allinonepdf-flatpak image (docker/Dockerfile.flatpak), started by build-flatpak.ps1 with
#   /work/src  the staged manifest and its sources (read-only use)
#   /work/out  where the bundle is written
# Builds the Flatpak, bundles it, then installs the bundle and checks it. Usage: build-in-container.sh <version> [--no-validate]
set -eu
VERSION="$1"
VALIDATE=1
[ "${2:-}" = "--no-validate" ] && VALIDATE=0
APP=com.therandomadmin.AllInOnePdf
SRC=/work/src
BUNDLE="/work/out/AllInOnePdf-$VERSION.flatpak"
# ostree repos and build dirs need a Linux file system (hard links, xattrs), not the bind mount from the host.
TMP=/var/tmp/aio-flatpak
rm -rf "$TMP"; mkdir -p "$TMP"
export XDG_RUNTIME_DIR="$TMP/xdg"; mkdir -p -m 700 "$XDG_RUNTIME_DIR"

fail() { echo "FAILED: $*" >&2; exit 1; }
# 'flatpak run' needs a system bus and a session bus; a container has neither, so start a system bus once and give
# each run a private session bus.
start_system_bus() {
  [ -S /run/dbus/system_bus_socket ] && return 0
  mkdir -p /run/dbus
  dbus-daemon --system --fork || fail "dbus-daemon --system"
}
app() { dbus-run-session -- flatpak run "$APP" "$@"; }
step() { echo; echo "== $*"; }

step "Validate the desktop file and the metainfo"
desktop-file-validate "$SRC/$APP.desktop" || fail "desktop-file-validate"
appstreamcli validate --no-net --explain "$SRC/$APP.metainfo.xml" || fail "appstreamcli validate"

step "flatpak-builder"
# rofiles-fuse needs FUSE, which a container often lacks; it only guards against modules editing their sources.
flatpak-builder --disable-rofiles-fuse --force-clean --disable-updates --sandbox \
  --state-dir="$TMP/state" --repo="$TMP/repo" "$TMP/build" "$SRC/$APP.yml" || fail "flatpak-builder"

step "flatpak build-bundle"
mkdir -p /work/out
rm -f "$BUNDLE"
# The runtime repo lets 'flatpak install <bundle>' fetch the GNOME runtime from Flathub on the user's machine.
flatpak build-bundle --runtime-repo=https://dl.flathub.org/repo/flathub.flatpakrepo \
  "$TMP/repo" "$BUNDLE" "$APP" || fail "build-bundle"
# Written as root; let the host user (a CI runner on Linux) move it.
chmod a+rw "$BUNDLE"
ls -l "$BUNDLE"

[ "$VALIDATE" = 1 ] || { echo "RESULT: OK (not validated)"; exit 0; }

step "Install the bundle"
start_system_bus
flatpak install --user -y --noninteractive "$BUNDLE" || fail "flatpak install"
FILES="$(flatpak info --user --show-location "$APP")/files"

step "flatpak info"
flatpak info --user "$APP" | tee "$TMP/info.txt"
grep -Eq "Version: *$VERSION\$" "$TMP/info.txt" || fail "flatpak info does not show version $VERSION"

step "allinonepdf --help"
app --help > "$TMP/help.txt" 2>"$TMP/help.err" || { cat "$TMP/help.err"; fail "--help exit code"; }
cat "$TMP/help.txt"
grep -q -- "--paths" "$TMP/help.txt" || fail "--help printed no option list"

step "allinonepdf --check-assets"
app --check-assets > "$TMP/assets.txt" 2>&1 || { cat "$TMP/assets.txt"; fail "--check-assets exit code"; }
cat "$TMP/assets.txt"
grep -q "All assets resolved" "$TMP/assets.txt" || fail "--check-assets did not resolve every asset"

step "allinonepdf --paths"
app --paths > "$TMP/paths.txt" 2>&1 || { cat "$TMP/paths.txt"; fail "--paths exit code"; }
cat "$TMP/paths.txt"
grep -Eq "^portable: *False" "$TMP/paths.txt" || fail "--paths reports portable mode"
grep -Eq "^data: *$HOME/\.var/app/$APP/config/AllInOnePdf\$" "$TMP/paths.txt" || fail "--paths data folder is not under ~/.var/app/$APP/config"

step "Installed files"
[ ! -e "$FILES/lib/allinonepdf/portable.txt" ] || fail "portable.txt was shipped"
SRC_DLLS=$(find "$SRC/publish" -name '*.dll' | wc -l)
APP_DLLS=$(find "$FILES/lib/allinonepdf" -name '*.dll' | wc -l)
echo "dlls: publish folder $SRC_DLLS, installed app $APP_DLLS"
[ "$SRC_DLLS" -eq "$APP_DLLS" ] || fail "dll count differs"
for f in "share/applications/$APP.desktop" "share/metainfo/$APP.metainfo.xml" "share/icons/hicolor/256x256/apps/$APP.png" bin/allinonepdf; do
  [ -e "$FILES/$f" ] || fail "missing /app/$f"
done
desktop-file-validate "$FILES/share/applications/$APP.desktop" || fail "installed desktop file"
appstreamcli validate --no-net "$FILES/share/metainfo/$APP.metainfo.xml" || fail "installed metainfo"
echo "installed size: $(du -sh "$FILES" | cut -f1)"

flatpak uninstall --user -y --noninteractive "$APP" >/dev/null
rm -rf "$TMP"
echo
echo "RESULT: OK"
