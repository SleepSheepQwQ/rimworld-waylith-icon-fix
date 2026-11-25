#!/usr/bin/env bash
# Build, verify, package, and optionally install GlyphFix 2.7.9.
#
# Usage:
#   ./repair-glyphfix.sh
#   ./repair-glyphfix.sh --install /path/to/RimWorld/Mods
#   ./repair-glyphfix.sh --install /path/to/RimWorld/Mods --force
#   ./repair-glyphfix.sh --skip-tests
#
# This script never edits ModsConfig.xml and never targets a hard-coded device path. Enable GlyphFix
# in RimWorld's mod list, then restart the game.
set -euo pipefail

trap 'code=$?; echo "GlyphFix setup failed (exit $code). No game files were changed unless installation had already started." >&2' ERR

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
INSTALL_ROOT=""
FORCE=0
RUN_TESTS=1

usage() {
    cat <<'EOF'
Build, verify, package, and optionally install GlyphFix 2.7.9.

Usage:
  ./repair-glyphfix.sh
  ./repair-glyphfix.sh --install /path/to/RimWorld/Mods
  ./repair-glyphfix.sh --install /path/to/RimWorld/Mods --force
  ./repair-glyphfix.sh --skip-tests

The script never edits ModsConfig.xml. Enable GlyphFix in RimWorld's mod list, then restart.
EOF
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --install)
            [ "$#" -ge 2 ] || { echo "missing path after --install" >&2; exit 2; }
            INSTALL_ROOT=$2
            shift 2
            ;;
        --force)
            FORCE=1
            shift
            ;;
        --skip-tests)
            RUN_TESTS=0
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "unknown argument: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

command -v dotnet >/dev/null 2>&1 || {
    echo "dotnet is required (Termux: pkg install dotnet)" >&2
    exit 1
}

echo "[1/5] source checks"
if git -C "$ROOT" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    git -C "$ROOT" diff --check
else
    echo "source archive detected; git diff check skipped"
fi
V_CS=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/mod/AgentTouchFix.csproj")
V_XML=$(sed -n 's:.*<modVersion>\(.*\)</modVersion>.*:\1:p' "$ROOT/mod/About/About.xml")
if [ -z "$V_CS" ] || [ "$V_CS" != "$V_XML" ]; then
    echo "version mismatch: csproj=${V_CS:-missing} About.xml=${V_XML:-missing}" >&2
    exit 1
fi
echo "version $V_CS"

if [ "$RUN_TESTS" -eq 1 ]; then
    echo "[2/5] tests"
    dotnet test "$ROOT/tests/TexSt/TexSt.csproj" -c Release --nologo
    dotnet test "$ROOT/tests/DdsAltSt/DdsAltSt.csproj" -c Release --nologo
    dotnet run --project "$ROOT/tests/BuildingGate/BuildingGate.csproj" -c Release
    python3 "$ROOT/tests/BuildingGate/test_real_host_gate.py"
else
    echo "[2/5] tests skipped by request"
fi

echo "[3/5] build"
dotnet build "$ROOT/mod/AgentTouchFix.csproj" -c Release --nologo

DLL="$ROOT/mod/bin/Release/net10.0/GlyphFix.dll"
ABOUT="$ROOT/mod/About/About.xml"
[ -f "$DLL" ] || { echo "build output missing: $DLL" >&2; exit 1; }
[ -f "$ABOUT" ] || { echo "About.xml missing: $ABOUT" >&2; exit 1; }

STAGE="$ROOT/dist/GlyphFix"
rm -rf "$STAGE"
mkdir -p "$STAGE/About" "$STAGE/Assemblies"
cp "$ABOUT" "$STAGE/About/About.xml"
cp "$DLL" "$STAGE/Assemblies/GlyphFix.dll"

echo "[4/5] package"
ZIP="$ROOT/dist/GlyphFix.zip"
rm -f "$ZIP"
if command -v zip >/dev/null 2>&1; then
    (cd "$ROOT/dist" && zip -qr "$ZIP" GlyphFix)
elif command -v python3 >/dev/null 2>&1; then
    (cd "$ROOT/dist" && python3 -m zipfile -c "$ZIP" GlyphFix)
else
    echo "warning: neither zip nor python3 is installed; staged mod remains at $STAGE" >&2
    echo "         Termux: pkg install zip" >&2
fi

echo "[5/5] result"
sha256sum "$DLL"
if [ -f "$ZIP" ]; then
    sha256sum "$ZIP"
fi
echo
echo "Covered image-loss paths:"
echo "  - build-menu icons and invalid CutoutComplexUI materials"
echo "  - colonist/animal/table icons through Thing and ThingDef overloads"
echo "  - animals with CutoutComplex body graphics and directional/mask keys"
echo "  - Odyssey unique weapons with .png.dds, BC4 masks, Graphic_Random, pack DDS"
echo "  - DDS format/mip/shadow, case/extension, BadTex, and atlas-hole fallbacks"

if [ -n "$INSTALL_ROOT" ]; then
    mkdir -p "$INSTALL_ROOT"
    TARGET=""
    for candidate in "$INSTALL_ROOT"/*; do
        [ -d "$candidate" ] || continue
        leaf=$(basename "$candidate")
        lowered=$(printf '%s' "$leaf" | tr '[:upper:]' '[:lower:]')
        if [ "$lowered" = "glyphfix" ]; then
            TARGET=$candidate
            break
        fi
    done
    [ -n "$TARGET" ] || TARGET="$INSTALL_ROOT/GlyphFix"
    if [ -e "$TARGET" ] && [ "$FORCE" -ne 1 ]; then
        echo "refusing to overwrite existing $TARGET (pass --force)" >&2
        exit 1
    fi
    if [ -e "$TARGET" ]; then
        BACKUP_DIR="$INSTALL_ROOT/.glyphfix-backups"
        mkdir -p "$BACKUP_DIR"
        BACKUP="$BACKUP_DIR/$(basename "$TARGET").$(date +%Y%m%d-%H%M%S)"
        mv "$TARGET" "$BACKUP"
        echo "previous install moved to $BACKUP"
    fi
    cp -a "$STAGE" "$TARGET"
    echo "installed: $TARGET"
    echo "Open RimWorld's mod list, enable GlyphFix, and restart the game."
else
    echo "staged mod: $STAGE"
    if [ -f "$ZIP" ]; then
        echo "package:    $ZIP"
    fi
fi
