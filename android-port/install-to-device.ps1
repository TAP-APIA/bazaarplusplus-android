param(
    [string] $Modpack = 'D:\thebarzaar\dist\bpp-modpack',
    [string] $Package = 'com.tempogames.thebazaar',
    [string] $ModpackName = 'bpp',
    [string] $Adb = 'D:\thebarzaar\tools\platform-tools\platform-tools\adb.exe',
    [string] $StorageRoot = '/sdcard/BepInEx_Android',
    [string] $DeviceId
)

# Pushes the packaged modpack to a connected device.
#
# Layout expected by NextBep / BepInEx Android:
#   <StorageRoot>/<package>/modpacks/<modpack>/plugins/*.dll
#   <StorageRoot>/<package>/modpacks/<modpack>/modpack.json
# The device also needs the "bpp" modpack selected in the launcher before the plugins load.

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Adb)) { throw "adb not found: $Adb" }
if (-not (Test-Path (Join-Path $Modpack 'plugins'))) {
    throw "Packaged modpack not found: $Modpack (run package-modpack.ps1 first)."
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
Write-Host "Pushing to $target"

& $Adb -s $DeviceId shell "mkdir -p $target/plugins" | Out-Null
& $Adb -s $DeviceId shell "mkdir -p $target/config $target/logs" | Out-Null

foreach ($directory in @('plugins', 'core')) {
    $source = Join-Path $Modpack $directory
    if (-not (Test-Path -LiteralPath $source)) { continue }
    & $Adb -s $DeviceId shell "mkdir -p $target/$directory $runtimeTarget/$directory" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Cannot create device directory: $directory" }
    Get-ChildItem -LiteralPath $source -Filter *.dll | ForEach-Object {
        foreach ($destination in @("$target/$directory", "$runtimeTarget/$directory")) {
            & $Adb -s $DeviceId push $_.FullName "$destination/$($_.Name)" 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "adb push failed: $($_.Name) -> $destination" }
        }
        Write-Host ("  pushed both copies: " + $directory + '/' + $_.Name)
    }
}

$manifestPath = Join-Path $Modpack 'modpack.json'
if (Test-Path $manifestPath) {
    & $Adb -s $DeviceId push $manifestPath "$target/modpack.json" 2>&1 | Out-Null
    Write-Host "  pushed modpack.json"
}

Write-Host ""
Write-Host "Next: unlock the tablet, open the NextBep launcher, select the '$ModpackName' modpack,"
Write-Host "then start the game. BepInEx log: $target/logs or $StorageRoot/$Package/BepInEx/LogOutput.log"
