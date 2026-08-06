#Requires -Version 5.1
<#
.SYNOPSIS
    7장 — 새 직업 beekeeper 를 추가한다. JSON 만으로는 안 끝나는 첫 실습.

.DESCRIPTION
    <b>이 스크립트는 실습장 사본이 아니라 저장소 본체를 고친다.</b> 이유가 있다.

      BucketKey.ArchetypeCount 는 <b>컴파일 상수</b>다. 플랜 스토어 배열 길이,
      캐시 통계 배열, 히트맵 열 수가 전부 이 값에서 나온다. 사본에만 아키타입을 넣으면
      바이너리는 여전히 40으로 컴파일돼 있어서 41번째가 첨자 밖으로 나간다.
      그래서 마스터데이터와 코드를 <b>같이</b> 고쳐야 한다.

    고치는 곳 다섯 (+ 생성기 한 번)

      ① items.json           honey 추가                         (5장과 같다)
      ② pois.json            apiary POI 둘 + allowed_archetypes
      ③ archetypes.json      beekeeper(code 40) · 가중치 재배분
      ④ context_buckets.json total_keys 2880 → 2952
      ⑤ fallback_plans.json  fb_beekeeper
      ⑥ BucketKey.cs         ArchetypeCount 40 → 41
      ⑦ gen_npcs 재실행      npc_instances.json 을 다시 만든다

    되돌리려면 revert.ps1 이다. 반드시 <b>전용 브랜치</b>에서 한다.

.EXAMPLE
    git switch -c lab/ch07-beekeeper
    ./samples/ch07_beekeeper/apply.ps1
    ./samples/ch07_beekeeper/revert.ps1
#>
[CmdletBinding()]
param(
    # main 에서도 그냥 진행한다. 권하지 않는다.
    [switch]$Force,

    # 빌드·생성기·검증을 건너뛰고 파일만 고친다.
    [switch]$EditOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

Push-Location $root
try {
    # ── 안전장치 ──────────────────────────────────────────────────────────
    $branch = (& git rev-parse --abbrev-ref HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $branch -in @('main', 'master') -and -not $Force) {
        Write-Host ""
        Write-Host "지금 '$branch' 브랜치다. 이 실습은 본체를 고치므로 전용 브랜치에서 한다." -ForegroundColor Red
        Write-Host "  git switch -c lab/ch07-beekeeper" -ForegroundColor Yellow
        Write-Host "그래도 강행하려면 -Force." -ForegroundColor DarkGray
        exit 1
    }

    function Edit-File {
        param([string]$Path, [string]$From, [string]$To, [string]$Label)

        $full = Join-Path $root $Path
        $text = ([IO.File]::ReadAllText($full)) -replace "`r`n", "`n"

        if ($text.IndexOf($To) -ge 0) {
            Write-Host "  이미 적용됨: $Label" -ForegroundColor DarkGray
            return
        }
        if ($text.IndexOf($From) -lt 0) {
            throw "$Path 에서 앵커를 못 찾았다 ($Label)."
        }

        $at   = $text.IndexOf($From)
        $text = $text.Substring(0, $at) + $To + $text.Substring($at + $From.Length)
        [IO.File]::WriteAllText($full, $text, (New-Object Text.UTF8Encoding $false))
        Write-Host "  $Label" -ForegroundColor Green
    }

    # ── ① items.json ──────────────────────────────────────────────────────
    Write-Host ""
    Write-Host "① items.json" -ForegroundColor Cyan
    Edit-File -Path 'masterdata/items.json' -Label 'honey (code 83)' `
        -From ('    { "id": "map",            "code": 82, "category": "product", "grants": ["HasProduct"], "stack": 10 }' + "`n  ],") `
        -To   ('    { "id": "map",            "code": 82, "category": "product", "grants": ["HasProduct"], "stack": 10 },' + "`n" +
               '    { "id": "honey",          "code": 83, "category": "raw",     "grants": ["HasRawMaterial"], "stack": 30 }' + "`n  ],")

    # ── ② pois.json ───────────────────────────────────────────────────────
    Write-Host ""
    Write-Host "② pois.json" -ForegroundColor Cyan

    $poiFrom = "      `"allowed_archetypes`": [],`n      `"resources`": []`n    }`n  ]`n}`n"
    $poiTo = @'
      "allowed_archetypes": [],
      "resources": []
    },
    {
      "id": "apiary_001_12",
      "code": 244,
      "zone": "highland_pasture",
      "type": "field",
      "subtype": "apiary",
      "pos": { "x": -420.0, "y": 0.0, "z": -980.0 },
      "capacity": 12,
      "open_hours": { "from": "Dawn", "to": "Evening" },
      "grants": ["AtField"],
      "allowed_archetypes": ["beekeeper", "herbalist"],
      "resources": ["honey"]
    },
    {
      "id": "apiary_002_08",
      "code": 245,
      "zone": "farmlands",
      "type": "field",
      "subtype": "apiary",
      "pos": { "x": 980.0, "y": 0.0, "z": 1310.0 },
      "capacity": 12,
      "open_hours": { "from": "Dawn", "to": "Evening" },
      "grants": ["AtField"],
      "allowed_archetypes": ["beekeeper", "herbalist"],
      "resources": ["honey"]
    }
  ]
}
'@ -replace "`r`n", "`n"

    Edit-File -Path 'masterdata/pois.json' -From $poiFrom -To $poiTo `
        -Label 'apiary_001_12 (244) · apiary_002_08 (245) · 정원 24'

    # ── ③ archetypes.json ────────────────────────────────────────────────
    Write-Host ""
    Write-Host "③ archetypes.json" -ForegroundColor Cyan

    # 인구 가중치는 합이 정확히 1.0 이어야 한다 (V5).
    # 새 직업 몫 0.004(=20마리)를 목동에게서 떼어 온다.
    Edit-File -Path 'masterdata/archetypes.json' -Label 'shepherd 가중치 0.025 → 0.021' `
        -From "`"fallback_plan`": `"fb_shepherd`",`n      `"combat_capable`": false,`n      `"population_weight`": 0.025" `
        -To   "`"fallback_plan`": `"fb_shepherd`",`n      `"combat_capable`": false,`n      `"population_weight`": 0.021"

    $archFrom = "`n  ]`n}`n"
    $archTo = @'
