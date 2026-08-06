#Requires -Version 5.1
<#
.SYNOPSIS
    1장 — 옵션을 하나씩만 바꿔 보는 대조 실험 8회.

.DESCRIPTION
    같은 기준선에서 옵션 하나만 바꿔 돌리고 종료 요약을 표로 모은다.
    전부 --max-speed 라 한 회차가 몇 초면 끝난다 (10Hz 페이싱을 안 한다).

    "옵션 설명을 읽는 것" 과 "바꿔 보고 숫자가 어떻게 움직이는지 보는 것" 은
    다른 일이다. 이 스크립트는 후자를 위한 것이다.

.EXAMPLE
    ./samples/ch01_first_run/variants.ps1
    ./samples/ch01_first_run/variants.ps1 -Csv ./lab/ch01_variants.csv
#>
[CmdletBinding()]
param(
    # 결과를 CSV 로도 남긴다.
    [string]$Csv = '',

    # 오래 걸리는 회차(NPC 5,000 · 게임 7일)를 건너뛴다.
    [switch]$Quick,

    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $root

# 기준선. 아래 회차들은 여기서 딱 하나씩만 바꾼다.
$base = @{
    npcs        = 50
    timeScale   = 600
    days        = 1
    playerBots  = 20
    dropRate    = 0
    failRate    = 0
}

$variants = @(
    @{ name = '기준선';                  note = 'NPC 50 · 배속 600 · 1일';        change = @{} },
    @{ name = '--npcs 500';              note = 'NPC 10배';                        change = @{ npcs = 500 } },
    @{ name = '--npcs 5000';             note = 'NPC 100배 (부하 목표치)';         change = @{ npcs = 5000 }; slow = $true },
    @{ name = '--time-scale 60';         note = '배속 1/10 (틱은 10배)';           change = @{ timeScale = 60 } },
    @{ name = '--days 7';                note = '게임 일주일';                     change = @{ days = 7 }; slow = $true },
    @{ name = '--player-bots 0';         note = '플레이어가 하나도 없다';          change = @{ playerBots = 0 } },
    @{ name = '--drop-rate 0.2';         note = '명령 5건 중 1건 유실';            change = @{ dropRate = 0.2 } },
    @{ name = '--fail-rate 0.2';         note = '액션 5건 중 1건 실패';            change = @{ failRate = 0.2 } }
)

function Invoke-Run {
    param([hashtable]$Opt)

    $cli = @('run', '-c', 'Release', '--no-launch-profile', '--project', 'src/Npc.Host')
    if ($NoBuild) { $cli += '--no-build' }
    $cli += @(
        '--',
        '--loopback', '--no-llm', '--max-speed', '--no-dashboard',
        '--npcs', $Opt.npcs,
        '--time-scale', $Opt.timeScale,
        '--days', $Opt.days,
        '--player-bots', $Opt.playerBots,
        '--drop-rate', $Opt.dropRate,
        '--fail-rate', $Opt.failRate
    )

    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & dotnet @cli 2>&1 | ForEach-Object { $_.ToString() }
    }
    finally {
        $ErrorActionPreference = $prev
    }

    return (($out | Where-Object { $_ -match '^tick p50 |^ticks \d' }) -join ' ')
}

function Get-Field {
    param([string]$Text, [string]$Pattern)
    if ($Text -match $Pattern) { return $Matches[1] }
    return ''
}

try {
    $rows = @()

    foreach ($v in $variants) {
        if ($Quick -and $v.slow) {
            Write-Host ("건너뜀  {0}" -f $v.name) -ForegroundColor DarkGray
            continue
        }

        $opt = @{} + $base
        foreach ($k in $v.change.Keys) { $opt[$k] = $v.change[$k] }

        Write-Host ("실행중  {0,-20} {1}" -f $v.name, $v.note) -ForegroundColor DarkGray
        $started = Get-Date
        $text    = Invoke-Run -Opt $opt
        $seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)

        if (-not $text) {
            Write-Host "  종료 요약을 못 읽었다 — 건너뛴다." -ForegroundColor Yellow
            continue
        }

        $rows += [pscustomobject]@{
            '회차'        = $v.name
            '틱'          = Get-Field $text 'ticks (\d+)'
            'p99(ms)'     = Get-Field $text 'p99 ([\d.]+)ms'
            'bytes/tick'  = Get-Field $text 'bytes/tick (\d+)'
            '명령'        = Get-Field $text 'commands (\d+)'
            '스텝'        = Get-Field $text 'steps (\d+)'
            '타임아웃'    = Get-Field $text 'timeouts (\d+)'
            '스캔/틱'     = Get-Field $text 'scan/tick (\d+)'
            '인터럽트'    = Get-Field $text 'interrupts (\d+)'
            'LLM'         = Get-Field $text 'llm (\d+)'
            '실시간(s)'   = $seconds
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
}
finally {
    Pop-Location
}
