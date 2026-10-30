# FusionCore baseline

This directory builds an unmodified, pinned FusionCore baseline for device
compatibility testing. FusionCore is maintained by All-Of-Us-Mods and is
licensed under GPL-3.0. Its downloaded source and build products stay under
the ignored `build/` directory.

The baseline intentionally contains no code from `VIkill33/BazaarPlusAndroid`
and no BazaarPlusPlus Mod payload. It requires the official game to already be
installed. A successful main-menu launch establishes the runtime baseline
before HarmonyOS-specific changes are introduced.

Run:

```sh
./fusioncore/build-baseline.sh
```

After the baseline launches the installed game successfully, build a local
BazaarPlusPlus payload variant with:

```sh
./fusioncore/build-with-bpp.sh
```

The payload variant keeps FusionCore's pinned BepInEx runtime and overlays only
the `plugins/` files from an Android APK previously built by this repository.
It does not copy launcher code or native libraries from that APK. Override the
local payload input with `BPP_ANDROID_PAYLOAD_APK=/absolute/path/to/apk`.

The payload build also installs the BazaarPlusPlus launcher shell from
`fusioncore/overlay/`: a focused home screen, built-in and imported Mod
management, FusionCore game settings, and an in-app BepInEx log viewer.
The resulting BHL1 test APK is written to `../dist/BHL1-0.3.0-debug.apk`.

