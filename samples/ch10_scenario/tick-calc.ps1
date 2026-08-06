#Requires -Version 5.1
<#
.SYNOPSIS
    10장 — 게임 시각 ↔ 틱 좌표 환산기.

.DESCRIPTION
    시나리오 jsonl 의 at_tick 은 <b>배속에 묶여 있다.</b> 같은 대본을 --time-scale 60 으로
    돌리면 아무 일도 안 일어난 것처럼 보인다. 쓰기 전에 이 표를 뽑아 둔다.

      1틱 = 0.1초 실시간 = (time_scale / 10) 게임초
      게임 하루 = 86,400 게임초 = 864,000 / time_scale 틱

    시작 시각은 06:00(Dawn)이다.

.EXAMPLE
    ./samples/ch10_scenario/tick-calc.ps1
    ./samples/ch10_scenario/tick-calc.ps1 -TimeScale 60 -At "18:00"
#>
[CmdletBinding()]
param(
    # 표로 볼 배속들.
    [int[]]$Scales = @(60, 600, 3600),

    # 이 시각이 몇 틱인지 하나만 계산한다. "HH:mm" 또는 "d1 HH:mm".
    [string]$At = '',

    # 게임 시작 시각(시).
    [int]$StartHour = 6
)

$ErrorActionPreference = 'Stop'

# 시간대 경계는 context_buckets.json 의 game_hours 다.
$bands = @(
    @{ n = 'Dawn';      from = 5;  to = 7  },
    @{ n = 'Morning';   from = 7;  to = 11 },
    @{ n = 'Noon';      from = 11; to = 14 },
    @{ n = 'Afternoon'; from = 14; to = 18 },
    @{ n = 'Evening';   from = 18; to = 22 },
    @{ n = 'Night';     from = 22; to = 5  }
)

function Get-Tick {
    param([int]$Day, [int]$Hour, [int]$Minute, [int]$TimeScale)

    # 시작 06:00 부터 흐른 게임초.
    $seconds = (($Day * 24 + $Hour - $StartHour) * 3600) + ($Minute * 60)
    if ($seconds -lt 0) { $seconds += 86400 }
    return [math]::Round($seconds * 10.0 / $TimeScale, 0)
}

if ($At) {
    $day = 0
    $t = $At.Trim()
    if ($t -match '^d(\d+)\s+(.*)$') { $day = [int]$Matches[1]; $t = $Matches[2] }
    if ($t -notmatch '^(\d{1,2}):(\d{2})$') {
        Write-Host "시각 형식이 아니다: $At   (예: 18:00 · d1 07:30)" -ForegroundColor Red
        exit 1
    }
    $h = [int]$Matches[1]; $m = [int]$Matches[2]

    Write-Host ""
    Write-Host ("게임 day {0} {1:00}:{2:00}" -f $day, $h, $m) -ForegroundColor Cyan
    foreach ($s in $Scales) {
        Write-Host ("  --time-scale {0,-5}  at_tick {1,7}   (실시간 {2}분)" -f `
            $s, (Get-Tick -Day $day -Hour $h -Minute $m -TimeScale $s), `
            [math]::Round((Get-Tick -Day $day -Hour $h -Minute $m -TimeScale $s) / 600.0, 1))
    }
    Write-Host ""
    exit 0
}

Write-Host ""
Write-Host "시간대 경계 — 게임 첫날 (시작 06:00 Dawn)" -ForegroundColor Cyan
$header = "  {0,-11} {1,-7}" -f '시간대', '시각'
foreach ($s in $Scales) { $header += ("{0,12}" -f "ts $s") }
Write-Host $header
Write-Host ("  " + ("-" * (18 + 12 * $Scales.Count)))

foreach ($b in $bands) {
    $line = "  {0,-11} {1:00}:00  " -f $b.n, $b.from
    foreach ($s in $Scales) {
        $line += ("{0,12}" -f (Get-Tick -Day 0 -Hour $b.from -Minute 0 -TimeScale $s))
    }
    Write-Host $line
}

Write-Host ""
Write-Host "  하루 = " -NoNewline
foreach ($s in $Scales) { Write-Host ("ts {0}: {1}틱   " -f $s, [math]::Round(864000.0 / $s, 0)) -NoNewline }
Write-Host ""
Write-Host ""
Write-Host "  대본을 쓸 때: 배속을 먼저 정하고, 그 열의 값을 at_tick 에 적는다." -ForegroundColor DarkGray
Write-Host "  배속을 바꾸면 대본도 같이 바꾼다 — 안 그러면 아무 일도 안 일어난다." -ForegroundColor DarkGray
Write-Host ""
