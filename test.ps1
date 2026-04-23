<# 
Runs the Sift solution test suite from the repo root with an optional build step and test filter.
Also clears stale compiler and testhost processes to keep local runs repeatable.
#>

param(
    [string]$Configuration = "Debug",
    [switch]$SkipBuild,
    [string]$Filter
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
Set-Location $repoRoot

$staleProcesses = @("testhost", "VBCSCompiler")
foreach ($processName in $staleProcesses) {
    Get-Process -Name $processName -ErrorAction SilentlyContinue | Stop-Process -Force
}

if (-not $SkipBuild) {
    dotnet build Lokad.Sift.slnx --configuration $Configuration --tl:off --nologo -v minimal --no-restore
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$testArguments = @(
    "test",
    "Lokad.Sift.slnx",
    "--configuration", $Configuration,
    "--tl:off",
    "--nologo",
    "-v", "minimal",
    "--no-build",
    "--no-restore"
)

if ($Filter) {
    $testArguments += @("--filter", $Filter)
}

dotnet @testArguments
exit $LASTEXITCODE
