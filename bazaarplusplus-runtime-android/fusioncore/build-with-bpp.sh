#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE="$ROOT/build/fusioncore/source"
CACHE="$ROOT/build/fusioncore/bpp"
OUTPUT="$ROOT/../dist/BHL1-0.3.0-debug.apk"
DEFAULT_PAYLOAD_APK="$ROOT/../dist/BepInExLauncher-bpp-android-preview.47-local-debug.apk"
PAYLOAD_APK="${BPP_ANDROID_PAYLOAD_APK:-$DEFAULT_PAYLOAD_APK}"
ANDROID_SDK_ROOT="${ANDROID_SDK_ROOT:-/tmp/bpp-tools/android-sdk}"
JAVA_HOME="${JAVA_HOME:-/tmp/bpp-tools/jdk17-root/usr/lib/jvm/java-17-openjdk-amd64}"
export ANDROID_SDK_ROOT
export JAVA_HOME
export PATH="$JAVA_HOME/bin:$PATH"

"$ROOT/fusioncore/fetch-upstream.sh"

if [[ ! -f "$PAYLOAD_APK" ]]; then
    printf 'BazaarPlusPlus Android payload APK is missing: %s\n' "$PAYLOAD_APK" >&2
    printf 'Set BPP_ANDROID_PAYLOAD_APK to a locally built APK from this repository.\n' >&2
    exit 2
fi

upstream_bep="$SOURCE/fusionApp/src/main/assets/BepInEx-arm64.zip"
pristine_bep="$CACHE/BepInEx-arm64.upstream.zip"
work="$(mktemp -d "$ROOT/build/fusioncore/bpp-build.XXXXXX")"
trap 'rm -rf "$work"' EXIT
mkdir -p "$CACHE"

# Preserve the pinned upstream runtime once. Subsequent builds always start from it,
# so a failed or repeated overlay cannot accumulate stale plugin files.
if [[ ! -f "$pristine_bep" ]]; then
    cp "$upstream_bep" "$pristine_bep"
fi

unzip -q "$PAYLOAD_APK" assets/bpp-modpack.zip -d "$work/payload-apk"
unzip -q "$work/payload-apk/assets/bpp-modpack.zip" 'plugins/*' -d "$work/modpack"
test -f "$work/modpack/plugins/BazaarPlusPlus.dll"

mkdir -p "$work/bepinex"
unzip -q "$pristine_bep" -d "$work/bepinex"
mkdir -p "$work/bepinex/plugins"
cp -R "$work/modpack/plugins/." "$work/bepinex/plugins/"

(
    cd "$work/bepinex"
    # FusionCore rejects a root "./" ZIP entry as outside its canonical output
    # prefix. Name the four upstream roots explicitly so extraction reaches plugins.
    zip -q -r "$work/BepInEx-arm64.zip" config core patchers plugins
)
cp "$work/BepInEx-arm64.zip" "$upstream_bep"

# Apply the BazaarPlusPlus launcher shell over the pinned FusionCore source. The
# upstream runtime and hooks stay untouched except for the post-extraction state hook.
cp -R "$ROOT/fusioncore/overlay/java/." "$SOURCE/fusionApp/src/main/java/"
mkdir -p "$SOURCE/fusionApp/src/main/res/xml"
cp -R "$ROOT/fusioncore/overlay/res/xml/." "$SOURCE/fusionApp/src/main/res/xml/"

bootstrap="$SOURCE/fusionApp/src/main/java/dev/allofus/fusioncore/BootstrapActivity.java"
if ! grep -q 'BppModRepository.applyBuiltInState' "$bootstrap"; then
    sed -i '/extractZipFromAssets(appContext, "BepInEx-arm64.zip", bepInExDir, "config\/"/a\            BppModRepository.applyBuiltInState(appContext, bepInExDir);' "$bootstrap"
fi

manifest="$SOURCE/fusionApp/src/main/AndroidManifest.xml"
if ! grep -q 'BppModManagerActivity' "$manifest"; then
    sed -i '/<!-- StubActivity for dynamic loading of unregistered activities -->/i\    <activity android:name="dev.allofus.fusioncore.BppModManagerActivity" android:theme="@style/Theme.Material3.Dark.NoActionBar" android:exported="false" />\n    <activity android:name="dev.allofus.fusioncore.BppLogActivity" android:theme="@style/Theme.Material3.Dark.NoActionBar" android:exported="false" />\n    <provider android:name="androidx.core.content.FileProvider" android:authorities="${applicationId}.files" android:exported="false" android:grantUriPermissions="true">\n      <meta-data android:name="android.support.FILE_PROVIDER_PATHS" android:resource="@xml/bpp_file_paths" />\n    </provider>' "$manifest"
fi
sed -i 's/android:label="@string\/app_name"/android:label="BHL1"/' "$manifest"

gradle="$SOURCE/fusionApp/build.gradle.kts"
sed -i -E 's/versionCode = [0-9]+/versionCode = 3/' "$gradle"
sed -i -E 's/versionName = "[^"]+"/versionName = "0.3.0-bhl1"/' "$gradle"

icon_root="$ROOT/../android-port/assets/icon"
for density in mdpi hdpi xhdpi xxhdpi xxxhdpi; do
    mipmap="$SOURCE/fusionApp/src/main/res/mipmap-$density"
    mkdir -p "$mipmap"
    cp "$icon_root/legacy-square-$density.png" "$mipmap/app_icon.png"
    cp "$icon_root/legacy-round-$density.png" "$mipmap/app_icon_round.png"
    cp "$icon_root/adaptive-foreground-$density.png" "$mipmap/ic_launcher_foreground.png"
done

printf 'sdk.dir=%s\n' "$ANDROID_SDK_ROOT" > "$SOURCE/local.properties"
(
    cd "$SOURCE"
    ./gradlew --no-daemon :fusionApp:assembleDebug
)

apk="$SOURCE/fusionApp/build/outputs/apk/debug/fusionApp-debug.apk"
test -f "$apk"
mkdir -p "$(dirname "$OUTPUT")"
cp "$apk" "$OUTPUT"
sha256sum "$OUTPUT"

