# PZA's Meitou UI fuzz kit: build and run one repo's fuzz harness (tests\fuzz\fuzz.csproj). Author: PZA. Licence: GPLv3 or later.
#
#   .\run_fuzz.ps1 -Repo ..\..\PZAMeitouSlowMo                   the full run (report in <repo>\tests\out\fuzz\fuzz-report.json / .txt)
#   -Seed N                 another seed (default 20261118); -Scale 0.1 = a tenth of the cases (the mutation check); -Only a,b  some targets
#   -MeitouDir <dir>        the unpacked Meitou release (default: MeitouDir in <repo>\mod\local.props, else in this folder's local.props; both git-ignored)
#   -Out <dir>              where the build, the runtime copy and the report go (default <repo>\tests\out\fuzz)
#
# It runs on a copy of the .NET 11 runtime inside the Meitou release (as the mods' build.ps1 -Check); it never starts the game and installs
# nothing. Exit code 0 = RESULT: PASS. The report records the repo's commit and whether the tree had uncommitted changes (the release gate
# only accepts a pass for the exact commit it packages, with a clean tree).
param(
    [Parameter(Mandatory = $true)][string]$Repo,
    [string]$Seed = "20261118",
    [string]$Scale = "1",
    [string]$Only = "",
    [string]$MeitouDir = "",
    [string]$Out = "",
    [string]$KenshiDir = "C:\Program Files (x86)\Steam\steamapps\common\Kenshi"
)
$ErrorActionPreference = "Stop"
$Repo = [System.IO.Path]::GetFullPath($Repo)
$proj = Join-Path $Repo "tests\fuzz\fuzz.csproj"
if (-not (Test-Path $proj)) { throw "no fuzz harness: $proj" }
if ($MeitouDir -eq "") {
    $lp = Join-Path $Repo "mod\local.props"
    if (Test-Path $lp) { $MeitouDir = ([xml](Get-Content $lp -Raw)).Project.PropertyGroup.MeitouDir }
}
if (-not $MeitouDir) {
    $kp = Join-Path $PSScriptRoot "local.props"
    if (Test-Path $kp) { $MeitouDir = ([xml](Get-Content $kp -Raw)).Project.PropertyGroup.MeitouDir }
}
if (-not $MeitouDir) { throw "no Meitou release: pass -MeitouDir, or set MeitouDir in <repo>\mod\local.props or $PSScriptRoot\local.props" }
$MeitouDir = [System.IO.Path]::GetFullPath($MeitouDir)
if (-not (Test-Path (Join-Path $MeitouDir "MeitouClient.dll"))) { throw "MeitouClient.dll not found in $MeitouDir" }
if ($Out -eq "") { $Out = Join-Path $Repo "tests\out\fuzz" }
$Out = [System.IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force $Out | Out-Null

$commit = ""; $dirty = "unknown"
try {
    $commit = (& git -C $Repo rev-parse HEAD 2>$null)
    $st = (& git -C $Repo status --porcelain --untracked-files=no 2>$null)
    $dirty = if ($st) { "true" } else { "false" }
} catch { }

$bin = Join-Path $Out "bin"
& dotnet build $proj -c Release -nologo -v q "-p:MeitouDir=$MeitouDir" -o $bin
if ($LASTEXITCODE -ne 0) { Write-Host "RESULT: FAIL (fuzz harness build failed)"; exit 2 }

# the runtime: a copy of the Meitou release's own .NET 11 (refreshed when the release folder changes)
$rt = Join-Path $Out "rt"
$stamp = Join-Path $rt "meitou-dir.txt"
if (-not (Test-Path $stamp) -or (Get-Content $stamp -Raw).Trim() -ne $MeitouDir) {
    if (Test-Path $rt) { Remove-Item -Recurse -Force $rt }
    New-Item -ItemType Directory -Force $rt | Out-Null
    $patterns = "System*.dll", "Microsoft.*.dll", "mscorlib.dll", "netstandard.dll", "WindowsBase.dll", "coreclr.dll", "clrjit.dll",
                "clrgc.dll", "hostfxr.dll", "hostpolicy.dll", "Meitou*.dll", "0Harmony.dll", "Bepu*.dll", "DotRecast*.dll", "*.so", "*Native*.dll"
    foreach ($p in $patterns) { Copy-Item (Join-Path $MeitouDir $p) $rt -Force -ErrorAction SilentlyContinue }
    Set-Content -Path $stamp -Value $MeitouDir -Encoding ascii
}
Copy-Item (Join-Path $bin "fuzz.dll") $rt -Force
Get-ChildItem $bin -Filter *.dll | Where-Object { $_.Name -ne "fuzz.dll" -and -not (Test-Path (Join-Path $rt $_.Name)) } | ForEach-Object { Copy-Item $_.FullName $rt }
# native libraries the mod ships (LEAFMeitou's leafcore.dll): tests\fuzz\native.txt lists them, one path from the repo per line: the very
# files the mod's build stages, so the fuzz tests what ships. A listed file that is missing fails the run (build it first).
$natList = Join-Path $Repo "tests\fuzz\native.txt"
if (Test-Path $natList) {
    foreach ($line in Get-Content $natList) {
        $l = $line.Trim(); if ($l -eq "" -or $l.StartsWith("#")) { continue }
        $f = Join-Path $Repo $l
        if (-not (Test-Path $f)) { Write-Host "RESULT: FAIL (native library $l not built)"; exit 2 }
        Copy-Item $f $rt -Force
    }
}
$cfg = '{ "runtimeOptions": { "tfm": "net11.0", "includedFrameworks": [ { "name": "Microsoft.NETCore.App", "version": "11.0.0" } ] } }'
Set-Content -Path (Join-Path $rt "fuzz.runtimeconfig.json") -Value $cfg -Encoding ascii

$env:PZA_KENSHI = $KenshiDir
$a = @("--seed", $Seed, "--cases-scale", $Scale, "--report", $Out, "--commit", "$commit", "--dirty", $dirty)
if ($Only) { $a += @("--only", $Only) }
Push-Location $rt
try { & dotnet exec --runtimeconfig fuzz.runtimeconfig.json fuzz.dll @a } finally { Pop-Location }
exit $LASTEXITCODE
