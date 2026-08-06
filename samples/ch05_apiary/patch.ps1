#Requires -Version 5.1
<#
.SYNOPSIS
    5장 — 마을에 없던 것을 만든다. 아이템 honey 와 양봉장(apiary) POI 둘.

.DESCRIPTION
    작성 순서를 지킨다. items → pois 다. 거꾸로 하면 POI 가 없는 아이템을 낸다고 해서
    V3 에 걸리고, 그때 되돌아와야 한다.

      ① items.json   honey (code 83) 추가
      ② pois.json    apiary_001_12 (code 244) · apiary_002_08 (code 245) 추가
      ③ poi_distances.bin 재생성   ← 이걸 빼먹는 것이 이 장의 함정이다

    전부 lab/<이름>/masterdata 사본에서 한다. 원본은 안 건드린다.

.EXAMPLE
    ./samples/ch05_apiary/patch.ps1
    ./samples/ch05_apiary/patch.ps1 -Lab ch05 -SkipDistances   # 함정을 재현해 본다
#>
[CmdletBinding()]
param(
    [string]$Lab = 'ch05',

    # 거리표를 일부러 안 굽는다. 무슨 일이 나는지 보려면.
    [switch]$SkipDistances,

    # 이미 있는 실습장에 덧바른다 (6장 이후가 이걸 쓴다).
    [switch]$Reuse
)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$labPath = Join-Path $root "lab/$Lab"
$md      = Join-Path $labPath 'masterdata'

Push-Location $root
try {
    if (-not $Reuse -or -not (Test-Path $md)) {
        & (Join-Path $root 'samples/lab.ps1') -New $Lab -Force | Out-Null
    }

    # 원문 그대로 찾아 바꾼다. JSON 을 파싱해 다시 쓰면 서식이 통째로 흐트러져
    # git diff 로 "내가 무엇을 넣었나" 를 못 읽는다.
    function Edit-Json {
        param([string]$File, [string]$From, [string]$To, [string]$Label)

        $path = Join-Path $md $File
        $text = ([IO.File]::ReadAllText($path)) -replace "`r`n", "`n"

        if ($text.IndexOf($From) -lt 0) {
            throw "$File 에서 앵커를 못 찾았다 ($Label). 원본이 바뀌었는지 확인한다."
        }
        if ($text.IndexOf($To) -ge 0) {
            Write-Host "  이미 적용돼 있다: $Label" -ForegroundColor DarkGray
            return
        }

        $at   = $text.IndexOf($From)
        $text = $text.Substring(0, $at) + $To + $text.Substring($at + $From.Length)
        [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false))
        Write-Host "  $Label" -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "① items.json — honey 를 넣는다" -ForegroundColor Cyan

    $itemFrom = '    { "id": "map",            "code": 82, "category": "product", "grants": ["HasProduct"], "stack": 10 }' + "`n  ],"
    $itemTo   = '    { "id": "map",            "code": 82, "category": "product", "grants": ["HasProduct"], "stack": 10 },' + "`n" +
                '    { "id": "honey",          "code": 83, "category": "raw",     "grants": ["HasRawMaterial"], "stack": 30 }' + "`n  ],"

    Edit-Json -File 'items.json' -From $itemFrom -To $itemTo -Label 'honey (code 83, raw, HasRawMaterial)'

    Write-Host ""
    Write-Host "② pois.json — 양봉장 둘을 세운다" -ForegroundColor Cyan

    # 마지막 POI(wayshrine_001_12, code 243) 뒤에 붙인다.
    # code 는 1부터 연속이어야 한다 — poi_distances.bin 의 첨자가 code - 1 이기 때문이다.
    $poiFrom = @'
      "allowed_archetypes": [],
      "resources": []
    }
  ]
}
'@ -replace "`r`n", "`n"

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
      "pos": {
        "x": -420.0,
        "y": 0.0,
        "z": -980.0
      },
      "capacity": 12,
      "open_hours": {
        "from": "Dawn",
        "to": "Evening"
      },
      "grants": [
        "AtField"
      ],
      "allowed_archetypes": [
        "shepherd",
        "herbalist"
      ],
      "resources": [
        "honey"
      ]
    },
    {
      "id": "apiary_002_08",
      "code": 245,
      "zone": "farmlands",
      "type": "field",
      "subtype": "apiary",
      "pos": {
        "x": 980.0,
        "y": 0.0,
        "z": 1310.0
      },
      "capacity": 12,
      "open_hours": {
        "from": "Dawn",
        "to": "Evening"
      },
      "grants": [
        "AtField"
      ],
      "allowed_archetypes": [
        "shepherd",
        "herbalist"
      ],
      "resources": [
        "honey"
      ]
    }
  ]
}
'@ -replace "`r`n", "`n"

    Edit-Json -File 'pois.json' -From $poiFrom -To $poiTo -Label 'apiary_001_12 (244) · apiary_002_08 (245)'

    Write-Host ""
    if ($SkipDistances) {
        Write-Host "③ poi_distances.bin — 일부러 건너뛴다" -ForegroundColor Yellow
        Write-Host "   POI 는 245개가 됐는데 거리표는 243개짜리 그대로다." -ForegroundColor Yellow
    }
    else {
        Write-Host "③ poi_distances.bin — 다시 굽는다" -ForegroundColor Cyan

        # 생성기는 --masterdata 를 안 받는다. NpcServer.sln 이 있는 폴더를 저장소 루트로 보고
        # 그 아래 masterdata/ 를 읽는다. lab.ps1 이 실습장에 같은 이름의 빈 파일을 두는 이유다.
        Push-Location $labPath
        try {
            $prev = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            & dotnet run (Join-Path $root 'tools/gen_poi_distances.cs') 2>&1 |
                ForEach-Object { Write-Host "   $($_.ToString())" -ForegroundColor DarkGray }
            $ErrorActionPreference = $prev
        }
        finally { Pop-Location }
    }

    Write-Host ""
    Write-Host "④ 검증" -ForegroundColor Cyan
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & dotnet run -c Release --no-build --no-launch-profile --project src/Npc.Host -- `
        validate --masterdata "./lab/$Lab/masterdata" 2>&1 |
        ForEach-Object { Write-Host "   $($_.ToString())" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev

    Write-Host ""
    if ($code -eq 0) {
        Write-Host "적용 완료 — lab/$Lab/masterdata" -ForegroundColor Green
        Write-Host "  이제 이렇게 돌려 본다:" -ForegroundColor Cyan
        Write-Host "    ./samples/check.ps1 -Masterdata ./lab/$Lab/masterdata -Npcs 50"
    }
    else {
        Write-Host "검증 실패 — 위 메시지를 읽는다." -ForegroundColor Red
    }
    exit $code
}
finally {
    Pop-Location
}
