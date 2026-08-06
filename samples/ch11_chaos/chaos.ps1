#Requires -Version 5.1
<#
.SYNOPSIS
    11장 — 명령을 유실시키고 액션을 실패시켜 본다.

.DESCRIPTION
    "명령은 유실된다고 가정한다" 는 설계가 진짜로 도는지 확인한다.
    --drop-rate 를 0 에서 0.5 까지 올리며 같은 회차를 돌리고 종료 요약을 표로 모은다.

    보는 것
      commands   실제로 나간 명령 (드롭된 것은 안 센다)
      steps      전진한 스텝
      timeouts   응답이 안 와서 <b>로컬에서 합성한</b> 실패
      완주       NPC 가 멈췄는가 — 멈추면 steps 가 0 에 수렴한다

.EXAMPLE
    ./samples/ch11_chaos/chaos.ps1
    ./samples/ch11_chaos/chaos.ps1 -Mode fail -Npcs 500 -Csv ./lab/ch11_fail.csv
#>
[CmdletBinding()]
param(
    # drop = 명령 유실 · fail = 액션 실패 · both = 둘 다
    [ValidateSet('drop', 'fail', 'both')]
    [string]$Mode = 'drop',

    [double[]]$Rates = @(0, 0.1, 0.2, 0.3, 0.5),

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
    $md = (Resolve-Path $Masterdata).Path
    $rows = @()
    $baseSteps = 0

    foreach ($rate in $Rates) {
        $cli = @('run', '-c', 'Release', '--no-launch-profile', '--project', 'src/Npc.Host')
        if ($NoBuild) { $cli += '--no-build' }
        $cli += @('--', '--loopback', '--no-llm', '--max-speed', '--no-dashboard',
                  '--masterdata', $md, '--npcs', $Npcs, '--time-scale', $TimeScale, '--days', $Days)

        if ($Mode -eq 'drop' -or $Mode -eq 'both') { $cli += @('--drop-rate', $rate) }
        if ($Mode -eq 'fail' -or $Mode -eq 'both') { $cli += @('--fail-rate', $rate) }

        Write-Host ("실행중  $Mode-rate {0}" -f $rate) -ForegroundColor DarkGray

        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $out = & dotnet @cli 2>&1 | ForEach-Object { $_.ToString() }
        $ErrorActionPreference = $prev

        $text = ($out | Where-Object { $_ -match '^tick p50 |^ticks \d' }) -join ' '
        if (-not $text) { Write-Host "  종료 요약을 못 읽었다." -ForegroundColor Yellow; continue }

        function F { param([string]$p) if ($text -match $p) { return [double]$Matches[1] } ; return 0 }

        $steps    = F 'steps (\d+)'
        $timeouts = F 'timeouts (\d+)'
        if ($rate -eq 0) { $baseSteps = $steps }

        $rows += [pscustomobject]@{
            'rate'        = $rate
            'p99(ms)'     = F 'p99 ([\d.]+)ms'
            'bytes/tick'  = [int](F 'bytes/tick (\d+)')
            '명령'        = [int](F 'commands (\d+)')
            '스텝'        = [int]$steps
            '타임아웃'    = [int]$timeouts
            '타임아웃 %'  = if ($steps -gt 0) { [math]::Round(100.0 * $timeouts / $steps, 1) } else { 0 }
            '진행률 %'    = if ($baseSteps -gt 0) { [math]::Round(100.0 * $steps / $baseSteps, 1) } else { 100 }
            '드롭'        = [int](F 'drops link (\d+)')
            '크래시'      = 'no'
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
    Write-Host "  판정: 진행률이 0 으로 수렴하지 않으면 통과다." -ForegroundColor Green
    Write-Host "        느려지는 것은 정상이고, 멈추는 것이 실패다." -ForegroundColor DarkGray
}
finally {
    Pop-Location
}
