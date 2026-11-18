#!/bin/sh
# Builds ADBora for Linux (self-contained single file, no .NET installation needed):
#   dist/ADBora-<ver>-Linux-<arch>.tar.gz   folder "ADBora" with the program, install-desktop.sh, README, LICENSE
# Requirements (build PC only): .NET 10 SDK.  Usage: build/build-linux.sh [x64|arm64]
set -e
arch="${1:-x64}"
root="$(cd "$(dirname "$0")/.." && pwd)"
project="$root/src/ADBora.App/ADBora.App.csproj"
dist="$root/dist"
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$project" | head -1)
out="$dist/linux-$arch/ADBora"
echo "Building ADBora $version for Linux ($arch) ..."

rm -rf "$dist/linux-$arch" "$dist/ADBora-$version-Linux-$arch.tar.gz"
dotnet publish "$project" -c Release -r "linux-$arch" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -o "$out"

# Start check: open the main window once with every tab as start tab (catches start-up crashes)
if [ "$arch" = "x64" ] && [ "$(uname -m)" = "x86_64" ] && [ -n "$DISPLAY$WAYLAND_DISPLAY" ]; then
    for tab in 0 1 2 3 4 5 6 7; do
        timeout 30 "$out/ADBora" --smoke-test --start-tab $tab || { echo "Smoke test failed (start tab $tab) - see error.log"; exit 1; }
    done
    echo "Smoke test passed (all start tabs)."
fi

cp "$root/README.md" "$root/LICENSE" "$out/"
cp "$root/Logo.png" "$out/ADBora.png"
# Marks a release build: enables the automatic update (Build Info tab)
echo "ADBora release build - automatic updates replace the files of this folder." > "$out/release.flag"

cat > "$out/install-desktop.sh" <<'SH'
#!/bin/sh
# Adds ADBora to the application menu of the current user (and removes it with --remove).
dir="$(cd "$(dirname "$0")" && pwd)"
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
icons="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"
if [ "$1" = "--remove" ]; then
    rm -f "$apps/adbora.desktop" "$icons/adbora.png"
    echo "ADBora removed from the application menu."
    exit 0
fi
mkdir -p "$apps" "$icons"
cp "$dir/ADBora.png" "$icons/adbora.png"
cat > "$apps/adbora.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=ADBora
GenericName=Android Device Toolbox
Comment=APK backup and install, speed test, headset casting and ADB console
Exec="$dir/ADBora"
Icon=adbora
Terminal=false
Categories=Development;Utility;
Keywords=adb;android;quest;vr;headset;apk;
StartupWMClass=ADBora
DESKTOP
update-desktop-database "$apps" >/dev/null 2>&1 || true
echo "ADBora added to the application menu."
SH
chmod +x "$out/install-desktop.sh" "$out/ADBora"

tar -C "$dist/linux-$arch" -czf "$dist/ADBora-$version-Linux-$arch.tar.gz" ADBora
rm -rf "$dist/linux-$arch"
echo "Done: $dist/ADBora-$version-Linux-$arch.tar.gz"
