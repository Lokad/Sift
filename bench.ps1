<#
Runs the Sift benchmark harness from the repo root and emits a JSON report for the selected scenario.
Supports the real-corpus path by default plus specialized postings, update, and compaction benchmark modes.
#>

param(
    [string]$Configuration = "Release",
    [switch]$SkipBuild,
    [switch]$PostingsMicro,
    [switch]$PostingsStats,
    [switch]$PostingsBdn,
    [switch]$UpdateBench,
    [switch]$CompactBench,
    [string[]]$Corpora,
    [string]$RealCorpusRoot = "bench/repos",
    [string]$IndexCacheRoot = "bench/indexes",
    [switch]$UseIndexCache,
    [switch]$QueryOnly,
    [switch]$RefreshIndex,
    [switch]$PrepareIndexOnly,
    [switch]$FullRepo,
    [string]$QueryId,
    [int]$Warmup = 1,
    [int]$Iterations = 1,
    [int]$MaxFiles = 100,
    [string]$QueryPack = "src/Lokad.Sift.Benchmarks/Assets/real-diverse.json",
    [string[]]$BdnFilter,
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
Set-Location $repoRoot

$benchmarkAssembly = Join-Path $repoRoot "src\Lokad.Sift.Benchmarks\bin\$Configuration\net10.0\Lokad.Sift.Benchmarks.dll"

if ($Configuration -ne 'Release') { throw 'Benchmarks require -Configuration Release.' }
$lockDirectory = Join-Path $repoRoot 'bench/out'
New-Item -ItemType Directory -Path $lockDirectory -Force | Out-Null
$runLock = $null
$benchmarkProcess = $null
try {
    try {
        $runLock = [IO.FileStream]::new((Join-Path $lockDirectory '.run.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    catch [IO.IOException] {
        if (($_.Exception.HResult -band 0xFFFF) -notin @(11, 32, 33, 35)) { throw }
        throw 'Another benchmark launch is active in this checkout.'
    }

    if (-not $SkipBuild) {
        dotnet build Lokad.Sift.slnx --configuration $Configuration --tl:off --nologo -v minimal --no-restore
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }

    if (-not (Test-Path $benchmarkAssembly)) {
        throw "Expected benchmark assembly at '$benchmarkAssembly'."
    }

    if (-not $OutputPath) {
        $outputDirectory = Join-Path $repoRoot "bench\out"
        New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
        $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
        if ($PostingsMicro) {
            $OutputPath = Join-Path $outputDirectory "postings-micro-$timestamp.json"
        }
        elseif ($PostingsStats) {
            $OutputPath = Join-Path $outputDirectory "postings-stats-$timestamp.json"
        }
        elseif ($PostingsBdn) {
            $OutputPath = Join-Path $outputDirectory "postings-bdn-$timestamp.json"
        }
        elseif ($UpdateBench) {
            $OutputPath = Join-Path $outputDirectory "update-bench-$timestamp.json"
        }
        elseif ($CompactBench) {
            $OutputPath = Join-Path $outputDirectory "compact-bench-$timestamp.json"
        }
        else {
            $OutputPath = Join-Path $outputDirectory "real-corpus-$timestamp.json"
        }
    }

    $benchmarkArguments = @(
        $benchmarkAssembly,
        "--json=$OutputPath",
        "--warmup=$Warmup",
        "--iterations=$Iterations"
    )

    if ($PostingsMicro) {
        $benchmarkArguments += "--postings-micro"
    }
    elseif ($PostingsStats) {
        $benchmarkArguments += "--postings-stats"

        if ($Corpora -and $Corpora.Count -gt 0) {
            $benchmarkArguments += "--corpora=$($Corpora -join ',')"
        }

        if ($IndexCacheRoot) {
            $benchmarkArguments += "--index-cache-root=$IndexCacheRoot"
        }
    }
    elseif ($PostingsBdn) {
        $benchmarkArguments += "--postings-bdn"

        if ($BdnFilter -and $BdnFilter.Count -gt 0) {
            $benchmarkArguments += "--bdn-filter=$($BdnFilter -join ',')"
        }
    }
    elseif ($UpdateBench) {
        $benchmarkArguments += "--update-bench"

        if ($Corpora -and $Corpora.Count -gt 0) {
            $benchmarkArguments += "--corpora=$($Corpora -join ',')"
        }

        if ($RealCorpusRoot) {
            $benchmarkArguments += "--real-corpus-root=$RealCorpusRoot"
        }

        if ($IndexCacheRoot) {
            $benchmarkArguments += "--index-cache-root=$IndexCacheRoot"
        }

        if ($RefreshIndex) {
            $benchmarkArguments += "--refresh-index"
        }
    }
    elseif ($CompactBench) {
        $benchmarkArguments += "--compact-bench"

        if ($Corpora -and $Corpora.Count -gt 0) {
            $benchmarkArguments += "--corpora=$($Corpora -join ',')"
        }

        if ($RealCorpusRoot) {
            $benchmarkArguments += "--real-corpus-root=$RealCorpusRoot"
        }

        if ($IndexCacheRoot) {
            $benchmarkArguments += "--index-cache-root=$IndexCacheRoot"
        }

        if ($RefreshIndex) {
            $benchmarkArguments += "--refresh-index"
        }
    }
    else {
        $benchmarkArguments += "--real-corpus"

        if (-not $FullRepo) {
            $benchmarkArguments += "--max-files=$MaxFiles"
        }

        if ($Corpora -and $Corpora.Count -gt 0) {
            $benchmarkArguments += "--corpora=$($Corpora -join ',')"
        }

        if ($RealCorpusRoot) {
            $benchmarkArguments += "--real-corpus-root=$RealCorpusRoot"
        }

        if ($IndexCacheRoot) {
            $benchmarkArguments += "--index-cache-root=$IndexCacheRoot"
        }

        if ($UseIndexCache) {
            $benchmarkArguments += "--use-index-cache"
        }

        if ($QueryOnly) {
            $benchmarkArguments += "--query-only"
        }

        if ($RefreshIndex) {
            $benchmarkArguments += "--refresh-index"
        }

        if ($PrepareIndexOnly) {
            $benchmarkArguments += "--prepare-index-only"
        }

        if ($QueryPack) {
            $benchmarkArguments += "--real-query-pack-json=$QueryPack"
        }

        if ($QueryId) {
            $benchmarkArguments += "--query-id=$QueryId"
        }
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WorkingDirectory = $repoRoot
    foreach ($argument in $benchmarkArguments) { $startInfo.ArgumentList.Add($argument) }
    $benchmarkProcess = [Diagnostics.Process]::Start($startInfo)
    while (-not $benchmarkProcess.WaitForExit(1000)) { }
    $benchmarkExitCode = $benchmarkProcess.ExitCode
    if ($benchmarkExitCode -eq 0) { Write-Output "Benchmark report: $OutputPath" }
    exit $benchmarkExitCode
}
finally {
    if ($null -ne $benchmarkProcess) {
        if (-not $benchmarkProcess.HasExited) {
            $benchmarkProcess.Kill($true)
            $benchmarkProcess.WaitForExit()
        }
        $benchmarkProcess.Dispose()
    }
    if ($null -ne $runLock) { $runLock.Dispose() }
}
