#!/bin/sh
# Builds the installed filesystem tree of AllInOne PDF in $1, from the inputs build-packages.ps1 staged in /work/in.
# Runs inside the packaging containers (on the container's own filesystem, so the permissions set here stick).
# Usage: stage-tree.sh <root> deb|rpm
set -eu
IN=/work/in
ROOT="$1"
KIND="$2"
ID=com.therandomadmin.AllInOnePdf
OPT="$ROOT/opt/allinonepdf"
DOC="$ROOT/usr/share/doc/allinonepdf"

rm -rf "$ROOT"
mkdir -p "$OPT"
cp -r "$IN/app/." "$OPT/"
# Installed copies keep their data in ~/.config/AllInOnePdf: no portable marker. No debug symbols either.
# The licence and notices move to /usr/share/doc (the app shows its own embedded copy of the notices).
rm -f "$OPT/portable.txt" "$OPT/LICENSE.txt" "$OPT/THIRD-PARTY-NOTICES.md"
find "$OPT" \( -name '*.pdb' -o -name '*.dbg' \) -exec rm -f {} +

install -D -m 0755 "$IN/allinonepdf" "$ROOT/usr/bin/allinonepdf"
install -D -m 0644 "$IN/share/$ID.desktop" "$ROOT/usr/share/applications/$ID.desktop"
install -D -m 0644 "$IN/share/$ID.metainfo.xml" "$ROOT/usr/share/metainfo/$ID.metainfo.xml"
for size in 256 128 64 48 32 16; do
  install -D -m 0644 "$IN/icons/$size.png" "$ROOT/usr/share/icons/hicolor/${size}x${size}/apps/$ID.png"
done
mkdir -p "$ROOT/usr/share/man/man1"
gzip -9n -c "$IN/allinonepdf.1" > "$ROOT/usr/share/man/man1/allinonepdf.1.gz"
install -D -m 0644 "$IN/THIRD-PARTY-NOTICES.md" "$DOC/THIRD-PARTY-NOTICES.md"
if [ "$KIND" = deb ]; then
  install -D -m 0644 "$IN/deb/copyright" "$DOC/copyright"
  gzip -9n -c "$IN/deb/changelog" > "$DOC/changelog.gz"
  install -D -m 0644 "$IN/deb/lintian-overrides" "$ROOT/usr/share/lintian/overrides/allinonepdf"
else
  install -D -m 0644 "$IN/LICENSE" "$DOC/LICENSE"
fi

find "$ROOT" -type d -exec chmod 0755 {} +
find "$ROOT" -type f -exec chmod 0644 {} +
chmod 0755 "$ROOT/usr/bin/allinonepdf" "$OPT/AllInOnePdf"
[ -f "$OPT/createdump" ] && chmod 0755 "$OPT/createdump"
# Shared libraries: Debian policy wants 0644 (dlopen needs no execute bit), Fedora's convention is 0755.
if [ "$KIND" = rpm ]; then
  find "$OPT" -name '*.so' -exec chmod 0755 {} +
fi
