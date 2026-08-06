#Requires -Version 5.1
<#
.SYNOPSIS
    실습장(lab) 관리 — 마스터데이터 사본을 뜨고, 비교하고, 지운다.

.DESCRIPTION
    활용 실습서 0장. 이 책의 모든 마스터데이터 실습은 원본 masterdata/ 가 아니라
    lab/<이름>/masterdata 사본에서 한다. 원본은 21개 장 내내 한 글자도 바뀌지 않는다.

    실습이 끝나면 -Drop 한 번으로 흔적 없이 사라진다. lab/ 은 .gitignore 대상이다.

.EXAMPLE
    ./samples/lab.ps1 -New ch05
    ./samples/lab.ps1 -Diff ch05
    ./samples/lab.ps1 -Drop ch05
    ./samples/lab.ps1 -List
#>
[CmdletBinding(DefaultParameterSetName = 'List')]
param(
    # 실습장을 새로 만든다. 원본 masterdata/ 를 통째로 복사한다.
    [Parameter(ParameterSetName = 'New', Mandatory = $true, Position = 0)]
    [string]$New,

    # 원본과 무엇이 다른지 본다. 내가 뭘 고쳤는지 잊었을 때 쓴다.
    [Parameter(ParameterSetName = 'Diff', Mandatory = $true, Position = 0)]
    [string]$Diff,

    # 실습장을 지운다.
    [Parameter(ParameterSetName = 'Drop', Mandatory = $true, Position = 0)]
    [string]$Drop,

    # 실습장 목록.
    [Parameter(ParameterSetName = 'List')]
    [switch]$List,

    # 이미 있는 실습장을 덮어쓴다.
    [Parameter(ParameterSetName = 'New')]
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$source  = Join-Path $root 'masterdata'
$labRoot = Join-Path $root 'lab'

if (-not (Test-Path (Join-Path $source 'archetypes.json'))) {
    Write-Host "원본 마스터데이터를 찾지 못했다: $source" -ForegroundColor Red
    Write-Host "이 스크립트는 저장소 안의 samples/ 에서 실행해야 한다." -ForegroundColor Red
    exit 1
}

# 상대 경로 -> SHA256 표. 두 폴더를 견줄 때 쓴다.
function Get-HashMap {
    param([string]$Dir)

    $map = @{}
    if (-not (Test-Path $Dir)) { return $map }

    $prefix = (Resolve-Path $Dir).Path.TrimEnd('\') + '\'
    foreach ($f in Get-ChildItem -Path $Dir -Recurse -File) {
        $rel = $f.FullName.Substring($prefix.Length)
        $map[$rel] = (Get-FileHash -Path $f.FullName -Algorithm SHA256).Hash
    }
    return $map
}

switch ($PSCmdlet.ParameterSetName) {

    'New' {
        $target = Join-Path $labRoot $New
        $dest   = Join-Path $target 'masterdata'

        if (Test-Path $dest) {
            if (-not $Force) {
                Write-Host "이미 있다: $dest" -ForegroundColor Yellow
                Write-Host "덮어쓰려면 -Force, 지우려면 -Drop $New" -ForegroundColor Yellow
                exit 1
            }
            Remove-Item -Path $dest -Recurse -Force
        }

        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Copy-Item -Path $source -Destination $dest -Recurse

        # 실습장을 "작은 저장소 루트" 로 만든다.
        #
        # tools/gen_npcs.cs · gen_poi_distances.cs 는 --masterdata 인자를 받지 않는다.
        # NpcServer.sln 이 있는 폴더를 위로 찾아 올라가 그 아래 masterdata/ 를 읽는다.
        # 여기에 같은 이름의 빈 파일을 두면, 실습장 안에서 실행했을 때 생성기가
        # 원본이 아니라 이 사본을 고친다. 5·7장이 이것에 기댄다.
        $marker = Join-Path $target 'NpcServer.sln'
        if (-not (Test-Path $marker)) {
            Set-Content -Path $marker -Value '# 실습장 표식. tools/*.cs 가 저장소 루트를 여기로 본다.' -Encoding UTF8
        }

        $files = Get-ChildItem -Path $dest -Recurse -File
        $kb    = [math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1KB, 1)

        Write-Host ""
        Write-Host "실습장 생성" -ForegroundColor Green
        Write-Host "  경로   $dest"
        Write-Host "  파일   $($files.Count)개 · $kb KB"
        Write-Host "  표식   NpcServer.sln (생성기가 이 사본을 보게 한다)"
        Write-Host ""
        Write-Host "  다음 한 줄로 이 사본이 멀쩡한지 먼저 확인한다:" -ForegroundColor Cyan
        Write-Host "    dotnet run -c Release --project src/Npc.Host -- validate --masterdata ./lab/$New/masterdata"
        Write-Host ""
    }

    'Diff' {
        $dest = Join-Path (Join-Path $labRoot $Diff) 'masterdata'

        if (-not (Test-Path $dest)) {
            Write-Host "그런 실습장이 없다: $dest" -ForegroundColor Red
            exit 1
        }

        $a = Get-HashMap -Dir $source
        $b = Get-HashMap -Dir $dest

        $changed = @($a.Keys | Where-Object { $b.ContainsKey($_) -and $b[$_] -ne $a[$_] } | Sort-Object)
        $added   = @($b.Keys | Where-Object { -not $a.ContainsKey($_) } | Sort-Object)
        $removed = @($a.Keys | Where-Object { -not $b.ContainsKey($_) } | Sort-Object)

        Write-Host ""
        Write-Host "lab/$Diff/masterdata  vs  masterdata/" -ForegroundColor Cyan

        if ($changed.Count -eq 0 -and $added.Count -eq 0 -and $removed.Count -eq 0) {
            Write-Host "  차이 없음 — 아직 아무것도 안 고쳤다." -ForegroundColor DarkGray
        }
        foreach ($f in $changed) { Write-Host "  ~ 수정  $f" -ForegroundColor Yellow }
        foreach ($f in $added)   { Write-Host "  + 추가  $f" -ForegroundColor Green }
        foreach ($f in $removed) { Write-Host "  - 삭제  $f" -ForegroundColor Red }
        Write-Host ""
    }

    'Drop' {
        $target = Join-Path $labRoot $Drop

        if (-not (Test-Path $target)) {
            Write-Host "그런 실습장이 없다: $target" -ForegroundColor Yellow
            exit 0
        }

        Remove-Item -Path $target -Recurse -Force
        Write-Host "지웠다: lab/$Drop" -ForegroundColor Green
    }

    default {
        Write-Host ""
        if (-not (Test-Path $labRoot)) {
            Write-Host "실습장이 하나도 없다. ./samples/lab.ps1 -New <이름> 으로 만든다." -ForegroundColor DarkGray
            Write-Host ""
            exit 0
        }

        $dirs = @(Get-ChildItem -Path $labRoot -Directory | Sort-Object Name)
        if ($dirs.Count -eq 0) {
            Write-Host "실습장이 하나도 없다. ./samples/lab.ps1 -New <이름> 으로 만든다." -ForegroundColor DarkGray
            Write-Host ""
            exit 0
        }

        Write-Host "실습장 $($dirs.Count)개" -ForegroundColor Cyan
        foreach ($d in $dirs) {
            $md = Join-Path $d.FullName 'masterdata'
            $n  = 0
            if (Test-Path $md) { $n = @(Get-ChildItem -Path $md -Recurse -File).Count }
            Write-Host ("  {0,-16} 파일 {1,3}개   {2}" -f $d.Name, $n, $d.LastWriteTime.ToString('yyyy-MM-dd HH:mm'))
        }
        Write-Host ""
    }
}
