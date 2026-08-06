#Requires -Version 5.1
<#
.SYNOPSIS
    8장 — 인터럽트 규칙을 하나 넣고, 시나리오로 발동시켜 본다.

.DESCRIPTION
    넣는 규칙 하나

      shelter_from_storm_in_field
        when  WeatherHarsh 이고 AtField 이고, 자고 있지도 싸우고 있지도 않을 때
        then  즉시 $home 으로 이동
        replan.urgency 45

    기존 seek_shelter 는 InWilderness(야생)만 본다. 밭에서 일하던 NPC 는 비를 다 맞는다.
    그 틈을 메우는 규칙이다.

    <b>인터럽트는 LLM 이 만들지 않는다.</b> 반응 속도가 생명이라 결정론 규칙으로만 정의한다.
    그리고 cooldown_s 같은 시간 기반 억제를 쓰지 않는다 — 결정론이 깨지기 때문이다.
    대신 <b>엣지 트리거</b>다. 조건이 거짓 → 참으로 넘어가는 순간에만 한 번 걸린다.

.EXAMPLE
    ./samples/ch08_interrupt/apply.ps1
    ./samples/ch08_interrupt/apply.ps1 -Run          # 시나리오까지 돌린다
#>
[CmdletBinding()]
param(
    [string]$Lab = 'ch08',

    # 규칙을 넣지 않고 기준선만 돌린다 (비교용).
    [switch]$Baseline,

    # 적용 후 시나리오를 돌려 인터럽트 수를 센다.
    [switch]$Run,

    [int]$Npcs = 200
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$md   = Join-Path $root "lab/$Lab/masterdata"

Push-Location $root
try {
    & (Join-Path $root 'samples/lab.ps1') -New $Lab -Force | Out-Null

    if (-not $Baseline) {
        $path = Join-Path $md 'interrupts.json'
        $text = ([IO.File]::ReadAllText($path)) -replace "`r`n", "`n"

        # 규칙 배열의 맨 끝에 붙인다. 우선순위가 같으면 id 오름차순으로 깨지므로
        # 파일 안의 위치는 판정에 영향을 주지 않는다 — priority 만 본다.
        $from = "`n  ]`n}`n"
        $to = @'
,
    {
      "id": "shelter_from_storm_in_field",
      "priority": 42,
      "when": {
        "all_flag": ["WeatherHarsh", "AtField"],
        "none_flag": ["IsSleeping", "InCombat"]
      },
      "then": { "action": "MoveTo", "params": { "poi": "$home" } },
      "replan": { "urgency": 45 }
    }
  ]
}
'@ -replace "`r`n", "`n"

        if ($text.IndexOf('shelter_from_storm_in_field') -ge 0) {
            Write-Host "  이미 적용돼 있다." -ForegroundColor DarkGray
        }
        elseif ($text.IndexOf($from) -lt 0) {
            throw 'interrupts.json 에서 배열 끝을 못 찾았다.'
        }
        else {
            $at   = $text.IndexOf($from)
            $text = $text.Substring(0, $at) + $to + $text.Substring($at + $from.Length)
            [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false))
            Write-Host ""
            Write-Host "① 규칙 추가 — shelter_from_storm_in_field (priority 42)" -ForegroundColor Cyan
        }
    }
    else {
        Write-Host ""
        Write-Host "① 규칙을 넣지 않는다 (기준선)" -ForegroundColor DarkGray
    }

    # ── 검증 ──────────────────────────────────────────────────────────────
    Write-Host ""
    Write-Host "② 검증" -ForegroundColor Cyan
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & dotnet run -c Release --no-build --no-launch-profile --project src/Npc.Host -- `
        validate --masterdata $md 2>&1 | ForEach-Object { Write-Host "   $($_.ToString())" }
    $code = $LASTEXITCODE

    if ($code -ne 0) {
        $ErrorActionPreference = $prev
        Write-Host ""
        Write-Host "검증 실패 — 위 메시지를 읽는다." -ForegroundColor Red
        exit $code
    }

    if (-not $Run) {
        $ErrorActionPreference = $prev
        Write-Host ""
        Write-Host "적용 완료. 시나리오까지 돌리려면 -Run." -ForegroundColor Green
        exit 0
    }

    # ── 시나리오 ──────────────────────────────────────────────────────────
    Write-Host ""
    Write-Host "③ 폭풍 시나리오 (tick 100 Storm · 400 Fair · 700 Storm)" -ForegroundColor Cyan

    $out = & dotnet run -c Release --no-build --no-launch-profile --project src/Npc.Host -- `
        --loopback --npcs $Npcs --time-scale 600 --days 1 --no-llm --max-speed --no-dashboard `
        --masterdata $md --scenario (Join-Path $PSScriptRoot 'storm.jsonl') 2>&1 |
        ForEach-Object { $_.ToString() }
    $ErrorActionPreference = $prev

    $report = ($out | Where-Object { $_ -match '^tick p50 |^ticks \d' })
    $report | ForEach-Object { Write-Host "   $_" -ForegroundColor DarkGray }

    $text = $report -join ' '
    $interrupts = if ($text -match 'interrupts (\d+)') { [int]$Matches[1] } else { -1 }
    $replanQ    = if ($text -match 'replan-q (\d+)')   { [int]$Matches[1] } else { -1 }

    Write-Host ""
    Write-Host ("   인터럽트 발행 {0}건 · 재계획 큐 {1}" -f $interrupts, $replanQ) -ForegroundColor Green
    Write-Host "   규칙 없이 같은 회차를 돌려 견준다:" -ForegroundColor Cyan
    Write-Host "     ./samples/ch08_interrupt/apply.ps1 -Lab ch08_base -Baseline -Run -Npcs $Npcs"
}
finally {
    Pop-Location
}
