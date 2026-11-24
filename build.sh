#!/bin/bash
# Build the Minecraft Earth 0.33.0 iOS client for a Solace server at http://earth.home.arpa:8080.
#
#   ./build.sh device ORIGINAL.ipa OUT_DIR --identity "Apple Development: ..." --profile app.mobileprovision
#   ./build.sh sim    ORIGINAL.ipa OUT_DIR --team TEAMID
#
# Options:
#   --bundle-id ID      default me.kesku.minecraftearth (must match the profile for device builds)
#   --device MODEL      extra UISupportedDevices entry, e.g. iPhone19,4 (repeatable)
#   --full-inner       fill the unfolded iPhone Duo display with phone-sized controls
#   --team TEAMID       keychain group prefix for sim builds (device builds read it from the profile)
#
# Needs: macOS with Xcode command line tools, python3, git, and the .NET 10 SDK (`dotnet`, or set DOTNET).
set -euo pipefail

PATCHER_REPO=https://github.com/Earth-Restored/Minecraft_Earth_Patcher
PATCHER_COMMIT=ec54101c44ba16f3576e61efd2b5301df9c1814b
APP=minecraftearthtf.app
HERE=$(cd "$(dirname "$0")" && pwd)
P=/usr/libexec/PlistBuddy
DOTNET=${DOTNET:-dotnet}

usage() { sed -n 2,14p "$0" | sed 's/^# \{0,1\}//'; exit 2; }
[ $# -ge 3 ] || usage
MODE=$1 IPA=$(cd "$(dirname "$2")" && pwd)/$(basename "$2") OUT=$3; shift 3
case $MODE in device|sim) ;; *) usage ;; esac

BUNDLE_ID=me.kesku.minecraftearth IDENTITY= PROFILE= TEAM= DEVICES=() DUO_ARGS=()
while [ $# -gt 0 ]; do
  case $1 in
    --identity) IDENTITY=$2; shift 2 ;;
    --profile) PROFILE=$2; shift 2 ;;
    --bundle-id) BUNDLE_ID=$2; shift 2 ;;
    --team) TEAM=$2; shift 2 ;;
    --full-inner) DUO_ARGS=(--full-inner); shift ;;
    --device) DEVICES+=("$2"); shift 2 ;;
    *) usage ;;
  esac
done
if [ "$MODE" = device ] && { [ -z "$IDENTITY" ] || [ -z "$PROFILE" ]; }; then
  echo "device builds need --identity and --profile" >&2; exit 2
fi
if [ "$MODE" = sim ] && [ -z "$TEAM" ]; then echo "sim builds need --team" >&2; exit 2; fi

mkdir -p "$OUT"; OUT=$(cd "$OUT" && pwd)
WORK=$OUT/work; rm -rf "$WORK"; mkdir -p "$WORK"

# 1. Official patcher, pinned
CACHE=$HERE/.cache/Minecraft_Earth_Patcher
if [ "$(git -C "$CACHE" rev-parse HEAD 2>/dev/null)" != "$PATCHER_COMMIT" ]; then
  rm -rf "$CACHE"; git clone -q "$PATCHER_REPO" "$CACHE"; git -C "$CACHE" checkout -q "$PATCHER_COMMIT"
fi
"$DOTNET" build -c Release -v quiet -nologo -o "$HERE/.cache/driver" "$HERE/driver/IpaDriver.csproj" >/dev/null
"$DOTNET" "$HERE/.cache/driver/IpaDriver.dll" "$IPA" "$WORK/patched.ipa" "$WORK/decoded" >"$WORK/patcher.log" 2>&1 \
  || { tail -20 "$WORK/patcher.log"; exit 1; }

# The patcher's own zip has bad CRCs, so take the original IPA and drop in the three files it changes
cd "$WORK"
unzip -q "$IPA"
mkdir patched && (cd patched && unzip -q ../patched.ipa \
  "Payload/$APP/Info.plist" "Payload/$APP/minecraftearthtf" "Payload/$APP/Frameworks/Xal.framework/Xal")
A=$WORK/Payload/$APP
for f in Info.plist minecraftearthtf Frameworks/Xal.framework/Xal; do cp "patched/Payload/$APP/$f" "$A/$f"; done
rm -rf "$A/SC_Info" "$A/_CodeSignature" patched patched.ipa decoded

# 2. Changes the patcher doesn't make
$P -c "Set :CFBundleIdentifier $BUNDLE_ID" "$A/Info.plist"
for d in "${DEVICES[@]+"${DEVICES[@]}"}"; do
  $P -c 'Print :UISupportedDevices' "$A/Info.plist" | grep -qx "    $d" \
    || $P -c "Add :UISupportedDevices:1 string $d" "$A/Info.plist"
