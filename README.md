# Terraria + Calamity iOS port kit

Experimental source, native-library patches, and build tools for running tModLoader with Calamity on an iPhone through LiveContainer/Strata with **JIT disabled**. Includes an editable native touch overlay, OpenGL ES and Vulkan/MoltenVK renderer support, shared audio output, and build-time baking of supported Calamity IL edits into AOT code.

This repository contains the porting code. Build your personal IPA from your own Terraria, tModLoader, Calamity, and Music files. It contains no game binaries, mod packages, artwork, saves, or installable IPA. It is an independent project, unaffiliated with Re-Logic, tModLoader, or the Calamity team.

## Status

The development build has reached graphical menus and Calamity gameplay on an iPhone 16 Pro, with audio, touch controls, and JIT disabled. The tested combination is tModLoader **2026.09.3.1**, Terraria baseline **1.4.4.9**, Calamity **2.2.4**, and Music **2.1**. Other combinations need a new hook audit, weave, and bake.

This is an experimental kit assembled from a working development tree. A fresh build from this source-only checkout has not been tested end to end. Input staging and the headless baking setup below are manual steps. Loading time, memory use, thermal performance, and some controls remain under development; see [known issues](docs/TROUBLESHOOTING.md).

## Requirements

- Apple Silicon Mac, Xcode and its iPhone SDK, approximately 30 GB of free space.
- .NET SDK 10.0.404, .NET 8 targeting/runtime support, and the .NET iOS workload. The development build used iOS workload 26.0.11017 with Xcode 27.2 and disabled exact-version validation.
- Git, Python 3 with Pillow, and GNU `timeout` (`coreutils`; ensure the command is available as `timeout`).
- An iPhone with a configured LiveContainer/Strata installation; import/sign your own build through your existing installation method.
- Your own compatible game and mod inputs. The development build needed the host's increased-memory-limit entitlement; an IPA cannot grant that entitlement to itself.

Use a local .NET installation under `toolchain/dotnet` with its NuGet cache under `toolchain/nuget`, or adapt the build scripts to your SDK installation. This entire directory is ignored by Git.

## 1. Stage your inputs

Create the following ignored directories locally:

```text
inputs/loader-references/    tModLoader.dll, TerrariaHooks.dll, FNA.dll,
                            ReLogic.dll and managed dependencies from tML
inputs/loader-libraries/     Mono.Cecil*.dll and MonoMod*.dll used by the tools
inputs/mods/CalamityMod/     extracted CalamityMod.dll
inputs/mods/CalamityModMusic/ extracted CalamityModMusic.dll
inputs/mod-packages/         original, verified .tmod packages
build/terraria-content/Content/  your Terraria Content tree
build/tml-content/Content/       the matching tModLoader Content overrides
```

Extract dependencies from your tModLoader distribution, retaining compatible versions. The reference tree used for weaving must resolve its managed dependencies. Use the real managed Steamworks.NET assembly from the distribution's Linux runtime rather than a reference-only stub. Keep native Windows DLLs out of the managed app bundle.

Extract and verify your mod references without executing them:

```bash
python3 tools/extract-mods.py /path/to/CalamityMod.tmod /path/to/CalamityModMusic.tmod
```

This writes local extracted references and `metadata/selected-mod-verification.json`. Supply both mods in one invocation.

## 2. Build and patch the native libraries

```bash
bash tools/fetch-native-sources.sh
```

This clones the pinned SDL2, FNA3D, FAudio, and Theorafile sources into `build/native-src`, initializes submodules, applies the patches, and installs artwork-free control placeholders. It refuses to overwrite an existing checkout.

Optionally generate item icons from your own vanilla content:

```bash
dotnet build tools/XnbExport -c Release
python3 tools/make-control-icons.py --content build/terraria-content/Content
```

The generated icon header stays in the ignored native build tree. The tracked placeholder remains free of game art. No extracted official mobile app icon is included; the app uses its default icon unless you provide your own.

Build SDL before the other native frameworks:

```bash
xcodebuild -project build/native-src/SDL2/Xcode/SDL/SDL.xcodeproj \
  -target Framework-iOS -configuration Release -sdk iphoneos ARCHS=arm64 \
  CODE_SIGNING_ALLOWED=NO IPHONEOS_DEPLOYMENT_TARGET=15.0 \
  SYMROOT="$PWD/build/native-build/SDL2"
bash tools/build-natives-ios.sh
```

For the optional Vulkan-to-Metal renderer, obtain the upstream MoltenVK **1.4.2 iOS release** and extract it under `build/moltenvk-src` so this path exists:
`build/moltenvk-src/MoltenVK/MoltenVK/dynamic/MoltenVK.xcframework/ios-arm64/MoltenVK.framework`.
The framework is added during packaging. OpenGL ES is the alternative renderer.

