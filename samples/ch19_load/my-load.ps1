#Requires -Version 5.1
<#
.SYNOPSIS
    19장 — 내 마스터데이터로 부하를 걸고 기준선과 견준다.

.DESCRIPTION
    tools/run_load.ps1 은 docs/measurements/W10_load.csv 를 <b>덮어쓴다</b>.
    실습에서 그 파일을 건드리면 저장소의 실측 원자료가 오염된다. 그래서 따로 둔다 —
    이 스크립트는 lab/ 에만 쓴다.

    NPC 수를 올려 가며 돌리고 수용 기준 넷을 판정한다.

      틱 p99        ≤ 20 ms
      틱당 할당     = 0 B          <b>계기는 이것 하나다</b>
      예산 초과 틱  = 0
      인지 스캔     ≤ 150 / 틱

.EXAMPLE
    ./samples/ch19_load/my-load.ps1
    ./samples/ch19_load/my-load.ps1 -Masterdata ./lab/ch05/masterdata -Csv ./lab/ch19_mine.csv
#>
[CmdletBinding()]
param(
    [int[]]$Npcs = @(500, 1000, 2000, 5000),
    [int]$TimeScale = 600,
    [int]$Days = 1,
    [string]$Masterdata = './masterdata',
    [string]$Csv = './lab/ch19_load.csv',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $root

try {
    $md = (Resolve-Path $Masterdata).Path
    $rows = @()

    foreach ($n in $Npcs) {
        $cli = @('run', '-c', 'Release', '--no-launch-profile', '--project', 'src/Npc.Host')
        if ($NoBuild) { $cli += '--no-build' }
        $cli += @('--', '--loopback', '--no-llm', '--max-speed', '--no-dashboard',
                  '--masterdata', $md, '--npcs', $n, '--time-scale', $TimeScale, '--days', $Days)

        Write-Host ("실행중  npcs {0}" -f $n) -ForegroundColor DarkGray
        $started = Get-Date

        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $out = & dotnet @cli 2>&1 | ForEach-Object { $_.ToString() }
        $ErrorActionPreference = $prev

        $seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
        $text = ($out | Where-Object { $_ -match '^tick p50 |^ticks \d' }) -join ' '
        if (-not $text) { Write-Host "  종료 요약을 못 읽었다." -ForegroundColor Yellow; continue }

        function F { param([string]$p) if ($text -match $p) { return [double]$Matches[1] } ; return -1 }

        $p99   = F 'p99 ([\d.]+)ms'
        $bytes = F 'bytes/tick (\d+)'
        $over  = F 'overruns (\d+)'
        $scan  = F 'scan/tick (\d+)'

        $rows += [pscustomobject]@{
            'NPC'        = $n
            'p50(ms)'    = F 'p50 ([\d.]+)ms'
            'p99(ms)'    = $p99
            'max(ms)'    = F 'max ([\d.]+)ms'
            'bytes/tick' = [int]$bytes
            '초과틱'     = [int]$over
            '스캔/틱'    = [int]$scan
            '명령'       = [int](F 'commands (\d+)')
            '힙(MB)'     = F 'heap ([\d.]+)MB'
            '실시간(s)'  = $seconds
            '판정'       = $(if ($p99 -le 20 -and $bytes -eq 0 -and $over -eq 0 -and $scan -le 150) { 'OK' } else { 'FAIL' })
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

    $fail = @($rows | Where-Object { $_.판정 -ne 'OK' })
    Write-Host ""
    if ($fail.Count -eq 0) {
        Write-Host "수용 기준 넷을 전부 넘겼다." -ForegroundColor Green
    }
    else {
        Write-Host "$($fail.Count) 회차가 기준을 못 넘겼다." -ForegroundColor Red
    }
    Write-Host ""
    Write-Host "  bytes/tick 이 0 이 아니면 그 자체가 회귀다." -ForegroundColor DarkGray
    Write-Host "  gen0 로 재지 않는 이유: 그 값은 프로세스 전역이라 HTTP·직렬화가 전부 섞인다." -ForegroundColor DarkGray
}
finally {
    Pop-Location
}