,
    {
      "id": "beekeeper",
      "code": 40,
      "name_key": "npc.beekeeper",
      "desc": "양봉가. 고지 초원과 농지의 벌통을 돌보며 꿀을 거둔다. 벌이 예민한 낮 시간을 피해 이른 아침과 해질녘에 주로 움직인다.",
      "allowed_actions": [
        "Drink",
        "Eat",
        "Emote",
        "Equip",
        "Flee",
        "Gather",
        "Greet",
        "MoveTo",
        "Observe",
        "PickUp",
        "Rest",
        "Retreat",
        "Sleep",
        "Store",
        "Talk",
        "Trade",
        "Wait",
        "Wander",
        "Withdraw"
      ],
      "home_poi_type": "home",
      "workplace_poi_type": "apiary",
      "primary_recipes": [],
      "traits": {
        "diligence": 75,
        "sociability": 35,
        "courage": 40,
        "greed": 30
      },
      "default_goals": [
        "tend_hives",
        "harvest_honey",
        "sell_honey"
      ],
      "initial_inventory": [
        { "item": "bread", "count": 2 },
        { "item": "water", "count": 1 },
        { "item": "coin", "count": 10 }
      ],
      "fallback_plan": "fb_beekeeper",
      "combat_capable": false,
      "population_weight": 0.004
    }
  ]
}
'@ -replace "`r`n", "`n"

    # 배열 마지막 원소 뒤에 붙인다. code 는 40 — 반드시 뒤에만 붙인다.
    Edit-File -Path 'masterdata/archetypes.json' -From $archFrom -To $archTo `
        -Label 'beekeeper (code 40, 일터 apiary, 가중치 0.004 = 20마리)'

    # ── ④ context_buckets.json ───────────────────────────────────────────
    Write-Host ""
    Write-Host "④ context_buckets.json" -ForegroundColor Cyan
    Edit-File -Path 'masterdata/context_buckets.json' -Label 'total_keys 2880 → 2952 (41 × 6 × 4 × 3)' `
        -From '"total_keys": 2880' -To '"total_keys": 2952'

    # ── ⑤ fallback_plans.json ────────────────────────────────────────────
    Write-Host ""
    Write-Host "⑤ fallback_plans.json" -ForegroundColor Cyan

    $fbFrom = "`n  ]`n}`n"
    $fbTo = @'
