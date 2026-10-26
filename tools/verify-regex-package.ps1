param([switch]$SkipPack, [switch]$KeepTemporaryFiles)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'verify-build-guard.ps1')
$packageFeed = Join-Path $repoRoot 'artifacts\regex-decoupling\nuget'
$checkRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('sift-package-check-' + [guid]::NewGuid().ToString('N'))
try {
[xml]$projectDefinition = Get-Content -LiteralPath (Join-Path $repoRoot 'src\Lokad.Sift\Lokad.Sift.csproj') -Raw
$packageVersion = $projectDefinition.Project.PropertyGroup.Version
[xml]$centralVersions = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Packages.props') -Raw
$producerVersion = ($centralVersions.Project.ItemGroup.PackageVersion | Where-Object Include -eq 'Lokad.Utf8Regex').Version

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('dotnet failed: ' + ($Arguments -join ' ')) }
}

$globalPackagesOutput = & dotnet nuget locals global-packages --list
if ($LASTEXITCODE -ne 0) { throw 'Could not locate the restored build/test packages.' }
$globalPackagesRoot = ($globalPackagesOutput -replace '^global-packages:\s*', '').Trim()

# Compile an exact source copy with only the locked private build packages
# available. The separate feed and package cache contain no regex producer.
$sourceRoot = Join-Path $checkRoot 'source'
$isolatedCore = Join-Path $sourceRoot 'src\Lokad.Sift'
$buildFeed = Join-Path $checkRoot 'build-feed'
$buildPackages = Join-Path $checkRoot 'build-packages'
New-Item -ItemType Directory -Path $isolatedCore, $buildFeed -Force | Out-Null
$sdkPolicy = Join-Path $repoRoot 'global.json'
if (Test-Path -LiteralPath $sdkPolicy) {
    Copy-Item -LiteralPath $sdkPolicy -Destination $checkRoot
}
foreach ($fileName in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $fileName) -Destination $sourceRoot
}
$originalCore = Join-Path $repoRoot 'src\Lokad.Sift'
Copy-Item -LiteralPath (Join-Path $originalCore 'Lokad.Sift.csproj'), (Join-Path $originalCore 'packages.lock.json') -Destination $isolatedCore
foreach ($sourceFile in Get-ChildItem -LiteralPath $originalCore -Filter '*.cs' -File -Recurse) {
    $relativePath = [System.IO.Path]::GetRelativePath($originalCore, $sourceFile.FullName)
    if ($relativePath -match '(^|[\\/])(bin|obj)[\\/]') { continue }
    $targetPath = Join-Path $isolatedCore $relativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $targetPath) -Force | Out-Null
    Copy-Item -LiteralPath $sourceFile.FullName -Destination $targetPath
}
$coreLock = Get-Content -LiteralPath (Join-Path $isolatedCore 'packages.lock.json') -Raw | ConvertFrom-Json
foreach ($dependency in $coreLock.dependencies.'net10.0'.PSObject.Properties) {
    if ($dependency.Name -match 'Utf8Regex') { throw 'Producer remains in the core lock file.' }
    $packageId = $dependency.Name.ToLowerInvariant()
    $archiveName = $packageId + '.' + $dependency.Value.resolved + '.nupkg'
    $buildArchive = Join-Path $globalPackagesRoot ($packageId + '\' + $dependency.Value.resolved + '\' + $archiveName)
    Copy-Item -LiteralPath $buildArchive -Destination $buildFeed
}
$isolatedProject = Join-Path $isolatedCore 'Lokad.Sift.csproj'
Invoke-DotNet @('restore', $isolatedProject, '--locked-mode', '--packages', $buildPackages, '--source', $buildFeed, '--tl:off', '-v', 'minimal')
Invoke-DotNet @('build', $isolatedProject, '-c', 'Release', '--no-restore', '--tl:off', '--nologo', '-v', 'minimal')

New-Item -ItemType Directory -Path $packageFeed -Force | Out-Null
if (-not $SkipPack) {
    Invoke-DotNet @('pack', (Join-Path $repoRoot 'src\Lokad.Sift\Lokad.Sift.csproj'), '-c', 'Release',
        '-o', $packageFeed, '--tl:off', '--nologo', '-v', 'minimal')
}
$packagePath = Join-Path $packageFeed ('Lokad.Sift.' + $packageVersion + '.nupkg')
$archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $nuspec = $archive.Entries | Where-Object FullName -like '*.nuspec' | Select-Object -First 1
    $reader = [System.IO.StreamReader]::new($nuspec.Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $dependencies = @($manifest.SelectNodes("//*[local-name()='dependency']"))
    if ($dependencies.Count -ne 0) { throw 'Core package has runtime package dependencies.' }
    if ($archive.Entries.FullName -match 'Utf8Regex') { throw 'Producer content entered the core package.' }
} finally { $archive.Dispose() }

$commonSource = [System.Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'RegexPackageCheck.cs'))
$adapterSource = [System.Security.SecurityElement]::Escape((Join-Path $repoRoot 'examples\Utf8RegexAdapter.cs'))
$coreFeed = Join-Path $checkRoot 'core-feed'
New-Item -ItemType Directory -Path $coreFeed -Force | Out-Null
Copy-Item -LiteralPath $packagePath -Destination $coreFeed
$projectTemplate = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <UseSharedCompilation>false</UseSharedCompilation>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Lokad.Sift" Version="PACKAGE_VERSION" />
    <Compile Include="COMMON_SOURCE" Link="RegexPackageCheck.cs" />
    EXTRA_ITEMS
  </ItemGroup>
