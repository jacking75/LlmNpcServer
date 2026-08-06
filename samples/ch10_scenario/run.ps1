#Requires -Version 5.1
<#
.SYNOPSIS
    10장 — 대본을 돌리고 기준선과 견준다.

.DESCRIPTION
    같은 시드·같은 마스터데이터로 대본만 갈아 끼워 돌린 뒤 종료 요약을 표로 모은다.
    <b>대본이 무엇을 얼마나 흔들었는지</b>가 숫자로 나온다.

    돌리는 회차
      기준선     대본 없음
      weather    기후 Fair → Cold → Storm → Fair
      plague     지역 상태가 존을 타고 Alert → Disaster → 회복
      siege      저장소에 원래 있는 공성 대본 (scenarios/siege.jsonl)

.EXAMPLE
    ./samples/ch10_scenario/run.ps1
    ./samples/ch10_scenario/run.ps1 -Npcs 500 -Csv ./lab/ch10.csv
#>
[CmdletBinding()]
param(
    [int]$Npcs = 200,
    [int]$TimeScale = 600,
    [int]$Days = 1,
    [string]$Masterdata = './masterdata',
    [string]$Csv = '',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $root

try {
    if (-not (Test-Path $Masterdata)) { throw "그런 마스터데이터 폴더가 없다: $Masterdata" }
    $md = (Resolve-Path $Masterdata).Path

    $runs = @(
        @{ name = '기준선';  file = '' },
        @{ name = 'weather'; file = (Join-Path $PSScriptRoot 'weather.jsonl') },
        @{ name = 'plague';  file = (Join-Path $PSScriptRoot 'plague.jsonl') },
        @{ name = 'siege';   file = (Join-Path $root 'scenarios/siege.jsonl') }
    )

    $rows = @()

    foreach ($r in $runs) {
        if ($r.file -and -not (Test-Path $r.file)) {
            Write-Host ("건너뜀  {0} — 파일이 없다: {1}" -f $r.name, $r.file) -ForegroundColor Yellow
            continue
        }

        $cli = @('run', '-c', 'Release', '--no-launch-profile', '--project', 'src/Npc.Host')
        if ($NoBuild) { $cli += '--no-build' }
        $cli += @('--', '--loopback', '--no-llm', '--max-speed', '--no-dashboard',
                  '--masterdata', $md, '--npcs', $Npcs, '--time-scale', $TimeScale, '--days', $Days)
        if ($r.file) { $cli += @('--scenario', $r.file) }

        Write-Host ("실행중  {0}" -f $r.name) -ForegroundColor DarkGray

        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $out = & dotnet @cli 2>&1 | ForEach-Object { $_.ToString() }
        $ErrorActionPreference = $prev

        $text = ($out | Where-Object { $_ -match '^tick p50 |^ticks \d' }) -join ' '
        if (-not $text) {
            Write-Host "  종료 요약을 못 읽었다." -ForegroundColor Yellow
            continue
        }

        function F { param([string]$p) if ($text -match $p) { return $Matches[1] } ; return '' }

        $rows += [pscustomobject]@{
            '회차'       = $r.name
            'p99(ms)'    = F 'p99 ([\d.]+)ms'
            'max(ms)'    = F 'max ([\d.]+)ms'
            'bytes/tick' = F 'bytes/tick (\d+)'
            '이벤트'     = F 'events (\d+)'
            '명령'       = F 'commands (\d+)'
            '스텝'       = F 'steps (\d+)'
            '인터럽트'   = F 'interrupts (\d+)'
            '재계획큐'   = F 'replan-q (\d+)'
            '이월'       = F 'backlogs (\d+)'
        }
    }

    Write-Host ""
    $rows | Format-Table -AutoSize

    if ($Csv) {
        $dir = Split-Path -Parent $Csv
        if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $rows | Export-Csv -Path $Csv -NoTypeInformation -Encoding UTF8
        Write-Host "CSV 저장: $Csv" -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "  볼 것: 대본이 있어도 bytes/tick 은 0 이고 이월은 0 이다." -ForegroundColor DarkGray
    Write-Host "         대량 전환을 여러 틱에 나눠 처리하기 때문이다." -ForegroundColor DarkGray
}
finally {
    Pop-Location
}
