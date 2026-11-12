#!/bin/sh
# Builds /work/out/$1 (the .deb) in the aio-pkg-deb container from the inputs build-packages.ps1 staged in /work/in.
set -eu
DEB="$1"
R=/tmp/allinonepdf
sh /work/in/stage-tree.sh "$R" deb
mkdir -p "$R/DEBIAN"
size=$(du -sk "$R" | cut -f1)
sed "s/{INSTALLED_SIZE}/$size/" /work/in/deb/control > "$R/DEBIAN/control"
(cd "$R" && find . -type f ! -path './DEBIAN/*' -printf '%P\0' | LC_ALL=C sort -z | xargs -0 md5sum > DEBIAN/md5sums)
chmod 0755 "$R/DEBIAN"
chmod 0644 "$R/DEBIAN/control" "$R/DEBIAN/md5sums"
dpkg-deb --build --root-owner-group -Zxz "$R" "/work/out/$DEB"
dpkg-deb --info "/work/out/$DEB"
