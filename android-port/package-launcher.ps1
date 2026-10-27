param(
    [string]$SourceApk = (Join-Path $PSScriptRoot '../dist/app-bpp-fixed.apk'),
    [string]$Destination = (Join-Path $PSScriptRoot '../dist/BepInExLauncher-bpp-android.apk'),
    [string]$ModpackZip = '',
    [string]$BuildOutput = (Join-Path $PSScriptRoot '../bazaarplusplus-mod/src/BazaarPlusPlus/bin/Debug/net6.0'),
    [string]$InteropSnapshot = (Join-Path $PSScriptRoot '../android-managed'),
    [string]$SafeRuntime = (Join-Path $PSScriptRoot '../safe_Il2CppInterop.Runtime.dll'),
    [string]$SafeHarmony = (Join-Path $PSScriptRoot '../safe_Il2CppInterop.HarmonySupport.dll'),
    [string]$KeyStore = '',
    [string]$KeyAlias = 'androiddebugkey',
    [int]$VersionCode = 41,
    [string]$AndroidSdkRoot = $env:ANDROID_SDK_ROOT,
    [switch]$UnsignedOnly
)

# Builds the self-deploying BazaarPlusPlus Android APK by combining the verified
# BepInEx Android launcher with the packaged BPP modpack, manifest inventory, and
# BppLauncherActivity. Preserves all original native libraries (.so) and DEX files.

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
    $studioJbr = 'C:\Program Files\Android\Android Studio\jbr'
    if (Test-Path -LiteralPath "$studioJbr\bin\java.exe") {
        $env:JAVA_HOME = $studioJbr
    }
}
$java = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin/java.exe' } else { (Get-Command java).Source }
$javac = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin/javac.exe' } else { (Get-Command javac).Source }

$sdkRoot = if ($AndroidSdkRoot) { $AndroidSdkRoot } else { "$env:LOCALAPPDATA\Android\Sdk" }
$buildToolsDir = Get-ChildItem -LiteralPath "$sdkRoot\build-tools" -Directory |
    Sort-Object Name -Descending |
    Select-Object -First 1 -ExpandProperty FullName
$platformJar = Get-ChildItem -LiteralPath "$sdkRoot\platforms" -Directory |
    Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'android.jar' } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1

if (-not $buildToolsDir -or -not (Test-Path -LiteralPath $platformJar)) {
    throw "Android SDK build-tools or android.jar not found under $sdkRoot"
}

foreach ($requiredPath in @($SourceApk, $SafeRuntime, $SafeHarmony)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required build input not found: $requiredPath"
    }
}
if ([IO.Path]::GetFullPath($SourceApk) -eq [IO.Path]::GetFullPath($Destination)) {
    throw 'SourceApk and Destination must differ.'
}
if ($VersionCode -lt 2) {
    throw 'VersionCode must be at least 2 for a self-deploying APK that can overwrite the original launcher.'
}

$versionFile = Join-Path $PSScriptRoot '../VERSION'
$productVersion = if (Test-Path -LiteralPath $versionFile) {
    (Get-Content -LiteralPath $versionFile -Raw).Trim()
} else {
    '6.0.0'
}
# Monotonic Android versioning for the self-deploying launcher. Bump this for every published APK;
# install -r refuses a lower versionCode and users must not uninstall (that wipes local history).
# android-v6.0.0-preview.40 shipped versionCode 40, so the next package is 41 / 6.0.0-preview.41.
$versionCode = $VersionCode
$versionName = "$productVersion-preview.$versionCode"

$launcherToolsDir = Join-Path $PSScriptRoot 'tools/bpp-launcher'
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('bpp-apk-build-' + [guid]::NewGuid())
New-Item -ItemType Directory -Force -Path $workRoot | Out-Null

