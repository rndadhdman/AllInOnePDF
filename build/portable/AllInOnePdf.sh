#!/bin/sh
# Launcher for the portable Linux build of AllInOne PDF: the one file at the root, the app lives in ./app.
# - runs from any folder (USB stick, home directory, /opt)
# - keeps settings in ./Data (see app/portable.txt)
# - falls back to invariant globalisation when libicu is not installed
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
APP="$DIR/app/AllInOnePdf"
cd "$DIR"

if [ ! -f "$APP" ]; then
  echo "AllInOne PDF could not start: $APP is missing. Keep AllInOnePdf.sh together with its app and Data folders." >&2
  exit 2
fi

if [ ! -x "$APP" ]; then
  chmod +x "$APP" 2>/dev/null || true
fi

# ldconfig lives in /sbin, which is not on a normal user's PATH on Debian.
LDCONFIG="$(command -v ldconfig 2>/dev/null || echo /sbin/ldconfig)"

if ! "$LDCONFIG" -p 2>/dev/null | grep -q 'libwebkit2gtk-4\.1'; then
  echo "AllInOne PDF needs WebKitGTK 4.1 (e.g. 'sudo apt install libwebkit2gtk-4.1-0' on Debian/Ubuntu)." >&2
fi

if ! "$LDCONFIG" -p 2>/dev/null | grep -q 'libicuuc'; then
  export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
exec "$APP" "$@"
