#Requires -Version 5.1
<#
.SYNOPSIS
    6장 — 손으로 쓴 폴백 플랜을 마스터데이터에 얹는다.

.DESCRIPTION
    fallback_plans.json 안의 fb_shepherd 블록을 통째로 갈아 끼운다.
    -Variant 를 주면 일부러 틀린 판을 얹어 무엇이 잡히는지 본다.

      (없음)        fb_shepherd_v2.json — 10스텝짜리 정상 플랜
      forbidden     shepherd 에게 허용되지 않은 Mine 을 넣는다        → V8 이 잡는다
      loop          loop 를 false 로 둔다                            → V7 이 잡는다
      toolong       Rest 를 하나 더 넣어 11스텝으로 만든다            → V1.STEP_COUNT 가 잡는다
      precondition  MoveTo 없이 바로 Gather 한다                     → 검증은 통과한다

    마지막 판이 이 장의 요지다. <b>정적 검증은 "갈 수 있는가" 를 안 본다.</b>
    통과한 뒤 런타임에서 스텝이 실패하고 건너뛰어진다 — 2장의 추적기로 그것을 본다.

.EXAMPLE
    ./samples/ch06_plan/apply.ps1
    ./samples/ch06_plan/apply.ps1 -Variant precondition -Lab ch06_pre
#>
[CmdletBinding()]
param(
    [string]$Lab = 'ch06',

    [ValidateSet('', 'forbidden', 'loop', 'toolong', 'precondition')]
    [string]$Variant = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$md   = Join-Path $root "lab/$Lab/masterdata"

Push-Location $root
try {
    & (Join-Path $root 'samples/lab.ps1') -New $Lab -Force | Out-Null

    # ── 얹을 플랜을 만든다 ────────────────────────────────────────────────
    $planPath = Join-Path $PSScriptRoot 'fb_shepherd_v2.json'
    $plan     = ([IO.File]::ReadAllText($planPath)) -replace "`r`n", "`n"

    # 주석은 문서용이다. 마스터데이터에 그대로 넣어도 로더는 무시하지만,
    # 무엇이 실제 스키마인지 흐려지므로 얹기 전에 걷어낸다.
    $plan = $plan -replace '(?s)\s*"_comment":\s*\[.*?\],\s*\n', "`n"

    switch ($Variant) {
        'forbidden' {
            # shepherd.allowed_actions 에 Mine 이 없다.
            $plan = $plan -replace '"action": "Gather",(\s*)"args": \{ "resource": "wool"', '"action": "Mine",$1"args": { "resource": "wool"'
        }
        'loop' {
            $plan = $plan -replace '"loop": true', '"loop": false'
        }
        'toolong' {
            # 스텝 상한은 10이다. Rest 를 하나 끼워 11로 만든다.
            $plan = $plan -replace '(\{\s*"action": "Eat",\s*"args": \{\},\s*"timeout_s": 300\s*\},)',
                                   "`$1`n    {`n      `"action`": `"Rest`",`n      `"args`": { `"duration_s`": 600 },`n      `"timeout_s`": 1200`n    },"
        }
        'precondition' {
            # 첫 스텝(밭으로 이동)을 통째로 지운다. 그러면 AtField 없이 Gather 한다.
            $plan = $plan -replace '(?s)\{\s*"action": "MoveTo",\s*"args": \{ "poi": "\$nearest_field" \},\s*"timeout_s": 600\s*\},\s*', ''
        }
    }

    # 들여쓰기를 fallback_plans.json 의 배열 원소 수준(4칸)으로 맞춘다.
    $plan = (($plan -split "`n") | ForEach-Object { if ($_.Trim()) { "    $_" } else { $_ } }) -join "`n"
    $plan = $plan.TrimEnd("`n")

    # ── fb_shepherd 블록을 찾아 갈아 끼운다 ───────────────────────────────
    $path = Join-Path $md 'fallback_plans.json'
    $text = ([IO.File]::ReadAllText($path)) -replace "`r`n", "`n"

    $marker = '"id": "fb_shepherd"'
    $at = $text.IndexOf($marker)
    if ($at -lt 0) { throw 'fallback_plans.json 에서 fb_shepherd 를 못 찾았다.' }

    # 이 블록의 여는 중괄호와 닫는 중괄호를 찾는다.
    $open = $text.LastIndexOf("    {`n", $at)
    if ($open -lt 0) { throw '블록의 시작을 못 찾았다.' }

    $close = $text.IndexOf("`n    }", $at)
    if ($close -lt 0) { throw '블록의 끝을 못 찾았다.' }
    $closeEnd = $close + "`n    }".Length

    $text = $text.Substring(0, $open) + $plan + $text.Substring($closeEnd)
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false))

    $label = if ($Variant) { "fb_shepherd_v2 ($Variant 판)" } else { 'fb_shepherd_v2 (정상)' }
    Write-Host ""
    Write-Host "① 플랜 교체 — $label" -ForegroundColor Cyan
    Write-Host "   lab/$Lab/masterdata/fallback_plans.json"

    # ── 검증 ──────────────────────────────────────────────────────────────
    Write-Host ""
    Write-Host "② 검증" -ForegroundColor Cyan
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & dotnet run -c Release --no-build --no-launch-profile --project src/Npc.Host -- `
        validate --masterdata $md 2>&1 | ForEach-Object { Write-Host "   $($_.ToString())" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev

    Write-Host ""
    if ($code -eq 0) {
        Write-Host "검증 통과 — 이제 돌려서 눈으로 본다:" -ForegroundColor Green
        Write-Host "   dotnet run -c Release --project src/Npc.Host -- ``"
        Write-Host "       --loopback --npcs 20 --time-scale 60 --days 1 --no-llm --port 5080 ``"
        Write-Host "       --masterdata $md"
        Write-Host "   ./samples/ch02_watch/watch-npc.ps1 -Npc 7 -Seconds 60"
    }
    else {
        Write-Host "검증 실패 — 위 메시지가 무엇을 가리키는지 읽는다." -ForegroundColor Yellow
    }
    exit $code
}
finally {
    Pop-Location
}
