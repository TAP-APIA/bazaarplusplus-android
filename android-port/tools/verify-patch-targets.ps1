param(
    [string] $DumpCs = 'D:\thebarzaar\dumped3\dump.cs',
    [string] $Source = 'D:\thebarzaar\BazaarPlusPlus\bazaarplusplus-mod\src\BazaarPlusPlus'
)

# Verifies that every Harmony patch target type exists in the dumped game assembly.
#
# A missing target does not fail the build: Harmony resolves it at runtime and only logs an
# error, so the plugin loads while the feature silently stays absent. This check turns that
# into a build-time signal after every game update (regenerate dump.cs with Il2CppDumper first).

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $DumpCs)) { throw "dump.cs not found: $DumpCs" }
if (-not (Test-Path $Source)) { throw "Source not found: $Source" }

Write-Host "Indexing type declarations in $DumpCs ..."
$types = New-Object 'System.Collections.Generic.HashSet[string]'
$reader = [System.IO.File]::OpenText($DumpCs)
try {
    while ($null -ne ($line = $reader.ReadLine())) {
        if ($line -match '^\s*(?:public |internal |private |protected |static |sealed |abstract |partial |readonly |unsafe )*(?:class|struct|interface|enum)\s+([A-Za-z0-9_]+)') {
            [void]$types.Add($Matches[1])
        }
    }
}
finally { $reader.Close() }

$files = Get-ChildItem $Source -Recurse -Filter *.cs | Select-Object -ExpandProperty FullName
$targets = Select-String -Path $files -Pattern '\[HarmonyPatch\(typeof\(([A-Za-z0-9_]+)\)' |
    ForEach-Object { $_.Matches[0].Groups[1].Value } |
    Sort-Object -Unique

Write-Host ("Patch target types: {0}" -f $targets.Count)

$missing = @($targets | Where-Object { -not $types.Contains($_) })
if ($missing.Count -eq 0) {
    Write-Host "All patch target types exist in the game assembly."
    exit 0
}

Write-Host ("MISSING {0} target type(s):" -f $missing.Count)
$missing | ForEach-Object { Write-Host ("  " + $_) }
exit 1