## 3. Build the managed hooks and weave

```bash
mkdir -p metadata build/tml-bundle
dotnet build app/Payload -c Release
dotnet build tools/ManagedHooks -c Release
dotnet build tools/HookWeaver -c Release
dotnet tools/HookWeaver/bin/Release/net8.0/HookWeaver.dll audit
dotnet tools/HookWeaver/bin/Release/net8.0/HookWeaver.dll weave-ios
```

Populate `build/tml-bundle` with the app's managed tModLoader dependency set from your distribution. Overlay `build/woven-ios/*.dll` and the built `ManagedHooks.dll`. The patched FNA/ReLogic assemblies produced by the weaver belong in this bundle as well.

## 4. Bake supported IL edits

The baking script expects a local headless tML setup:

- `build/headless/tml-bake`: your complete runnable tML distribution, including its Mac native libraries; the script overlays the woven assemblies here.
- `build/headless/save-woven/Mods`: your two original mod packages and an `enabled.json` listing `CalamityModMusic` and `CalamityMod`.
- `build/terraria-content/Content`: the owned vanilla content tree prepared earlier.

```bash
bash tools/bake-il.sh
dotnet build tools/BakeVerify -c Release
```

The script prints its capture output directory. Put the captured `tModLoader.dll` into a complete local dependency directory and run:

```bash
dotnet tools/BakeVerify/bin/Release/net8.0/BakeVerify.dll /path/to/complete/captured-loader-directory
```

Review its `BAD`/`FAILED` output before replacing `build/tml-bundle/tModLoader.dll` with the captured assembly. Keep `TerrariaHooks`, `ManagedHooks`, patched FNA and ReLogic from the same weave/build. **Do not replace or modify MonoMod.Utils to make the bake pass.** Some IL edits still require the interpreter fallback.

## 5. Stage content, build, and package

```bash
python3 tools/prepare-game-assets.py
```

This merges vanilla content with matching tML overrides, adds the two mod packages, and writes `metadata/assets.json`. It copies source files and does not modify the originals.

Use a fresh intermediate/output label after changing managed assemblies to avoid stale AOT images:

```bash
dotnet build app/Phone -c Release -p:ValidateXcodeVersion=false \
  -p:IntermediateOutputPath=obj/v1/Release/net10.0-ios/ios-arm64/ \
  -p:OutputPath=bin/v1/Release/net10.0-ios/ios-arm64/
python3 tools/package-private-ipa.py --version v1
```

If ILLink stalls starting an external task host, add:
`-p:CustomBeforeMicrosoftCommonTargets="$PWD/tools/ILLinkInProcess.targets"`.
That optional targets file assumes the documented local SDK/NuGet layout and ILLink 10.0.15; update its path for a different toolchain.

Package the current native frameworks, optional MoltenVK, and 120 Hz configuration:

```bash
python3 tools/package-native-update.py \
  --base build/Terraria-Calamity-v1-Unsigned.ipa --version v2 \
  --audio --fna3d --moltenvk --promotion
```

Omit `--moltenvk` when building only for OpenGL ES. Native-update packaging verifies that entries outside its declared changes are byte-identical to the base IPA and checks ZIP CRCs.

Import your generated IPA into LiveContainer/Strata, preserve its data when updating, and keep JIT disabled. Start with a 60 FPS cap; use the gear menu to configure controls and renderer. See [architecture](docs/ARCHITECTURE.md) and [troubleshooting](docs/TROUBLESHOOTING.md).

## Logs and privacy

The generic collector accepts your own device and guest-container values:

```bash
python3 tools/fetch-phone-logs.py --version v2 \
  --device YOUR_DEVICE_ID --app-id YOUR_CONTAINER_BUNDLE_ID \
  --guest-documents Documents/Data/Application/YOUR_GUEST_ID/Documents
```

Logs, crash reports, provisioning, input files, build artifacts and saves are ignored by Git. Do not commit generated icon bytes or upload your personal full-game IPA as a release.

## Credits and licensing

Our original port tooling is MIT-licensed; see [LICENSE](LICENSE). Native patch context and derived modifications retain the upstream terms collected in [third_party](third_party). Dependencies retain their own licenses. This project's MIT license does not grant rights to Terraria, tModLoader, Calamity, their assets, or the user's input files.

Thanks to Re-Logic, the tModLoader and Calamity teams, the FNA/SDL/FAudio/FNA3D/Theorafile contributors, the MoltenVK project, Mono/.NET, MonoMod, and the LiveContainer community. Upstream projects and their licenses govern their respective work.