</Project>
'@

foreach ($consumerKind in @('core', 'producer')) {
    $consumerRoot = Join-Path $checkRoot $consumerKind
    $packageCache = Join-Path $consumerRoot 'packages'
    New-Item -ItemType Directory -Path $consumerRoot -Force | Out-Null
    $projectPath = Join-Path $consumerRoot 'Consumer.csproj'
    $extraItems = ''
    $program = 'await RegexPackageCheck.Run(null); await RegexPackageCheck.Run(new PackageAsciiEngine());'
    if ($consumerKind -eq 'producer') {
        # The normal test restore supplies this immutable pinned package. Copy
        # its archive into the local feed; consumers still use fresh caches.
        $producerArchive = Join-Path $globalPackagesRoot ('lokad.utf8regex\' + $producerVersion + '\lokad.utf8regex.' + $producerVersion + '.nupkg')
        if (-not (Test-Path -LiteralPath $producerArchive)) { throw 'Restore the test project before running producer package checks.' }
        Copy-Item -LiteralPath $producerArchive -Destination $packageFeed -Force
        $extraItems = '<PackageReference Include="Lokad.Utf8Regex" Version="' + $producerVersion + '" />' +
            '<Compile Include="' + $adapterSource + '" Link="Utf8RegexAdapter.cs" />'
        $program = 'await RegexPackageCheck.Run(new Lokad.Sift.Examples.Utf8RegexAdapter());'
    }
    $projectText = $projectTemplate.Replace('PACKAGE_VERSION', $packageVersion).Replace('COMMON_SOURCE', $commonSource).Replace('EXTRA_ITEMS', $extraItems)
    [System.IO.File]::WriteAllText($projectPath, $projectText)
    [System.IO.File]::WriteAllText((Join-Path $consumerRoot 'Program.cs'), $program)
    $consumerFeed = if ($consumerKind -eq 'core') { $coreFeed } else { $packageFeed }
    $restoreArgs = @('restore', $projectPath, '--packages', $packageCache, '--source', $consumerFeed, '--tl:off', '-v', 'minimal')
    Invoke-DotNet $restoreArgs
    if ($consumerKind -eq 'core' -and (Test-Path -LiteralPath (Join-Path $packageCache 'lokad.utf8regex'))) {
        throw 'Producer was restored for the core-only consumer.'
    }
    Invoke-DotNet @('run', '--project', $projectPath, '-c', 'Release', '--no-restore', '--tl:off', '--nologo', '-v', 'minimal')
}
Write-Output 'Exact Release package and isolated consumers verified.'
}
finally {
    if ($KeepTemporaryFiles) {
        Write-Output ('Package-check temporary files retained: ' + $checkRoot)
    }
    elseif (Test-Path -LiteralPath $checkRoot) {
        $temporaryParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        $cleanupRoot = [System.IO.Path]::GetFullPath($checkRoot)
        if ([System.IO.Path]::GetDirectoryName($cleanupRoot) -ne $temporaryParent.TrimEnd([System.IO.Path]::DirectorySeparatorChar) -or
            [System.IO.Path]::GetFileName($cleanupRoot) -notmatch '^sift-package-check-[0-9a-f]{32}$') {
            throw 'Refusing to remove an unexpected package-check temporary directory.'
        }
        Remove-Item -LiteralPath $cleanupRoot -Recurse -Force
    }
}
