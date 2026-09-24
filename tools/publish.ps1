# .NET 10 SDK와 Python 3.9+가 필요하다. Python은 zip의 Unix 경로와 실행 권한을 기록한다.
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'linux-x64', 'osx-arm64')]
    [string]$Rid
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$distRoot = Join-Path $repoRoot 'dist'
$version = ([xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.VersionPrefix |
    Where-Object { $_ } | Select-Object -Last 1
if (-not $version) { throw 'Directory.Build.props에 VersionPrefix가 없다.' }
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'LICENSE') -PathType Leaf)) {
    throw 'LICENSE가 없다. 배포 zip의 사용 조건을 먼저 확정해야 한다.'
}

New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
$stage = Join-Path $distRoot ('.staging-' + [guid]::NewGuid().ToString('N'))
$stageFull = [IO.Path]::GetFullPath($stage)
$distFull = [IO.Path]::GetFullPath($distRoot)
if (-not $stageFull.StartsWith($distFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "배포 임시 폴더가 dist 밖이다: $stageFull"
}
$package = Join-Path $stage 'npc-server'
New-Item -ItemType Directory -Path $package -Force | Out-Null

try {
    $apps = @(
        @{ Project = 'src/Npc.Host'; Folder = 'host'; Source = 'Npc.Host'; Target = 'npc-server' },
        @{ Project = 'tools/Npc.Cli'; Folder = 'cli'; Source = 'npc'; Target = 'npc' },
        @{ Project = 'tools/Npc.Studio'; Folder = 'studio'; Source = 'Npc.Studio'; Target = 'npc-studio' },
        @{ Project = 'tools/Npc.Conformance'; Folder = 'conformance'; Source = 'Npc.Conformance'; Target = 'npc-conformance' },
        @{ Project = 'testbed/Npc.TestGameServer'; Folder = 'test-game-server'; Source = 'Npc.TestGameServer'; Target = 'npc-test-gs' }
    )
    $extension = if ($Rid.StartsWith('win-')) { '.exe' } else { '' }
    foreach ($app in $apps) {
        $output = Join-Path $package (Join-Path 'bin' $app.Folder)
        dotnet publish (Join-Path $repoRoot $app.Project) -c Release -r $Rid --self-contained true `
            -p:PublishSingleFile=true -p:PublishTrimmed=false -o $output
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish 실패: $($app.Project) $Rid" }
        $sourceExe = Join-Path $output ($app.Source + $extension)
        if (-not (Test-Path -LiteralPath $sourceExe)) { throw "실행 파일이 없다: $sourceExe" }
        $targetExe = Join-Path $output ($app.Target + $extension)
        if ($sourceExe -ne $targetExe) { Move-Item -LiteralPath $sourceExe -Destination $targetExe }
    }

    Copy-Item -LiteralPath (Join-Path $repoRoot 'masterdata') -Destination $package -Recurse
    Copy-Item -LiteralPath (Join-Path $repoRoot 'appsettings.Llm.json') -Destination $package
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $package
    if (Test-Path -LiteralPath (Join-Path $repoRoot 'scenarios')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'scenarios') -Destination $package -Recurse
    }
    $planTarget = Join-Path $package 'planstore'
    New-Item -ItemType Directory -Path $planTarget -Force | Out-Null
    foreach ($name in @('pinned', 'manifest.json')) {
        $source = Join-Path (Join-Path $repoRoot 'planstore') $name
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $planTarget -Recurse }
    }
    $demoMarker = Join-Path $repoRoot 'planstore/demo-pack.txt'
    if (Test-Path -LiteralPath $demoMarker -PathType Leaf) {
        $demoPrefix = (Get-Content -LiteralPath $demoMarker -Raw).Trim()
        if ($demoPrefix -cnotmatch '^[0-9a-f]{8}$') { throw 'demo-pack.txt의 프리픽스가 잘못됐다.' }
        $demoSource = Join-Path (Join-Path $repoRoot 'planstore') $demoPrefix
        if (-not (Test-Path -LiteralPath (Join-Path $demoSource 'plans') -PathType Container)) {
            throw "데모 플랜 폴더가 없다: $demoSource"
        }
        $demoTarget = Join-Path $planTarget $demoPrefix
        New-Item -ItemType Directory -Path $demoTarget -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $demoSource 'plans') -Destination $demoTarget -Recurse
        $demoManifest = Join-Path $demoSource 'manifest.json'
        if (Test-Path -LiteralPath $demoManifest -PathType Leaf) {
            Copy-Item -LiteralPath $demoManifest -Destination $demoTarget
        }
        Copy-Item -LiteralPath $demoMarker -Destination $planTarget
    }
    $sampleTarget = Join-Path $package 'samples'
    New-Item -ItemType Directory -Path $sampleTarget -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'samples/worlds') -Destination $sampleTarget -Recurse
    Copy-Item -LiteralPath (Join-Path $repoRoot 'samples/python_gs') -Destination $sampleTarget -Recurse
    Copy-Item -LiteralPath (Join-Path $repoRoot 'QUICKSTART.md') -Destination $package
    $securityTarget = Join-Path $package 'docs/security'
    New-Item -ItemType Directory -Path $securityTarget -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/security/secrets.md') -Destination $securityTarget
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/host_options.md') -Destination (Join-Path $package 'docs')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/ADOPTION.md') -Destination (Join-Path $package 'docs')

    $zip = Join-Path $distRoot "npc-server-$version-$Rid.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
        throw '휴대 가능한 zip 생성에 Python 3가 필요하다.'
    }
    python (Join-Path $repoRoot 'tools/package_zip.py') $package $zip
    if ($LASTEXITCODE -ne 0) { throw 'zip 생성에 실패했다.' }
}
finally {
    if (Test-Path -LiteralPath $stageFull) {
        Remove-Item -LiteralPath $stageFull -Recurse -Force
    }
}
