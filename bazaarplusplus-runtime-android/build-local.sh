#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
BUILD="$ROOT/build/local"
JAVA_HOME="${JAVA_HOME:-/tmp/bpp-tools/jdk17-root/usr/lib/jvm/java-17-openjdk-amd64}"
ANDROID_JAR="${ANDROID_JAR:-/tmp/bpp-tools/platform-extract/android-35/android.jar}"
ANDROID_BUILD_TOOLS="${ANDROID_BUILD_TOOLS:-/tmp/bpp-tools/build-tools-extract/android-15}"
KEYSTORE="${KEYSTORE:-/tmp/bpp-tools/signing/debug.keystore}"
OUTPUT="$ROOT/build/outputs/BazaarPlusPlus-Runtime-Probe-0.16.0-debug.apk"
export JAVA_HOME
export PATH="$JAVA_HOME/bin:$PATH"
rm -rf "$BUILD"
mkdir -p "$BUILD/compiled-res" "$BUILD/classes" "$BUILD/dex" "$BUILD/generated" "$ROOT/build/outputs"
"$ANDROID_BUILD_TOOLS/aapt2" compile --dir "$ROOT/res" -o "$BUILD/compiled-res/resources.zip"
"$ANDROID_BUILD_TOOLS/aapt2" link -I "$ANDROID_JAR" --manifest "$ROOT/AndroidManifest.xml" --java "$BUILD/generated" --min-sdk-version 28 --target-sdk-version 35 --version-code 16 --version-name 0.16.0-probe.16 -o "$BUILD/unsigned.apk" "$BUILD/compiled-res/resources.zip"
find "$ROOT/src" "$BUILD/generated" -name '*.java' -print0 | xargs -0 "$JAVA_HOME/bin/javac" -source 8 -target 8 -encoding UTF-8 -classpath "$ANDROID_JAR" -d "$BUILD/classes"
(cd "$BUILD/classes" && "$JAVA_HOME/bin/jar" cf "$BUILD/classes.jar" .)
"$ANDROID_BUILD_TOOLS/d8" --min-api 28 --lib "$ANDROID_JAR" --output "$BUILD/dex" "$BUILD/classes.jar"
(cd "$BUILD/dex" && "$ANDROID_BUILD_TOOLS/aapt" add "$BUILD/unsigned.apk" classes.dex >/dev/null)
"$ANDROID_BUILD_TOOLS/zipalign" -P 16 -f 4 "$BUILD/unsigned.apk" "$BUILD/aligned.apk"
"$ANDROID_BUILD_TOOLS/apksigner" sign --ks "$KEYSTORE" --ks-key-alias androiddebugkey --ks-pass pass:android --key-pass pass:android --out "$OUTPUT" "$BUILD/aligned.apk"
"$ANDROID_BUILD_TOOLS/zipalign" -c -P 16 4 "$OUTPUT"
"$ANDROID_BUILD_TOOLS/apksigner" verify --verbose --print-certs "$OUTPUT"
sha256sum "$OUTPUT"

