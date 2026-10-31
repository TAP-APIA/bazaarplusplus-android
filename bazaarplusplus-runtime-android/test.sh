#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
grep -q 'com.tempogames.thebazaar' "$ROOT/src/com/bazaarplusplus/runtime/ApkImporter.java"
grep -q 'ACTION_OPEN_DOCUMENT' "$ROOT/src/com/bazaarplusplus/runtime/MainActivity.java"
grep -q 'lib/arm64-v8a/libunity.so' "$ROOT/src/com/bazaarplusplus/runtime/ApkImporter.java"
grep -q 'lib/arm64-v8a/libil2cpp.so' "$ROOT/src/com/bazaarplusplus/runtime/ApkImporter.java"
grep -q 'android:exported="true"' "$ROOT/AndroidManifest.xml"
grep -q '^repository=https://github.com/All-Of-Us-Mods/FusionCore$' "$ROOT/fusioncore/upstream.properties"
grep -Eq '^commit=[0-9a-f]{40}$' "$ROOT/fusioncore/upstream.properties"
grep -q '^license=GPL-3.0$' "$ROOT/fusioncore/upstream.properties"
test -f "$ROOT/fusioncore/LICENSE"
grep -q 'GNU GENERAL PUBLIC LICENSE' "$ROOT/fusioncore/LICENSE"
grep -q "'plugins/\*'" "$ROOT/fusioncore/build-with-bpp.sh"
grep -q 'BazaarPlusPlus.dll' "$ROOT/fusioncore/build-with-bpp.sh"
grep -q 'zip -q -r.*config core patchers plugins' "$ROOT/fusioncore/build-with-bpp.sh"
grep -q 'BppModRepository.applyBuiltInState' "$ROOT/fusioncore/build-with-bpp.sh"
grep -q 'versionCode = 3' "$ROOT/fusioncore/build-with-bpp.sh"
grep -Fq 'versionName = "0.3.0-bhl1"' "$ROOT/fusioncore/build-with-bpp.sh"
grep -q 'android:label="BHL1"' "$ROOT/fusioncore/build-with-bpp.sh"
grep -q 'BottomNavigationView' "$ROOT/fusioncore/overlay/java/dev/allofus/fusioncore/SelectorActivity.java"
grep -q 'Blocked zip entry outside output folder' "$ROOT/build/fusioncore/source/fusionApp/src/main/java/dev/allofus/fusioncore/tools/Utilities.java" 2>/dev/null || true
test -f "$ROOT/fusioncore/overlay/java/dev/allofus/fusioncore/BppModManagerActivity.java"
test -f "$ROOT/fusioncore/overlay/java/dev/allofus/fusioncore/BppLogActivity.java"
if grep -q "'core/\*'" "$ROOT/fusioncore/build-with-bpp.sh"; then
    echo 'FusionCore payload overlay must not import the legacy core directory.' >&2
    exit 1
fi
echo 'Runtime prototype source checks passed.'
