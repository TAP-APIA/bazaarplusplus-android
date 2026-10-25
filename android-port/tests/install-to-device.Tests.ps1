# Dependency-free deployment contract tests. No real device or adb is contacted.
$ErrorActionPreference = 'Stop'
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ('bpp-deploy-test-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $sandbox | Out-Null
$pack = Join-Path $sandbox 'modpack with spaces'
New-Item -ItemType Directory -Path "$pack/plugins", "$pack/core" | Out-Null
$pluginFiles = @(
    'BazaarPlusPlus.dll', 'BazaarPlusPlus.Localization.dll', 'BazaarPlusPlus.ModApi.dll',
    'BazaarPlusPlus.Storage.dll', 'Bpp.Managed.MessagePack.dll',
    'Bpp.Managed.MessagePack.Annotations.dll', 'Bpp.Managed.StringTools.dll',
    'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.core.dll',
    'SQLitePCLRaw.provider.e_sqlite3.dll', 'SQLitePCLRaw.batteries_v2.dll', 'libe_sqlite3.so'
)
$coreFiles = @('BepInEx.Unity.IL2CPP.dll', 'BepInEx.Core.dll', 'Il2CppInterop.Runtime.dll', 'Il2CppInterop.HarmonySupport.dll')
foreach ($file in $pluginFiles) { Set-Content "$pack/plugins/$file" 'fixture' }
foreach ($file in $coreFiles) { Set-Content "$pack/core/$file" 'fixture' }
$fileCount = $pluginFiles.Count + $coreFiles.Count
Set-Content "$pack/modpack.json" '{"name":"bpp","packageName":"com.tempogames.thebazaar"}'
$mock = Join-Path $sandbox 'adb.ps1'
$calls = [System.Collections.Generic.List[string]]::new()
$global:BppTestAdbCalls = $calls
$global:BppTestAdbMode = ''
@'
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
$global:BppTestAdbCalls.Add(($Arguments -join ' '))
$global:LASTEXITCODE = 0
if ($Arguments[0] -eq 'devices') {
    'List of devices attached'
    "fixture-device`tdevice"
    if ($global:BppTestAdbMode -eq 'multiple') { "other-device`tdevice" }
}
if ($global:BppTestAdbMode -eq 'missing' -and ($Arguments -join ' ') -match 'test -d') {
    $global:LASTEXITCODE = 1
}
if ($global:BppTestAdbMode -eq 'push-fails' -and $Arguments -contains 'push') {
    $global:LASTEXITCODE = 1
}
'@ | Set-Content -LiteralPath $mock
$installer = Join-Path $PSScriptRoot '../install-to-device.ps1'
function Assert-True($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Assert-True $rejected $Message
}
try {
    & $installer -Modpack $pack -Adb $mock -Confirm:$false
    $pushes = @($calls | Where-Object { $_ -match ' push ' })
    Assert-True ($pushes.Count -eq ($fileCount * 2 + 1)) 'Expected both copies of plugin, SQLite, core, plus manifest.'
    Assert-True ($pushes[0] -like '*/Android/data/com.bepinex.android/files/BepInEx_Android/*') 'Wrong default storage root.'
    Assert-True (@($pushes | Where-Object { $_ -match '/BepInEx/(plugins|core)/' }).Count -eq $fileCount) 'Active runtime was not updated.'
    Assert-True (@($calls | Where-Object { $_ -match 'am force-stop' }).Count -eq 1) 'Launcher must stop before writes.'
    Assert-True (-not ($calls -match 'uninstall|clear |/config|BazaarPlusPlusV6')) 'Deployment touched user data.'

    $calls.Clear()
    & $installer -Modpack $pack -Adb $mock -WhatIf
    Assert-True (-not ($calls -match ' push |mkdir|force-stop')) 'WhatIf mutated the device.'

    foreach ($mode in @('multiple', 'missing', 'push-fails')) {
        $calls.Clear()
        $global:BppTestAdbMode = $mode
        Assert-Rejected { & $installer -Modpack $pack -Adb $mock -Confirm:$false } "$mode was accepted."
        if ($mode -ne 'push-fails') {
            Assert-True (-not ($calls -match ' push |force-stop')) "$mode wrote before validation."
        } else {
            Assert-True (@($calls | Where-Object { $_ -match ' push ' }).Count -eq 1) 'Failed push did not stop deployment.'
        }
    }
    $global:BppTestAdbMode = ''
    $calls.Clear()
    Assert-Rejected { & $installer -Modpack $pack -Adb $mock -StorageRoot '/sdcard/../data' } 'Traversal path was accepted.'
    Assert-True ($calls.Count -eq 0) 'Invalid path reached adb.'

    foreach ($file in @('plugins/BazaarPlusPlus.dll', 'plugins/libe_sqlite3.so', 'core/Il2CppInterop.Runtime.dll', 'core/Il2CppInterop.HarmonySupport.dll')) {
        $path = Join-Path $pack $file
        Move-Item -LiteralPath $path -Destination "$path.saved"
        try {
            Assert-Rejected { & $installer -Modpack $pack -Adb $mock } "Missing $file was accepted."
            Assert-True ($calls.Count -eq 0) 'Incomplete modpack reached adb.'
        } finally { Move-Item -LiteralPath "$path.saved" -Destination $path }
    }

    Set-Content "$pack/modpack.json" '{"name":"other","packageName":"com.tempogames.thebazaar"}'
    Assert-Rejected { & $installer -Modpack $pack -Adb $mock } 'Mismatched manifest was accepted.'
    Write-Host 'PASS: dual copies, scoped storage, stop-before-write, no user data, WhatIf, multi-device, missing root, push failure, path validation, missing payloads, manifest validation.'
} finally {
    Remove-Variable -Scope Global -Name BppTestAdbCalls, BppTestAdbMode
    # Leave the uniquely named fixture directory for inspection; it contains no device data.
}
