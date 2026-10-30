#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE="$ROOT/build/fusioncore/source"
OUTPUT="$ROOT/build/outputs/FusionCore-Harmony-Baseline-debug.apk"
ANDROID_SDK_ROOT="${ANDROID_SDK_ROOT:-/tmp/bpp-tools/android-sdk}"
JAVA_HOME="${JAVA_HOME:-/tmp/bpp-tools/jdk17-root/usr/lib/jvm/java-17-openjdk-amd64}"
export ANDROID_SDK_ROOT
export JAVA_HOME
export PATH="$JAVA_HOME/bin:$PATH"

"$ROOT/fusioncore/fetch-upstream.sh"

if [[ ! -f "$ANDROID_SDK_ROOT/platforms/android-37/android.jar" ]] &&
   [[ ! -f "$ANDROID_SDK_ROOT/platforms/android-37.0/android.jar" ]]; then
    printf 'Android SDK platform 37 is missing from %s\n' "$ANDROID_SDK_ROOT" >&2
    printf 'Install platform 37, build-tools, NDK and CMake before building.\n' >&2
    exit 2
fi

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

