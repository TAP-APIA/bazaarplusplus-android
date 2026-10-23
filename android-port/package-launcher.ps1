param(
    [string]$SourceApk = 'D:\thebarzaar\dist\BepInExLauncher-android-fixed.apk',
    [string]$Destination = 'D:\thebarzaar\dist\BepInExLauncher-android-updated.apk',
    [string]$KeyStore = 'D:\thebarzaar\tools\launcher\bpp.keystore',
    [string]$KeyAlias = 'bpp',
    [switch]$UnsignedOnly
)

# Usage (keystore credentials are documented in HANDOVER-ANDROID-PORT.md section 1.6,
# kept out of this script so the script itself stays shareable):
#
#   $env:JAVA_TOOL_OPTIONS      = $null
#   $env:BPP_APK_STORE_PASSWORD = '<store password>'
#   $env:BPP_APK_KEY_PASSWORD   = '<key password>'
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\package-launcher.ps1
#
# NOTE ON OVERWRITE UPGRADES: the currently installed launcher on the test tablet is
# signed with the Android DEBUG certificate, not with bpp.keystore. Signing with
# bpp.keystore produces an incompatible signature, so `adb install -r` fails with
# INSTALL_FAILED_UPDATE_INCOMPATIBLE and an uninstall is required. See
# HANDOVER-ANDROID-PORT.md section 4 / P2-1 before shipping an update.
$ErrorActionPreference = 'Stop'
$runtime = 'D:\thebarzaar\safe_Il2CppInterop.Runtime.dll'
$harmony = 'D:\thebarzaar\safe_Il2CppInterop.HarmonySupport.dll'
$sqlite = 'D:\thebarzaar\BazaarPlusPlus\bazaarplusplus-mod\src\BazaarPlusPlus\bin\Debug\net6.0\runtimes\android-arm64\native\libe_sqlite3.so'
$buildTools = 'D:\thebarzaar\tools\android-build\sdk\build-tools\35.0.0'
$java = 'D:\thebarzaar\tools\android-build\jdk-17.0.20.1+1\bin\java.exe'
foreach ($path in @($SourceApk, $runtime, $harmony, $sqlite)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required file not found: $path" }
}
if ([IO.Path]::GetFullPath($SourceApk) -eq [IO.Path]::GetFullPath($Destination)) {
    throw 'SourceApk and Destination must differ; keep the installed baseline recoverable.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$unsigned = [IO.Path]::ChangeExtension($Destination, 'unsigned.apk')
$aligned = [IO.Path]::ChangeExtension($Destination, 'aligned.apk')
Copy-Item -LiteralPath $SourceApk -Destination $unsigned -Force
$apk = [IO.Compression.ZipFile]::Open($unsigned, [IO.Compression.ZipArchiveMode]::Update)
try {
    $asset = $apk.GetEntry('assets/BepInEx.Android.zip')
    if ($null -eq $asset) { throw 'Launcher has no embedded BepInEx.Android.zip' }
    $buffer = [IO.MemoryStream]::new()
    $inputStream = $asset.Open()
    try { $inputStream.CopyTo($buffer) } finally { $inputStream.Dispose() }
    $buffer.Position = 0
    $bep = [IO.Compression.ZipArchive]::new($buffer, [IO.Compression.ZipArchiveMode]::Update, $true)
    try {
        $entries = @($bep.Entries | Where-Object { $_.FullName.Replace('\', '/') -match '(^|/)core/Il2CppInterop\.Runtime\.dll$' })
        if ($entries.Count -ne 1) { throw "Expected one embedded interop runtime, found $($entries.Count)" }
        $entryName = $entries[0].FullName
        $entries[0].Delete()
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($bep, $runtime, $entryName) | Out-Null
        $harmonyEntries = @($bep.Entries | Where-Object { $_.FullName.Replace('\', '/') -match '(^|/)core/Il2CppInterop\.HarmonySupport\.dll$' })
        if ($harmonyEntries.Count -ne 1) { throw "Expected one embedded Harmony support, found $($harmonyEntries.Count)" }
        $harmonyEntryName = $harmonyEntries[0].FullName
        $harmonyEntries[0].Delete()
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($bep, $harmony, $harmonyEntryName) | Out-Null
        # Some Windows ZIP writers use backslashes, which Android does not treat as separators.
        foreach ($entry in @($bep.Entries | Where-Object { $_.FullName.Contains('\') })) {
            $normalizedName = $entry.FullName.Replace('\', '/')
            if ($null -ne $bep.GetEntry($normalizedName)) {
                throw "Duplicate embedded path after normalization: $normalizedName"
            }
            $normalized = $bep.CreateEntry($normalizedName)
            $sourceStream = $entry.Open()
            $destinationStream = $normalized.Open()
            try { $sourceStream.CopyTo($destinationStream) }
            finally { $destinationStream.Dispose(); $sourceStream.Dispose() }
            $entry.Delete()
        }
    } finally { $bep.Dispose() }
    $asset.Delete()
    $newAsset = $apk.CreateEntry('assets/BepInEx.Android.zip')
    $outputStream = $newAsset.Open()
    try { $buffer.Position = 0; $buffer.CopyTo($outputStream) } finally { $outputStream.Dispose(); $buffer.Dispose() }
    $oldSqlite = $apk.GetEntry('lib/arm64-v8a/libe_sqlite3.so')
    if ($null -ne $oldSqlite) { $oldSqlite.Delete() }
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($apk, $sqlite, 'lib/arm64-v8a/libe_sqlite3.so', [IO.Compression.CompressionLevel]::NoCompression) | Out-Null
    foreach ($signature in @($apk.Entries | Where-Object { $_.FullName -match '^META-INF/.*\.(SF|RSA|DSA|EC)$' })) {
        $signature.Delete()
    }
} finally { $apk.Dispose() }
& "$buildTools\zipalign.exe" -P 16 -f 4 $unsigned $aligned
if ($LASTEXITCODE -ne 0) { throw 'zipalign failed' }
if ($UnsignedOnly) {
    Write-Host "Prepared unsigned launcher: $aligned"
    return
}
if ([string]::IsNullOrEmpty($env:BPP_APK_STORE_PASSWORD)) {
    throw 'Set BPP_APK_STORE_PASSWORD to the existing keystore password. The prepared APK is preserved.'
}
$signArguments = @('-jar', "$buildTools\lib\apksigner.jar", 'sign', '--ks', $KeyStore, '--ks-pass', 'env:BPP_APK_STORE_PASSWORD', '--out', $Destination)
if ($KeyAlias) { $signArguments += @('--ks-key-alias', $KeyAlias) }
if ($env:BPP_APK_KEY_PASSWORD) { $signArguments += @('--key-pass', 'env:BPP_APK_KEY_PASSWORD') }
& $java @signArguments $aligned
if ($LASTEXITCODE -ne 0) { throw 'APK signing failed' }
& $java -jar "$buildTools\lib\apksigner.jar" verify --verbose --print-certs $Destination
if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed' }
Write-Host "Signed launcher: $Destination"