try {
    # 0. Build a fresh modpack from the current Android build unless one was supplied.
    if ([string]::IsNullOrWhiteSpace($ModpackZip)) {
        $modpackDir = Join-Path $workRoot 'bpp-modpack'
        & (Join-Path $PSScriptRoot 'package-modpack.ps1') `
            -BuildOutput $BuildOutput `
            -Destination $modpackDir `
            -LauncherApk $SourceApk `
            -InteropSnapshot $InteropSnapshot `
            -SafeRuntime $SafeRuntime `
            -SafeHarmony $SafeHarmony `
            -Zip
        if ($LASTEXITCODE -ne 0) { throw 'package-modpack.ps1 failed' }
        $ModpackZip = "$modpackDir.zip"
    }
    if (-not (Test-Path -LiteralPath $ModpackZip -PathType Leaf)) {
        throw "Modpack archive not found: $ModpackZip"
    }

    # 1. Run pure-Java installer unit tests and manifest patcher tests before packaging
    $testClasses = Join-Path $workRoot 'test-classes'
    New-Item -ItemType Directory -Force -Path $testClasses | Out-Null
    & $javac -encoding UTF-8 --release 17 -d $testClasses `
        (Join-Path $launcherToolsDir 'src/com/bepinex/android/bpp/BppBundledModpackInstaller.java') `
        (Join-Path $launcherToolsDir 'test/com/bepinex/android/bpp/BppBundledModpackInstallerTests.java')
    if ($LASTEXITCODE -ne 0) { throw 'BppBundledModpackInstaller test compilation failed' }
    & $java -cp $testClasses com.bepinex.android.bpp.BppBundledModpackInstallerTests
    if ($LASTEXITCODE -ne 0) { throw 'BppBundledModpackInstaller unit tests failed' }

    & $javac -encoding UTF-8 --release 17 -d $testClasses `
        (Join-Path $launcherToolsDir 'src/com/bepinex/android/bpp/BppDiagnosticBundle.java') `
        (Join-Path $launcherToolsDir 'test/com/bepinex/android/bpp/BppDiagnosticBundleTests.java')
    if ($LASTEXITCODE -ne 0) { throw 'BppDiagnosticBundle test compilation failed' }
    & $java -cp $testClasses com.bepinex.android.bpp.BppDiagnosticBundleTests
    if ($LASTEXITCODE -ne 0) { throw 'BppDiagnosticBundle unit tests failed' }

    & python (Join-Path $launcherToolsDir 'test_patch_manifest.py') ([IO.Path]::GetFullPath($SourceApk))
    if ($LASTEXITCODE -ne 0) { throw 'AndroidManifest patcher tests failed' }

    & python (Join-Path $launcherToolsDir 'test_game_window.py')
    if ($LASTEXITCODE -ne 0) { throw 'Android game-window lifecycle tests failed' }

    & python (Join-Path $launcherToolsDir 'test_launcher_update.py')
    if ($LASTEXITCODE -ne 0) { throw 'Launcher update checker tests failed' }

    # 2. Compile BppBundledModpackInstaller + BppLauncherActivity into classes3.dex
    $appClasses = Join-Path $workRoot 'app-classes'
    $dexOut = Join-Path $workRoot 'dex'
    New-Item -ItemType Directory -Force -Path $appClasses, $dexOut | Out-Null
    & $javac -encoding UTF-8 --release 11 -cp $platformJar -d $appClasses `
        (Join-Path $launcherToolsDir 'src/com/bepinex/android/bpp/BppBundledModpackInstaller.java') `
        (Join-Path $launcherToolsDir 'src/com/bepinex/android/bpp/BppLauncherActivity.java') `
        (Join-Path $launcherToolsDir 'src/com/bepinex/android/bpp/BppLauncherUpdateChecker.java') `
        (Join-Path $launcherToolsDir 'src/com/bepinex/android/bpp/BppBackBridge.java') `
        (Join-Path $launcherToolsDir 'src/com/bepinex/android/bpp/BppGameWindow.java') `
        (Join-Path $launcherToolsDir 'src/com/bepinex/android/bpp/BppDiagnosticBundle.java') `
        (Join-Path $launcherToolsDir 'src/com/bepinex/android/bpp/BppDiagnosticExporter.java') `
        (Join-Path $launcherToolsDir 'src/com/bepinex/android/bpp/BppDiagnosticSessionLog.java') `
        (Join-Path $launcherToolsDir 'src/com/bepinex/android/bpp/BppGameProcess.java') `
        (Join-Path $launcherToolsDir 'src/com/bepinex/android/bpp/BppGameApplication.java') `
        (Join-Path $launcherToolsDir 'src/com/bepinex/android/bpp/BppGenerationForegroundService.java')
    if ($LASTEXITCODE -ne 0) { throw 'BppLauncherActivity compilation failed' }

    $compiledClassFiles = @(Get-ChildItem -LiteralPath $appClasses -Recurse -Filter *.class | Select-Object -ExpandProperty FullName)
    & (Join-Path $buildToolsDir 'd8.bat') --min-api 28 --lib $platformJar --output $dexOut @compiledClassFiles
    if ($LASTEXITCODE -ne 0) { throw 'd8 dexing failed' }
    $classes3Dex = Join-Path $dexOut 'classes.dex'
    if (-not (Test-Path -LiteralPath $classes3Dex)) { throw 'classes3.dex was not produced by d8' }

    # 3. Normalize bpp-modpack.zip entries and generate bpp-modpack-manifest.tsv + assemble unsigned APK
    $unsignedApk = Join-Path $workRoot 'launcher.unsigned.apk'
    $alignedApk = Join-Path $workRoot 'launcher.aligned.apk'
    $assembleScript = Join-Path $workRoot 'assemble_apk.py'
    @'
import hashlib
import io
import struct
import sys
import zipfile
from pathlib import Path

source_apk = Path(sys.argv[1])
modpack_zip_in = Path(sys.argv[2])
safe_runtime = Path(sys.argv[3]).read_bytes()
safe_harmony = Path(sys.argv[4]).read_bytes()
classes3_dex = Path(sys.argv[5]).read_bytes()
patch_manifest_dir = Path(sys.argv[6])
product_version = sys.argv[7]
version_code = int(sys.argv[8])
version_name = sys.argv[9]
unsigned_apk_out = Path(sys.argv[10])

sys.path.insert(0, str(patch_manifest_dir))
from patch_manifest import patch_manifest_bytes

def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()

# Repackage modpack ZIP with forward slashes and deterministic order, verifying no forbidden files
modpack_entries: dict[str, bytes] = {}
with zipfile.ZipFile(modpack_zip_in, "r") as zin:
    for info in zin.infolist():
        name = info.filename.replace("\\", "/").strip("/")
        if not name or info.is_dir() or name.endswith("/"):
            continue
        if ".." in name.split("/"):
            raise ValueError(f"Unsafe modpack entry: {name}")
        if name != "modpack.json" and not name.startswith(("plugins/", "core/")):
            raise ValueError(f"Disallowed modpack entry: {name}")
        if Path(name).suffix.lower() in (".cfg", ".db", ".sqlite", ".keystore", ".jks", ".pdb", ".mp4", ".png"):
            raise ValueError(f"Forbidden file type in modpack: {name}")
        if name in modpack_entries:
            raise ValueError(f"Duplicate modpack entry: {name}")
        modpack_entries[name] = zin.read(info.filename)

# Ensure core in modpack has the exact patched runtime and harmony support
modpack_entries["core/Il2CppInterop.Runtime.dll"] = safe_runtime
modpack_entries["core/Il2CppInterop.HarmonySupport.dll"] = safe_harmony

modpack_buf = io.BytesIO()
with zipfile.ZipFile(modpack_buf, "w", compression=zipfile.ZIP_DEFLATED) as zout:
    for name in sorted(modpack_entries):
        zout.writestr(name, modpack_entries[name])
normalized_modpack_bytes = modpack_buf.getvalue()
bundle_id = sha256_hex(normalized_modpack_bytes)

manifest_lines = [f"bundle_id\t{bundle_id}\t{product_version}"]
for name in sorted(modpack_entries):
    blob = modpack_entries[name]
    manifest_lines.append(f"file\t{name}\t{len(blob)}\t{sha256_hex(blob)}")
manifest_tsv_bytes = ("\n".join(manifest_lines) + "\n").encode("utf-8")

with zipfile.ZipFile(source_apk, "r") as zsrc:
    # Verify libpine.so is not stubbed with 'ret' (c0 03 5f d6) at 0x62a4
    pine_bytes = zsrc.read("lib/arm64-v8a/libpine.so")
    if pine_bytes[0x62a4:0x62a8] == b"\xc0\x03\x5f\xd6":
        raise ValueError("Refusing to package stubbed libpine.so (Pine_init0 == ret)")

    # Patch embedded assets/BepInEx.Android.zip with safe Runtime + HarmonySupport and forward slashes
    bep_raw = zsrc.read("assets/BepInEx.Android.zip")
    bep_entries: dict[str, bytes] = {}
    with zipfile.ZipFile(io.BytesIO(bep_raw), "r") as zbep:
        for info in zbep.infolist():
            name = info.filename.replace("\\", "/").strip("/")
            if not name or info.is_dir() or name.endswith("/"):
                continue
            bep_entries[name] = zbep.read(info.filename)
    bep_entries["core/Il2CppInterop.Runtime.dll"] = safe_runtime
    bep_entries["core/Il2CppInterop.HarmonySupport.dll"] = safe_harmony
    bep_out_buf = io.BytesIO()
    with zipfile.ZipFile(bep_out_buf, "w", compression=zipfile.ZIP_DEFLATED) as zbep_out:
        for name in sorted(bep_entries):
            zbep_out.writestr(name, bep_entries[name])
    patched_bep_zip = bep_out_buf.getvalue()

    patched_manifest = patch_manifest_bytes(
        zsrc.read("AndroidManifest.xml"), version_code, version_name, allow_repack=True
    )

    with zipfile.ZipFile(unsigned_apk_out, "w") as zdst:
        for info in zsrc.infolist():
            name = info.filename
            if name.startswith("META-INF/") and (
                name.endswith((".SF", ".RSA", ".DSA", ".EC")) or name == "META-INF/MANIFEST.MF"
            ):
                continue
            if name in ("assets/bpp-modpack.zip", "assets/bpp-modpack-manifest.tsv", "classes3.dex"):
                continue
            if name == "AndroidManifest.xml":
                data = patched_manifest
            elif name == "assets/BepInEx.Android.zip":
                data = patched_bep_zip
            else:
                data = zsrc.read(name)

            zi = zipfile.ZipInfo(name, date_time=info.date_time)
            # Keep native libraries and resources.arsc uncompressed for 16 KB page alignment
            if name.startswith("lib/") and name.endswith(".so") or name == "resources.arsc":
                zi.compress_type = zipfile.ZIP_STORED
            else:
                zi.compress_type = info.compress_type
            zi.external_attr = info.external_attr
            zi.internal_attr = info.internal_attr
            zi.create_system = info.create_system
            zdst.writestr(zi, data)

        for added_name, added_bytes, compress_type in [
            ("classes3.dex", classes3_dex, zipfile.ZIP_DEFLATED),
            ("assets/bpp-modpack.zip", normalized_modpack_bytes, zipfile.ZIP_DEFLATED),
            ("assets/bpp-modpack-manifest.tsv", manifest_tsv_bytes, zipfile.ZIP_DEFLATED),
        ]:
            zi = zipfile.ZipInfo(added_name, date_time=(2026, 10, 25, 0, 0, 0))
            zi.compress_type = compress_type
            zi.external_attr = 0o644 << 16
            zdst.writestr(zi, added_bytes)

print(f"Assembled unsigned APK: {unsigned_apk_out} (bundle_id={bundle_id[:12]}, entries={len(modpack_entries)})")
'@ | Set-Content -LiteralPath $assembleScript -Encoding UTF8

    & python $assembleScript `
        ([IO.Path]::GetFullPath($SourceApk)) `
        ([IO.Path]::GetFullPath($ModpackZip)) `
        ([IO.Path]::GetFullPath($SafeRuntime)) `
        ([IO.Path]::GetFullPath($SafeHarmony)) `
        $classes3Dex `
        $launcherToolsDir `
        $productVersion `
        $versionCode `
        $versionName `
        $unsignedApk
    if ($LASTEXITCODE -ne 0) { throw 'Failed to assemble combined APK' }

    # 4. Replace only the launcher icon PNG payloads; resources.arsc and manifest stay intact.
    & python (Join-Path $launcherToolsDir 'replace_icon.py') `
        $unsignedApk `
        (Join-Path $PSScriptRoot 'assets/icon')
    if ($LASTEXITCODE -ne 0) { throw 'Failed to replace launcher icon' }

    # 5. Page-align to 16 KB boundaries
    $zipalign = Join-Path $buildToolsDir 'zipalign.exe'
    & $zipalign -P 16 -f 4 $unsignedApk $alignedApk
    if ($LASTEXITCODE -ne 0) { throw 'zipalign failed' }

    $destinationDir = Split-Path -Parent ([IO.Path]::GetFullPath($Destination))
    if ($destinationDir) { New-Item -ItemType Directory -Force -Path $destinationDir | Out-Null }

    if ($UnsignedOnly) {
        Copy-Item -LiteralPath $alignedApk -Destination $Destination -Force
        Write-Host "Prepared unsigned aligned APK: $Destination"
        return
    }

    # 5. Sign APK (defaults to local Android debug keystore for seamless overwrite upgrades)
    $useDefaultDebugKey = [string]::IsNullOrWhiteSpace($KeyStore)
    if ($useDefaultDebugKey) {
        $KeyStore = Join-Path $env:USERPROFILE '.android/debug.keystore'
        $KeyAlias = 'androiddebugkey'
    }
    if (-not (Test-Path -LiteralPath $KeyStore -PathType Leaf)) {
        throw "Keystore not found: $KeyStore"
    }

    $apksignerJar = Join-Path $buildToolsDir 'lib/apksigner.jar'
    $signArgs = @('-jar', $apksignerJar, 'sign', '--ks', $KeyStore, '--ks-key-alias', $KeyAlias, '--out', $Destination)
    if ($useDefaultDebugKey -and [string]::IsNullOrEmpty($env:BPP_APK_STORE_PASSWORD)) {
        $signArgs += @('--ks-pass', 'pass:android', '--key-pass', 'pass:android')
    } else {
        if ([string]::IsNullOrEmpty($env:BPP_APK_STORE_PASSWORD)) {
            throw 'Set BPP_APK_STORE_PASSWORD for custom keystore signing.'
        }
        $signArgs += @('--ks-pass', 'env:BPP_APK_STORE_PASSWORD')
        if ($env:BPP_APK_KEY_PASSWORD) {
            $signArgs += @('--key-pass', 'env:BPP_APK_KEY_PASSWORD')
        }
    }
    & $java @signArgs $alignedApk
    if ($LASTEXITCODE -ne 0) { throw 'APK signing failed' }

    & $zipalign -c -P 16 4 $Destination
    if ($LASTEXITCODE -ne 0) { throw 'Post-sign 16 KB alignment verification failed' }

    & $java -jar $apksignerJar verify --verbose --print-certs $Destination
    if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed' }

    $verifyScript = Join-Path $launcherToolsDir 'verify_combined_apk.py'
    & python $verifyScript `
        ([IO.Path]::GetFullPath($SourceApk)) `
        ([IO.Path]::GetFullPath($Destination)) `
        ([IO.Path]::GetFullPath($ModpackZip)) `
        ([IO.Path]::GetFullPath($SafeRuntime)) `
        ([IO.Path]::GetFullPath($SafeHarmony)) `
        (Join-Path $PSScriptRoot 'assets/icon')
    if ($LASTEXITCODE -ne 0) { throw 'Combined APK integrity verification failed' }

    Write-Host "Signed self-deploying APK: $Destination"
} finally {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}
