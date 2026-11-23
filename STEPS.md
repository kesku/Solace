# Steps as run (2026-11-23)

This is the shell history that produced the working iOS 0.33.0 client for Solace, kept as-is. Paths are from the original scratch directory:

- `$S`: the scratch directory, which holds these scripts.
- `$W`: `$S/work`
- `$O`: a directory holding the signing identity name, the device UDID, the provisioning profile and the base entitlements, taken from an earlier signed build.

## 1. Official patcher (Earth-Restored/Minecraft_Earth_Patcher at ec54101)

`driver/` calls `IpaProcessor.Run` with the options the patcher UI uses for iOS advanced mode with a custom login server: locator, XAL, PlayFab, Xbox AB/Live redirects, web view, interactive sign-in, no sunset check, app name "Solace", and DRM removed.

```sh
DOTNET_ROOT=$S/tools/dotnet $S/tools/dotnet/dotnet $S/driver/out/IpaDriver.dll \
  Minecraft_Earth_0.33.0_ios_15.0.ipa $W/Solace_official.ipa $W/decoded
```

The patcher's output zip had bad CRCs. Only three of its files differ from the original IPA, so the working build is the original IPA with just those three files replaced.

## 2. Device build

```sh
cd $W; rm -rf Payload patched3
unzip -q Minecraft_Earth_0.33.0_ios_15.0.ipa
mkdir patched3 && cd patched3 && unzip -q $W/Solace_official.ipa \
  'Payload/minecraftearthtf.app/Info.plist' \
  'Payload/minecraftearthtf.app/minecraftearthtf' \
  'Payload/minecraftearthtf.app/Frameworks/Xal.framework/Xal' && cd ..
A=$W/Payload/minecraftearthtf.app; B=$W/patched3/Payload/minecraftearthtf.app
for f in Info.plist minecraftearthtf Frameworks/Xal.framework/Xal; do cp $B/$f $A/$f; done
rm -rf $A/SC_Info $A/_CodeSignature

P=/usr/libexec/PlistBuddy
$P -c 'Set :CFBundleIdentifier me.kesku.minecraftearth' $A/Info.plist
$P -c 'Add :UISupportedDevices:1 string iPhone19,4' $A/Info.plist

# PlayFab -> http://<titleid>.home.arpa:8080 (needs DNS CNAMEs 20ca2/11509.home.arpa -> earth.home.arpa)
python3 -I $S/patch_playfab.py $A/minecraftearthtf
# ATS: allow plain HTTP to home.arpa
$P -c 'Add :NSAppTransportSecurity:NSExceptionDomains:home.arpa dict' \
   -c 'Add :NSAppTransportSecurity:NSExceptionDomains:home.arpa:NSExceptionAllowsInsecureHTTPLoads bool true' \
   -c 'Add :NSAppTransportSecurity:NSExceptionDomains:home.arpa:NSIncludesSubdomains bool true' $A/Info.plist

cp $O/Payload/minecraftearthtf.app/embedded.mobileprovision $A/

# XAL needs keychain sharing for these groups or sign-in fails before any request
cp $O/Entitlements.plist $W/Entitlements.plist
$P -c 'Add :keychain-access-groups:1 string BPGL4AF8SG.com.microsoft.xal' \
   -c 'Add :keychain-access-groups:2 string BPGL4AF8SG.com.microsoft.adalcache' $W/Entitlements.plist

ID=$(cat $O/signing-identity)
for f in $A/Frameworks/*.framework; do codesign --force --sign "$ID" --timestamp=none --generate-entitlement-der "$f"; done
codesign --force --sign "$ID" --timestamp=none --generate-entitlement-der --entitlements $W/Entitlements.plist $A
codesign --verify --deep --strict $A

xcrun devicectl device install app --device "$(cat $O/iphone-id)" $A
ditto -c -k --keepParent Payload Minecraft_Earth_0.33.0_Solace_iOS_signed.ipa
```

## 3. Simulator build (arm64 Mac)

```sh
rm -rf $S/sim && mkdir -p $S/sim && cp -R $W/Payload/minecraftearthtf.app $S/sim/
A=$S/sim/minecraftearthtf.app
rm -rf $A/_CodeSignature $A/embedded.mobileprovision
find $A -name _CodeSignature -type d -prune -exec rm -rf {} +

# Retag every Mach-O as iOS Simulator
for f in $A/minecraftearthtf $A/Frameworks/AzureSpatialAnchors_Preview.framework/AzureSpatialAnchors_Preview \
         $A/Frameworks/HttpClient.framework/HttpClient $A/Frameworks/Xal.framework/Xal; do
  vtool -set-build-version iossim 12.0 13.0 -replace -output "$f.sim" "$f" && mv "$f.sim" "$f"
done

# vtool zeroes Xal's header padding, but the patcher put the redirect strings there (0x1000-0x1280)
python3 -I - $W/Payload/minecraftearthtf.app $S/sim/minecraftearthtf.app <<'EOF'
import sys
a, b = sys.argv[1:]
rel = 'Frameworks/Xal.framework/Xal'
x = open(f'{a}/{rel}', 'rb').read(); y = bytearray(open(f'{b}/{rel}', 'rb').read())
y[0x1000:0x1280] = x[0x1000:0x1280]
open(f'{b}/{rel}', 'wb').write(y)
EOF

# The Simulator rejects signature entitlements on ad-hoc builds (launch fails with code 163),
# and without keychain groups XAL gets -34018. Embed them the way Xcode does instead.
codesign --remove-signature $A/minecraftearthtf
python3 -I $S/add_entitlements_section.py $A/minecraftearthtf $W/Entitlements.plist

for f in $A/Frameworks/*.framework; do codesign --force --sign - --timestamp=none "$f"; done
codesign --force --sign - --timestamp=none $A
xcrun simctl install <udid> $A
```

`xrefs.py` is the ADRP+ADD cross-reference scanner used to find the PlayFab URL builders that `patch_playfab.py` rewrites.
