$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
[xml]$product = Get-Content -LiteralPath (Join-Path $repoRoot 'src/Lokad.Sift/Lokad.Sift.csproj') -Raw
$guard = $product.Project.Target | Where-Object Name -eq 'RejectInternalVerificationInProduct'
if (-not $guard) { throw 'The product verification-symbol guard is missing.' }

# Exercise the actual guard before dummy compile/pack targets. Never enable the
# symbol on the library or produce an instrumented product/package for this check.
$probeRoot = Join-Path ([IO.Path]::GetTempPath()) ('sift-guard-check-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $probeRoot | Out-Null
    $probe = [xml]'<Project><Target Name="CoreCompile"/><Target Name="Pack"/></Project>'
    $null = $probe.DocumentElement.AppendChild($probe.ImportNode($guard, $true))
    $probePath = Join-Path $probeRoot 'Guard.proj'
    $probe.Save($probePath)
    $cases = @(
        @{ Symbols = ''; Rejected = $false },
        @{ Symbols = 'TRACE;SIFT_INTERNAL_VERIFICATION_EXTRA'; Rejected = $false },
        @{ Symbols = 'sift_internal_verification'; Rejected = $false },
        @{ Symbols = 'SIFT_INTERNAL_VERIFICATION'; Rejected = $true },
        @{ Symbols = ' SIFT_INTERNAL_VERIFICATION '; Rejected = $true },
        @{ Symbols = 'TRACE; SIFT_INTERNAL_VERIFICATION ;DEBUG'; Rejected = $true },
        @{ Symbols = 'TRACE,SIFT_INTERNAL_VERIFICATION,DEBUG'; Rejected = $true },
        @{ Symbols = "TRACE`tSIFT_INTERNAL_VERIFICATION`r`nDEBUG"; Rejected = $true }
    )
    foreach ($case in $cases) {
        $encoded = $case.Symbols.Replace('%', '%25').Replace(';', '%3B').Replace(',', '%2C').Replace(' ', '%20').Replace("`t", '%09').Replace("`r", '%0D').Replace("`n", '%0A')
        foreach ($target in @('CoreCompile', 'Pack')) {
            $output = & dotnet msbuild $probePath --tl:off --nologo -v:minimal "-t:$target" "-p:DefineConstants=$encoded" 2>&1
            $rejected = $LASTEXITCODE -ne 0
            if ($rejected -ne $case.Rejected -or ($rejected -and "$output" -notmatch 'SIFT_INTERNAL_VERIFICATION is test-only')) {
                throw "Unexpected verification guard result for '$($case.Symbols)' before $target`: $output"
            }
        }
    }
    Write-Output 'Verification-symbol guard passed compile/pack boundary checks.'
}
finally {
    if (Test-Path -LiteralPath $probeRoot) {
        $cleanupRoot = [IO.Path]::GetFullPath($probeRoot)
        if ([IO.Path]::GetDirectoryName($cleanupRoot) -ne [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) -or
            [IO.Path]::GetFileName($cleanupRoot) -notmatch '^sift-guard-check-[0-9a-f]{32}$') {
            throw 'Refusing to remove an unexpected guard-check directory.'
        }
        Remove-Item -LiteralPath $cleanupRoot -Recurse -Force
    }
}
exit 0