done
# PlayFab -> http://<titleid>.home.arpa:8080 (needs DNS for 20ca2.home.arpa and 11509.home.arpa)
python3 -I "$HERE/patch_playfab.py" "$A/minecraftearthtf"
# Skip the "Time to Sunset" modal shown on every launch
python3 -I "$HERE/patch_sunset.py" "$A/minecraftearthtf"
# ATS: allow plain HTTP to home.arpa and its subdomains
$P -c 'Add :NSAppTransportSecurity:NSExceptionDomains:home.arpa dict' \
   -c 'Add :NSAppTransportSecurity:NSExceptionDomains:home.arpa:NSExceptionAllowsInsecureHTTPLoads bool true' \
   -c 'Add :NSAppTransportSecurity:NSExceptionDomains:home.arpa:NSIncludesSubdomains bool true' "$A/Info.plist"
plutil -lint -s "$A/Info.plist"

# XAL needs keychain sharing for com.microsoft.xal and com.microsoft.adalcache, or sign-in fails before any request
entitlements() {  # team id -> Entitlements.plist
  python3 -I - "$1" "$BUNDLE_ID" "$WORK/Entitlements.plist" <<'EOF'
import plistlib, sys
team, bundle, out = sys.argv[1:]
plistlib.dump({
    "application-identifier": f"{team}.{bundle}",
    "com.apple.developer.team-identifier": team,
    "get-task-allow": True,
    "keychain-access-groups": [f"{team}.{bundle}", f"{team}.com.microsoft.xal", f"{team}.com.microsoft.adalcache"],
}, open(out, "wb"))
EOF
}

# 3. Simulator (arm64 Mac): retag every Mach-O as iOS Simulator
SDK=iphoneos TARGET=arm64-apple-ios12.0
if [ "$MODE" = sim ]; then
  SDK=iphonesimulator TARGET=arm64-apple-ios12.0-simulator
  XAL_HEADER=$WORK/xal-header.bin
  dd if="$A/Frameworks/Xal.framework/Xal" of="$XAL_HEADER" bs=1 skip=4096 count=640 2>/dev/null
  for f in "$A/minecraftearthtf" "$A"/Frameworks/*.framework/*; do
    [ -f "$f" ] && file "$f" | grep -q Mach-O || continue
    vtool -set-build-version iossim 12.0 13.0 -replace -output "$f.sim" "$f" && mv "$f.sim" "$f"
  done
  # vtool zeroes Xal's header padding, but the patcher keeps its redirect strings there (0x1000-0x1280)
  dd if="$XAL_HEADER" of="$A/Frameworks/Xal.framework/Xal" bs=1 seek=4096 conv=notrunc 2>/dev/null
fi

# 4. Native iPhone Duo sizing and the single-scene bridge required by iOS 27.
# Patch after vtool so it doesn't overwrite the main executable's SDK version.
python3 -I "$HERE/patch_duo.py" "$A" "${DUO_ARGS[@]+"${DUO_ARGS[@]}"}"
if [ "${DUO_ARGS[0]:-}" = --full-inner ]; then
  python3 -I "$HERE/patch_duo_full_inner.py" "$A"
fi
LIFECYCLE="$A/Frameworks/SolaceScene.dylib"
xcrun --sdk "$SDK" clang -target "$TARGET" -fobjc-arc -dynamiclib -framework UIKit -framework CoreGraphics \
  -install_name @rpath/SolaceScene.dylib -o "$LIFECYCLE" "$HERE/SolaceScene.m"

if [ "$MODE" = device ]; then
  TEAM=$(security cms -D -i "$PROFILE" | plutil -extract TeamIdentifier.0 raw -)
  entitlements "$TEAM"
  cp "$PROFILE" "$A/embedded.mobileprovision"
  for f in "$A"/Frameworks/*.framework; do
    codesign --force --sign "$IDENTITY" --timestamp=none --generate-entitlement-der "$f" 2>/dev/null
  done
  codesign --force --sign "$IDENTITY" --timestamp=none "$LIFECYCLE" 2>/dev/null
  codesign --force --sign "$IDENTITY" --timestamp=none --generate-entitlement-der \
    --entitlements "$WORK/Entitlements.plist" "$A" 2>/dev/null
  codesign --verify --deep --strict "$A"
  rm -f "$OUT/Minecraft_Earth_Solace.ipa"
  (cd "$WORK" && ditto -c -k --keepParent Payload "$OUT/Minecraft_Earth_Solace.ipa")
  echo "$OUT/Minecraft_Earth_Solace.ipa"
  exit 0
fi

# The Simulator refuses ad-hoc signatures that carry entitlements (launch error 163), and without the
# keychain groups XAL gets -34018. Embed them as __TEXT,__entitlements, the way Xcode does.
entitlements "$TEAM"
codesign --remove-signature "$A/minecraftearthtf" 2>/dev/null || true
python3 -I "$HERE/add_entitlements_section.py" "$A/minecraftearthtf" "$WORK/Entitlements.plist"
for f in "$A"/Frameworks/*.framework; do codesign --force --sign - --timestamp=none "$f" 2>/dev/null; done
codesign --force --sign - --timestamp=none "$LIFECYCLE" 2>/dev/null
codesign --force --sign - --timestamp=none "$A" 2>/dev/null
codesign --verify --deep --strict "$A"
rm -rf "$OUT/$APP"; mv "$A" "$OUT/$APP"
echo "$OUT/$APP"
