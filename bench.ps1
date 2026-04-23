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

$staleBenchmarks = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf("Lokad.Sift.Benchmarks.dll", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 }
foreach ($process in $staleBenchmarks) {
    Stop-Process -Id $process.ProcessId -Force
}

Get-Process -Name VBCSCompiler -ErrorAction SilentlyContinue | Stop-Process -Force

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

dotnet @benchmarkArguments
if ($LASTEXITCODE -eq 0) {
    Write-Output "Benchmark report: $OutputPath"
}

exit $LASTEXITCODE
