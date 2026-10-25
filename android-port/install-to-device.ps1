[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Modpack = '',
    [string] $Package = 'com.tempogames.thebazaar',
    [string] $ModpackName = 'bpp',
    [string] $Adb = '',
    [string] $StorageRoot = '/sdcard/Android/data/com.bepinex.android/files/BepInEx_Android',
    [string] $DeviceId
)

# Pushes the packaged modpack to a connected device.
#
# Layout expected by NextBep / BepInEx Android:
#   <StorageRoot>/<package>/modpacks/<modpack>/plugins/*.dll
#   <StorageRoot>/<package>/modpacks/<modpack>/modpack.json
# The device also needs the "bpp" modpack selected in the launcher before the plugins load.

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Modpack)) {
    $Modpack = Join-Path $PSScriptRoot 'bpp-modpack'
    if (-not (Test-Path -LiteralPath $Modpack)) {
        $Modpack = Join-Path $PSScriptRoot '../dist/bpp-modpack'
    }
}
if ($Package -notmatch '^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+$' -or
    $ModpackName -notmatch '^[A-Za-z0-9_-]+$' -or
    $StorageRoot -notmatch '^/sdcard(?:/[A-Za-z0-9_.-]+)+$' -or
    $StorageRoot.Split('/') -contains '..') {
    throw 'Invalid package, modpack name or storage path.'
}

if ([string]::IsNullOrWhiteSpace($Adb)) {
    if (Get-Command adb -ErrorAction SilentlyContinue) {
        $Adb = (Get-Command adb).Source
    } elseif (Test-Path "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe") {
        $Adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
    } else {
        throw "adb executable not found."
    }
}
if (-not (Test-Path $Adb)) { throw "adb not found: $Adb" }
if (-not (Test-Path (Join-Path $Modpack 'plugins'))) {
    throw "Packaged modpack not found: $Modpack (run package-modpack.ps1 first)."
}
$manifestPath = Join-Path $Modpack 'modpack.json'
foreach ($required in @(
    'modpack.json', 'plugins/BazaarPlusPlus.dll',
    'plugins/BazaarPlusPlus.Localization.dll', 'plugins/BazaarPlusPlus.ModApi.dll',
    'plugins/BazaarPlusPlus.Storage.dll', 'plugins/Bpp.Managed.MessagePack.dll',
    'plugins/Bpp.Managed.MessagePack.Annotations.dll', 'plugins/Bpp.Managed.StringTools.dll',
    'plugins/Microsoft.Data.Sqlite.dll', 'plugins/SQLitePCLRaw.core.dll',
    'plugins/SQLitePCLRaw.provider.e_sqlite3.dll', 'plugins/SQLitePCLRaw.batteries_v2.dll',
    'plugins/libe_sqlite3.so', 'core/BepInEx.Unity.IL2CPP.dll',
    'core/BepInEx.Core.dll', 'core/Il2CppInterop.Runtime.dll',
    'core/Il2CppInterop.HarmonySupport.dll'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $Modpack $required) -PathType Leaf)) {
        throw "Incomplete modpack: required file missing: $required"
    }
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.packageName -ne $Package -or $manifest.name -ne $ModpackName) {
    throw 'The modpack manifest does not match -Package / -ModpackName.'
}

if ([string]::IsNullOrWhiteSpace($DeviceId)) {
    $deviceLines = & $Adb devices
    if ($LASTEXITCODE -ne 0) { throw 'ADB device discovery failed' }
    $onlineDevices = @($deviceLines | ForEach-Object {
        if ($_ -match '^(\S+)\s+device(?:\s|$)') { $Matches[1] }
    })
    if ($onlineDevices.Count -ne 1) {
        throw "Expected one online ADB device; found $($onlineDevices.Count). Specify -DeviceId."
    }
    $DeviceId = $onlineDevices[0]
}

$target = "$StorageRoot/$Package/modpacks/$ModpackName"
$runtimeTarget = "$StorageRoot/$Package/BepInEx"
& $Adb -s $DeviceId shell "test -d $StorageRoot/$Package"
if ($LASTEXITCODE -ne 0) {
    throw "Game directory missing: $StorageRoot/$Package. Add the game and launch it once in BepInEx Launcher first. For a legacy launcher, explicitly pass -StorageRoot /sdcard/BepInEx_Android."
}
if (-not $PSCmdlet.ShouldProcess("$DeviceId : $target", 'Stop launcher and update both modpack and active runtime')) {
    return
}
& $Adb -s $DeviceId shell am force-stop com.bepinex.android
if ($LASTEXITCODE -ne 0) { throw 'Cannot stop BepInEx Launcher; no files were pushed.' }
Write-Host "Pushing to $target"

foreach ($directory in @('plugins', 'core')) {
    $source = Join-Path $Modpack $directory
    if (-not (Test-Path -LiteralPath $source)) { continue }
    & $Adb -s $DeviceId shell "mkdir -p $target/$directory $runtimeTarget/$directory" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Cannot create device directory: $directory" }
    Get-ChildItem -LiteralPath $source -File | Where-Object {
        $directory -eq 'core' -or $_.Extension -in @('.dll', '.so')
    } | ForEach-Object {
        foreach ($destination in @("$target/$directory", "$runtimeTarget/$directory")) {
            # adb writes its progress line to stderr. Windows PowerShell 5.1 wraps a native
            # command's stderr into a NativeCommandError the moment $ErrorActionPreference is
            # 'Stop', and neither 2>&1 nor 2>$null suppresses it, so the loop aborts before the
            # BepInEx/plugins copy is pushed and that directory silently goes stale. Drop the
            # preference to 'Continue' for the call and judge success by the exit code alone.
            $previousPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            & $Adb -s $DeviceId push $_.FullName "$destination/$($_.Name)" 2>&1 | Out-Null
            $pushExitCode = $LASTEXITCODE
            $ErrorActionPreference = $previousPreference
            if ($pushExitCode -ne 0) { throw "adb push failed: $($_.Name) -> $destination" }
        }
        Write-Host ("  pushed both copies: " + $directory + '/' + $_.Name)
    }
}

if (Test-Path $manifestPath) {
    # Same native-stderr trap as the DLL loop above.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $Adb -s $DeviceId push $manifestPath "$target/modpack.json" 2>&1 | Out-Null
    $pushExitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousPreference
    if ($pushExitCode -ne 0) { throw "adb push failed: modpack.json -> $target" }
    Write-Host "  pushed modpack.json"
}

Write-Host ""
Write-Host "Next: unlock the tablet, open the NextBep launcher, select the '$ModpackName' modpack,"
Write-Host "then start the game. BepInEx log: $target/logs or $StorageRoot/$Package/BepInEx/LogOutput.log"
