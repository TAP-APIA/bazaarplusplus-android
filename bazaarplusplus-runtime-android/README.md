# BazaarPlusPlus Android Runtime Prototype

The first build validated APK import. Probe 2 adds a deliberately minimal pure-game runtime attempt without BepInEx or Mods.

It opens Android's document picker, copies the selected APK into private storage, calculates SHA-256, reads package and signing information, and checks Unity/IL2CPP files. A successful import is committed atomically under files/versions/<sha256>/source.apk.

Official game files are not included in this repository or the built APK. Build with ./build-local.sh.

Probe 2 extracts the imported APK's ARM64 native libraries into the app-private Game Version, creates a DexClassLoader and asset-backed Context, then reflectively constructs the official UnityPlayer inside BazaarRuntimeActivity. Every stage is written to files/runtime-probe.log and shown on the home screen after returning.

