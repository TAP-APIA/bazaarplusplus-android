# AGENTS.md

Standalone Android runtime prototype for importing user-owned The Bazaar APKs. Repo-wide rules are in ../AGENTS.md.

## Scope

- Own APK import, Game Version storage, Runtime Activity, Mod Profiles, and Android/HarmonyOS diagnostics.
- Consume BazaarPlusPlus Mod artifacts; the Mod project must not depend on this project.
- Never commit, bundle, upload, or include official game files in diagnostics or build outputs.

## Verification

- Run ./build-local.sh for a local debug APK.
- Run ./test.sh for source and manifest checks.
- Tests use synthetic ZIP fixtures only.

