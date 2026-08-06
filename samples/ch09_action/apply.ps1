#Requires -Version 5.1
<#
.SYNOPSIS
    9장 — 액션 카탈로그에 Tend(code 38)를 추가한다. 2부에서 파급이 가장 큰 변경.

.DESCRIPTION
    액션 하나를 넣으면 세 곳이 동시에 흔들린다.

      ① actions.json          카탈로그가 37 → 38
      ② archetypes.json       그 액션을 쓸 아키타입의 allowed_actions
      ③ fallback_plans.json   실제로 써 봐야 명령이 나가는지 확인된다

    그리고 <b>프롬프트 프리픽스가 바뀐다.</b> 액션 카탈로그는 손으로 쓴 파일이 아니라
    actions.json 에서 CatalogRenderer 가 <b>생성</b>하는 것이라, 액션을 넣는 순간
    프리픽스 SHA 가 달라지고 <b>미리 구워 둔 플랜이 전량 무효</b>가 된다.

    이 스크립트는 그 해시 변화를 전후로 찍어 보여 준다.

.EXAMPLE
    ./samples/ch09_action/apply.ps1
#>
[CmdletBinding()]
param(
    [string]$Lab = 'ch09'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$md   = Join-Path $root "lab/$Lab/masterdata"

Push-Location $root
try {
    & (Join-Path $root 'samples/lab.ps1') -New $Lab -Force | Out-Null

    function Show-Prefix {
        param([string]$Title)

        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $out = & dotnet run -c Release --no-build --project tools/Npc.Prebake -- `
            --print-prefix --masterdata $md 2>&1 | ForEach-Object { $_.ToString() }
        $ErrorActionPreference = $prev

        $full = ($out | Where-Object { $_ -match '^--- Full:' } | Select-Object -First 1)
        $cat  = ($out | Where-Object { $_ -match 'action_catalog' } | Select-Object -First 1)

        Write-Host "   $Title" -ForegroundColor DarkGray
        Write-Host "     $($full.Trim())"
        Write-Host "     $($cat.Trim())"

        if ($full -match 'Full: (\d+) tok · sha (\w+)') {
            return [pscustomobject]@{ Tokens = [int]$Matches[1]; Sha = $Matches[2] }
        }
        return $null
    }

    Write-Host ""
    Write-Host "① 고치기 전 프리픽스" -ForegroundColor Cyan
    $before = Show-Prefix -Title '(변경 전)'

    function Edit-Json {
        param([string]$File, [string]$From, [string]$To, [string]$Label)

        $path = Join-Path $md $File
        $text = ([IO.File]::ReadAllText($path)) -replace "`r`n", "`n"
        if ($text.IndexOf($From) -lt 0) { throw "$File 에서 앵커를 못 찾았다 ($Label)." }

        $at   = $text.IndexOf($From)
        $text = $text.Substring(0, $at) + $To + $text.Substring($at + $From.Length)
        [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false))
        Write-Host "  $Label" -ForegroundColor Green
    }

    # ── ② actions.json — Tend(code 38) ───────────────────────────────────
    Write-Host ""
    Write-Host "② actions.json — Tend 를 추가한다 (code 38)" -ForegroundColor Cyan

    $actFrom = "`n  ]`n}`n"
    $actTo = @'
,
    {
      "id": "Tend",
      "code": 38,
      "category": "labor",
      "desc": "가축과 벌통, 작물을 돌본다. 무언가를 만들어 내지는 않지만 다음 수확이 가능한 상태로 되돌린다. 밭에 서 있어야 하고, 지친 상태에서는 할 수 없다.",
      "params": {
        "duration_s": { "type": "int", "min": 60, "max": 1800, "default": 300 }
      },
      "requires": ["AtField"],
      "requires_any": [],
      "forbids": ["IsSleeping", "IsExhausted"],
      "grants": [],
      "clears": ["ResourceDepleted"],
      "duration": { "kind": "param", "base_s": 0, "per_meter_s": 0, "param": "duration_s" },
      "cost": 2,
      "default_timeout_s": 900,
      "emits": [
        { "command": "SetVisualState", "priority": "Normal", "map": { "Visual": "Working" } }
      ],
      "completes_on": ["NpcActionCompleted"],
      "fails_on": ["NpcActionFailed"]
    }
  ]
}
'@ -replace "`r`n", "`n"

    Edit-Json -File 'actions.json' -From $actFrom -To $actTo -Label 'Tend (code 38, labor, requires AtField)'

    # ── ③ archetypes.json — 목동에게 허용 ────────────────────────────────
    Write-Host ""
    Write-Host "③ archetypes.json — 목동에게 Tend 를 허용한다" -ForegroundColor Cyan
    Edit-Json -File 'archetypes.json' -Label 'shepherd.allowed_actions += Tend' `
        -From "        `"Store`",`n        `"Talk`",`n        `"Trade`",`n        `"Wait`",`n        `"Wander`",`n        `"Withdraw`"`n      ],`n      `"home_poi_type`": `"home`",`n      `"workplace_poi_type`": `"pasture`"" `
        -To   "        `"Store`",`n        `"Talk`",`n        `"Tend`",`n        `"Trade`",`n        `"Wait`",`n        `"Wander`",`n        `"Withdraw`"`n      ],`n      `"home_poi_type`": `"home`",`n      `"workplace_poi_type`": `"pasture`""

    # ── ④ fallback_plans.json — 실제로 써 본다 ───────────────────────────
    Write-Host ""
    Write-Host "④ fallback_plans.json — 목동 폴백에 Tend 를 끼운다" -ForegroundColor Cyan
    Edit-Json -File 'fallback_plans.json' -Label 'fb_shepherd 의 Gather 앞에 Tend 를 넣는다' `
        -From "        {`n          `"action`": `"Gather`",`n          `"args`": {`n            `"resource`": `"wool`",`n            `"count`": 5`n          },`n          `"timeout_s`": 900`n        }," `
        -To   "        {`n          `"action`": `"Tend`",`n          `"args`": {`n            `"duration_s`": 300`n          },`n          `"timeout_s`": 900`n        },`n        {`n          `"action`": `"Gather`",`n          `"args`": {`n            `"resource`": `"wool`",`n            `"count`": 5`n          },`n          `"timeout_s`": 900`n        },"

    # ── ⑤ 검증 ──────────────────────────────────────────────────────────
    Write-Host ""
    Write-Host "⑤ 검증" -ForegroundColor Cyan
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & dotnet run -c Release --no-build --no-launch-profile --project src/Npc.Host -- `
        validate --masterdata $md 2>&1 | ForEach-Object { Write-Host "   $($_.ToString())" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev

    if ($code -ne 0) {
        Write-Host ""
        Write-Host "검증 실패 — 위 메시지를 읽는다." -ForegroundColor Red
        exit $code
    }

    # ── ⑥ 프리픽스가 얼마나 흔들렸나 ─────────────────────────────────────
    Write-Host ""
    Write-Host "⑥ 고친 뒤 프리픽스" -ForegroundColor Cyan
    $after = Show-Prefix -Title '(변경 후)'

    Write-Host ""
    if ($before -and $after) {
        Write-Host ("   토큰  {0} → {1}  ({2:+#;-#;0})" -f $before.Tokens, $after.Tokens, ($after.Tokens - $before.Tokens)) -ForegroundColor Yellow
        Write-Host ("   SHA   {0} → {1}" -f $before.Sha, $after.Sha) -ForegroundColor Yellow
        if ($before.Sha -ne $after.Sha) {
            Write-Host ""
            Write-Host "   프리픽스 SHA 가 바뀌었다 = 미리 구워 둔 플랜이 전량 무효다." -ForegroundColor Red
            Write-Host "   의도한 무효화인지 스스로 판정한다. 아니라면 되돌린다." -ForegroundColor DarkGray
        }
    }

    Write-Host ""
    Write-Host "적용 완료. 실제로 명령이 나가는지 본다:" -ForegroundColor Green
    Write-Host "   dotnet run -c Release --project src/Npc.Host -- ``"
    Write-Host "       --loopback --npcs 20 --time-scale 60 --days 1 --no-llm --port 5080 ``"
    Write-Host "       --planstore ./nonexistent --masterdata $md"
    Write-Host "   ./samples/ch02_watch/watch-npc.ps1 -Npc 7 -Seconds 60"
}
finally {
    Pop-Location
}