,
    {
      "id": "fb_beekeeper",
      "archetype": "beekeeper",
      "goal": "harvest_and_sell_honey",
      "steps": [
        { "action": "MoveTo", "args": { "poi": "$nearest_field" },          "timeout_s": 600 },
        { "action": "Gather", "args": { "resource": "honey", "count": 5 },  "timeout_s": 900 },
        { "action": "MoveTo", "args": { "poi": "$home" },                   "timeout_s": 600 },
        { "action": "Store",  "args": { "item": "honey", "count": 5 },      "timeout_s": 300 },
        { "action": "Eat",    "args": {},                                   "timeout_s": 300 },
        { "action": "MoveTo", "args": { "poi": "$market" },                 "timeout_s": 600 },
        { "action": "Trade",  "args": { "item": "honey", "amount": 5 },     "timeout_s": 600 },
        { "action": "MoveTo", "args": { "poi": "$home" },                   "timeout_s": 600 },
        { "action": "Sleep",  "args": { "until_time": "Morning" },          "timeout_s": 7200 }
      ],
      "on_step_fail": "skip",
      "loop": true
    }
  ]
}
'@ -replace "`r`n", "`n"

    Edit-File -Path 'masterdata/fallback_plans.json' -From $fbFrom -To $fbTo `
        -Label 'fb_beekeeper (9스텝, loop:true)'

    # ── ⑥ BucketKey.cs ───────────────────────────────────────────────────
    Write-Host ""
    Write-Host "⑥ src/Npc.Core/Planning/BucketKey.cs — 여기서부터가 코드다" -ForegroundColor Cyan
    Edit-File -Path 'src/Npc.Core/Planning/BucketKey.cs' -Label 'ArchetypeCount 40 → 41' `
        -From "    /// <summary>아키타입 수. archetypes.json 의 40 과 맞아야 한다 (V6).</summary>`n    public const int ArchetypeCount = 40;" `
        -To   "    /// <summary>아키타입 수. archetypes.json 의 41 과 맞아야 한다 (V6).</summary>`n    public const int ArchetypeCount = 41;"

    if ($EditOnly) {
        Write-Host ""
        Write-Host "파일만 고쳤다. 나머지는 손으로 돌린다." -ForegroundColor Yellow
        exit 0
    }

    # ── ⑦ 생성기 · 빌드 · 검증 ───────────────────────────────────────────
    Write-Host ""
    Write-Host "⑦ poi_distances.bin · npc_instances.json 재생성" -ForegroundColor Cyan
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & dotnet run tools/gen_poi_distances.cs 2>&1 | ForEach-Object { Write-Host "   $($_.ToString())" -ForegroundColor DarkGray }
    & dotnet run tools/gen_npcs.cs           2>&1 | ForEach-Object { Write-Host "   $($_.ToString())" -ForegroundColor DarkGray }

    Write-Host ""
    Write-Host "⑧ 빌드" -ForegroundColor Cyan
    & dotnet build -c Release 2>&1 | Select-Object -Last 4 | ForEach-Object { Write-Host "   $($_.ToString())" -ForegroundColor DarkGray }

    Write-Host ""
    Write-Host "⑨ 검증" -ForegroundColor Cyan
    & dotnet run -c Release --no-build --no-launch-profile --project src/Npc.Host -- `
        validate --masterdata (Join-Path $root 'masterdata') 2>&1 | ForEach-Object { Write-Host "   $($_.ToString())" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev

    Write-Host ""
    if ($code -eq 0) {
        Write-Host "beekeeper 추가 완료. 아키타입이 41종이 됐다." -ForegroundColor Green
        Write-Host "  ./samples/check.ps1 -Npcs 200" -ForegroundColor Cyan
        Write-Host "  되돌리기: ./samples/ch07_beekeeper/revert.ps1" -ForegroundColor DarkGray
    }
    else {
        Write-Host "검증 실패 — 위 메시지를 읽는다." -ForegroundColor Red
    }
    exit $code
}
finally {
    Pop-Location
}
