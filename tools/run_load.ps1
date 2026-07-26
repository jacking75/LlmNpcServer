<#
.SYNOPSIS
    P4 부하 매트릭스를 돌린다. docs/14 §6 · T4-15.

.DESCRIPTION
    매트릭스 정의는 tests/Npc.Tests/Load/LoadHarness.cs 에 있다. 이 스크립트는 껍데기다 —
    셀 목록을 스크립트에 두면 지난 회차와 무엇이 달랐는지 알 수 없다.

    기본은 스모크 8셀이고 1~2분이면 끝난다.
    -Full 을 주면 전량 135셀 + 페이싱 5셀을 돈다. 20분 이상 걸린다.

.PARAMETER Full
    전량 매트릭스(135 + 5셀). 오래 걸린다.

.PARAMETER Csv
    산출 경로. 기본 docs/measurements/W10_load.csv

.EXAMPLE
    ./tools/run_load.ps1
    ./tools/run_load.ps1 -Full -Csv ./docs/measurements/W10_load_full.csv
#>
[CmdletBinding()]
param(
    [switch] $Full,
    [string] $Csv
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $repo

if (-not $Csv) {
    $Csv = Join-Path $repo 'docs/measurements/W10_load.csv'
}

$env:NPC_LOAD_CSV = $Csv
$env:NPC_LOAD_MATRIX = if ($Full) { 'full' } else { 'smoke' }

Write-Host "매트릭스 : $($env:NPC_LOAD_MATRIX)"
Write-Host "산출     : $Csv"

if ($Full) {
    Write-Host ''
    Write-Host '⚠ 전량 135 + 5셀이다. 20분 이상 걸린다.' -ForegroundColor Yellow
    Write-Host ''
}

# Release 로 잰다. Debug 는 틱 지연이 몇 배 나와 P1 기준선과 비교할 수 없다.
dotnet build -c Release --nologo | Out-Null

$started = Get-Date

dotnet test -c Release --no-build --nologo `
    --filter 'FullyQualifiedName~LoadMatrixTests.Load_RunsSelectedMatrix'

$exit = $LASTEXITCODE
$elapsed = (Get-Date) - $started

Write-Host ''
Write-Host ("소요: {0:mm\:ss}" -f $elapsed)

if (Test-Path $Csv) {
    $rows = (Get-Content $Csv | Measure-Object -Line).Lines - 1
    Write-Host "셀 $rows 건을 $Csv 에 적었다."

    # 완주하지 못한 셀을 바로 보여준다 — 조용히 넘기면 리포트가 거짓이 된다.
    $failed = Import-Csv $Csv | Where-Object { $_.completed -ne '1' }

    if ($failed) {
        Write-Host ''
        Write-Host '완주하지 못한 셀:' -ForegroundColor Yellow
        $failed | Select-Object cell, wall_clock_s, ticks | Format-Table
    }
}
else {
    Write-Host "warn: $Csv 가 생기지 않았다." -ForegroundColor Yellow
}

exit $exit
