# Minecraft Earth iOS client for Solace

This builds a Minecraft Earth 0.33.0 iOS client that plays against a Solace server at `http://earth.home.arpa:8080`. Sign-in needs the server-side iOS (XAL sisu) support from this fork's `main` branch.

The official [Minecraft_Earth_Patcher](https://github.com/Earth-Restored/Minecraft_Earth_Patcher) does most of the work. `build.sh` pins that patcher to a known commit and adds what iOS still needs on top:

- **PlayFab:** the binary builds `https://<titleid>.playfabapi.com` itself, which the patcher doesn't redirect. `patch_playfab.py` rewrites it to `http://<titleid>.home.arpa:8080`. The new text is the same length as the old, so the server name and port are fixed.
- **Sunset modal:** `patch_sunset.py` stops the "Time to Sunset" popup that otherwise appears on every launch.
- **ATS:** an exception so the app can make plain-HTTP requests to `home.arpa`.
- **Keychain:** keychain groups for `com.microsoft.xal` and `com.microsoft.adalcache`. Without them, XAL fails sign-in before it sends any request.
- **Bundle ID and devices:** a custom bundle ID, and extra `UISupportedDevices` entries for newer iPhones.
- **iPhone Duo:** native display sizing and live fold and unfold support. Pass `--full-inner` to fill the inner display with phone-sized controls.
- **Simulator:** a build for the iOS Simulator on Apple-silicon Macs (see below).

## DNS

The client needs these names to resolve to the Solace server:

- `earth.home.arpa`
- `20ca2.home.arpa`
- `11509.home.arpa`

## Build

You need macOS with the Xcode command line tools, python3, git, and the .NET 10 SDK.

```sh
# Device: needs a development identity and a provisioning profile for the bundle ID
./build.sh device Minecraft_Earth_0.33.0_ios_15.0.ipa out \
  --identity "Apple Development: ..." --profile app.mobileprovision --device iPhone19,4
xcrun devicectl device install app --device <udid> out/Minecraft_Earth_Solace.ipa

# Simulator
./build.sh sim Minecraft_Earth_0.33.0_ios_15.0.ipa out --team <TEAMID> --device iPhone19,4
xcrun simctl install <udid> out/minecraftearthtf.app
```

Both modes reproduce the builds tested on 2026-11-23. Apart from signature blobs and the sunset modal patch, the output is byte-identical.

## Simulator notes

- **Platform:** the Simulator rejects iOS-device binaries. `vtool` retags every Mach-O as `iossim`. That zeroes Xal's header padding, where the patcher stores the redirect strings, so `build.sh` copies that range back.
- **Entitlements:** an ad-hoc signature carrying entitlements won't launch in the Simulator (code 163). Without the keychain groups, XAL fails with `-34018`. `add_entitlements_section.py` embeds them as a `__TEXT,__entitlements` section, the same way Xcode does for Simulator builds.

## Files

- `build.sh`: the whole pipeline.
- `driver/`: runs the official patcher with the options its UI uses for iOS advanced mode with a custom server.
- `patch_playfab.py`: the PlayFab host and scheme patch. It checks the original bytes before changing anything and is safe to run twice.
- `add_entitlements_section.py`: embeds entitlements for Simulator builds.
- `xrefs.py`: an ADRP+ADD cross-reference scanner, used to find the PlayFab URL builders.
- `STEPS.md`: the original shell history, kept for reference.

The signing profile used for the owner's phone expires on 2027-11-07. After that, rebuild with a new profile.
