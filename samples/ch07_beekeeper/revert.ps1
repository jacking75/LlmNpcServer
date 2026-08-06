#Requires -Version 5.1
<#
.SYNOPSIS
    7장 — beekeeper 실습을 되돌린다.

.DESCRIPTION
    apply.ps1 이 고친 곳은 여섯이고, 그중 셋(npc_instances.json · poi_distances.bin ·
    빌드 산출물)은 <b>생성물</b>이라 손으로 되돌릴 수 없다. 그래서 git 으로 되돌린다.

      git restore masterdata src        추적 중인 파일을 HEAD 로 되돌린다
      dotnet build -c Release           41로 컴파일된 바이너리를 40으로 되돌린다

    실습 중에 다른 것도 고쳤다면 이 스크립트가 그것까지 날린다.
    <b>전용 브랜치에서 실습하라</b>고 하는 이유다.

.EXAMPLE
    ./samples/ch07_beekeeper/revert.ps1
#>
[CmdletBinding()]
param(
    # 확인 없이 되돌린다.
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

Push-Location $root
try {
    $dirty = @(& git status --porcelain -- masterdata src | Where-Object { $_ })

    if ($dirty.Count -eq 0) {
        Write-Host "masterdata/ 와 src/ 에 되돌릴 변경이 없다." -ForegroundColor DarkGray
        exit 0
    }

    Write-Host ""
    Write-Host "되돌릴 파일 $($dirty.Count)개" -ForegroundColor Cyan
    $dirty | ForEach-Object { Write-Host "   $_" -ForegroundColor DarkGray }

    if (-not $Yes) {
        Write-Host ""
        Write-Host "정말 되돌리려면 -Yes 를 붙여 다시 실행한다." -ForegroundColor Yellow
        Write-Host "   ./samples/ch07_beekeeper/revert.ps1 -Yes"
        exit 1
    }

    & git restore masterdata src
    Write-Host ""
    Write-Host "파일 복원 완료. 이제 다시 빌드한다 (ArchetypeCount 가 40으로 돌아간다)." -ForegroundColor Green

    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & dotnet build -c Release 2>&1 | Select-Object -Last 4 | ForEach-Object { Write-Host "   $($_.ToString())" -ForegroundColor DarkGray }
    $ErrorActionPreference = $prev

    Write-Host ""
    Write-Host "되돌리기 완료." -ForegroundColor Green
}
finally {
    Pop-Location
}
