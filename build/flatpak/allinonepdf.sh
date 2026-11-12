#!/bin/sh
# Launcher for the AllInOne PDF Flatpak: /app/bin/allinonepdf (the manifest's command).
# Settings live in ~/.var/app/com.therandomadmin.AllInOnePdf/config/AllInOnePdf (XDG config inside the sandbox).
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# The GNOME runtime ships ICU; fall back to invariant globalisation should a runtime ever come without it.
if ! ls /usr/lib/x86_64-linux-gnu/libicuuc.so.* /usr/lib/libicuuc.so.* >/dev/null 2>&1; then
  export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
fi

exec /app/lib/allinonepdf/AllInOnePdf "$@"
