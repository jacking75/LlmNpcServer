#Requires -Version 5.1
<#
.SYNOPSIS
    4장 — 마스터데이터를 일부러 깨뜨리고 검증기가 무엇을 잡는지 본다.

.DESCRIPTION
    패치 9종을 각각 깨끗한 사본에 하나씩 적용하고 validate 를 돌린다.
    각 패치는 <b>한 곳만</b> 고친다. 그런데도 규칙이 둘 이상 걸리는 경우가 있다 —
    마스터데이터가 서로 물려 있기 때문이고, 그게 이 장의 요지다.

    깨뜨리는 것은 전부 lab/ 사본이다. 원본 masterdata/ 는 건드리지 않는다.

.EXAMPLE
    ./samples/ch04_break/break.ps1                # 9종 전부
    ./samples/ch04_break/break.ps1 -Only v5       # 하나만
    ./samples/ch04_break/break.ps1 -List          # 목록만
#>
[CmdletBinding()]
param(
    # 패치 하나만 돌린다 (v1 · v2 · …).
    [string]$Only = '',

    # 목록만 보고 끝낸다.
    [switch]$List,

    # 끝나고 사본을 남긴다 (직접 열어 보고 싶을 때).
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$lab  = Join-Path $root 'lab'

# 각 패치 = 파일 하나 · 찾을 문자열 · 바꿀 문자열.
# 원문을 그대로 찾아 바꾸므로 서식이 안 흐트러지고, 책에 그대로 옮겨 적을 수 있다.
$patches = @(
    @{
        key = 'v1'; expect = 'V1'
        title = 'code 중복 — 목수에게 대장장이 번호를 준다'
        file = 'archetypes.json'
        from = "`"code`": 1,`n      `"name_key`": `"npc.carpenter`""
        to   = "`"code`": 0,`n      `"name_key`": `"npc.carpenter`""
        why  = 'code 는 배열 첨자이자 버킷 키의 축이다. 겹치면 두 직업이 한 칸을 쓴다'
    },
    @{
        key = 'v2'; expect = 'V2'
        title = '없는 플래그 참조 — POI 가 AtBakery 를 세운다'
        file = 'pois.json'
        from = "`"grants`": [`n        `"AtField`"`n      ],"
        to   = "`"grants`": [`n        `"AtBakery`"`n      ],"
        why  = '플래그는 world_flags.json 이 정한 64비트 중 하나다. 자유 문자열이 아니다'
    },
    @{
        key = 'v3'; expect = 'V3'
        title = '없는 존 참조 — POI 를 아틀란티스로 보낸다'
        file = 'pois.json'
        from = "`"zone`": `"farmlands`","
        to   = "`"zone`": `"atlantis`","
        why  = '존·아이템·액션·POI 참조는 전부 대상 파일에 있어야 한다'
    },
    @{
        key = 'v4'; expect = 'V4'
        title = '카탈로그에 없는 액션 — 대장장이에게 Fly 를 허용한다'
        file = 'archetypes.json'
        from = "`"allowed_actions`": [`n        `"Craft`","
        to   = "`"allowed_actions`": [`n        `"Fly`",`n        `"Craft`","
        why  = 'LLM 이 조합할 수 있는 것은 액션 카탈로그 37개뿐이다'
    },
    @{
        key = 'v5'; expect = 'V5'
        title = '인구 가중치 합이 1.0 이 아니다 — 대장장이를 8배로'
        file = 'archetypes.json'
        from = "`"population_weight`": 0.012`n    },`n    {`n      `"id`": `"carpenter`""
        to   = "`"population_weight`": 0.099`n    },`n    {`n      `"id`": `"carpenter`""
        why  = '가중치는 인구 배분표다. 합이 1이 아니면 몇 마리를 만들지가 정해지지 않는다'
    },
    @{
        key = 'v6'; expect = 'V6'
        title = '버킷 총수가 안 맞는다 — 시간대를 하나 지운다'
        file = 'context_buckets.json'
        from = "`"values`": [`"Dawn`", `"Morning`", `"Noon`", `"Afternoon`", `"Evening`", `"Night`"]"
        to   = "`"values`": [`"Dawn`", `"Morning`", `"Noon`", `"Afternoon`", `"Evening`"]"
        why  = 'total_keys 는 차원 곱이다. 40×6×4×3 = 2,880 이 어긋나면 첨자 계산이 깨진다'
    },
    @{
        key = 'v7'; expect = 'V7'
        title = '폴백이 사라진다 — 목동 폴백의 id 를 바꾼다'
        file = 'fallback_plans.json'
        from = "`"id`": `"fb_shepherd`""
        to   = "`"id`": `"fb_shepherd_broken`""
        why  = '아키타입이 가리키는 폴백이 없으면 최후 보루가 사라진다'
    },
    @{
        key = 'v8'; expect = 'V8'
        title = '폴백이 허용 안 된 액션을 쓴다 — 목동에게 광질을 시킨다'
        file = 'fallback_plans.json'
        from = "`"action`": `"Gather`",`n          `"args`": {`n            `"resource`": `"wool`""
        to   = "`"action`": `"Mine`",`n          `"args`": {`n            `"resource`": `"wool`""
        why  = '폴백은 그 아키타입의 allowed_actions 만으로 구성돼야 한다'
    },
    @{
        key = 'v11'; expect = 'V11'
        title = '존 인접이 비대칭이다 — 한쪽 방향만 남긴다'
        file = 'zones.json'
        from = "        `"town_east_market`",`n        `"town_north`",`n        `"town_south`",`n        `"town_west_crafts`"`n"
        to   = "        `"town_north`",`n        `"town_south`",`n        `"town_west_crafts`"`n"
        why  = '존 그래프가 비대칭이면 거리표와 도달 판정이 방향에 따라 달라진다'
    }
)

if ($List) {
    Write-Host ""
    Write-Host "패치 $($patches.Count)종" -ForegroundColor Cyan
    foreach ($p in $patches) {
        Write-Host ("  {0,-5} {1,-4} {2}" -f $p.key, $p.expect, $p.title)
    }
    Write-Host ""
    exit 0
}

$targets = $patches
if ($Only) {
    $targets = @($patches | Where-Object { $_.key -eq $Only })
    if ($targets.Count -eq 0) {
        Write-Host "그런 패치가 없다: $Only  (-List 로 목록을 본다)" -ForegroundColor Red
        exit 1
    }
}

function Invoke-Validate {
    param([string]$Masterdata)

    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & dotnet run -c Release --no-build --no-launch-profile --project src/Npc.Host -- `
            validate --masterdata $Masterdata 2>&1 | ForEach-Object { $_.ToString() }
    }
    finally {
        $ErrorActionPreference = $prev
    }
    return $out
}

Push-Location $root
$results = @()

try {
    foreach ($p in $targets) {
        $name = "ch04_$($p.key)"
        & (Join-Path $PSScriptRoot '../lab.ps1') -New $name -Force | Out-Null

        $path = Join-Path $lab "$name/masterdata/$($p.file)"
        $text = [IO.File]::ReadAllText($path)

        # 원문을 LF 로 통일해 두고 찾는다 — 체크아웃이 CRLF 여도 앵커가 맞아야 한다.
        $text = $text -replace "`r`n", "`n"

        if ($text.IndexOf($p.from) -lt 0) {
            Write-Host ("[{0}] 앵커를 못 찾았다 — {1} 가 바뀐 것 같다" -f $p.key, $p.file) -ForegroundColor Yellow
            $results += [pscustomobject]@{ 패치 = $p.key; 예상 = $p.expect; 실제 = '앵커 없음'; 위반 = 0 }
            continue
        }

        # 첫 한 곳만 바꾼다. 여러 곳을 동시에 바꾸면 무엇이 원인인지 흐려진다.
        $at   = $text.IndexOf($p.from)
        $text = $text.Substring(0, $at) + $p.to + $text.Substring($at + $p.from.Length)
        [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false))

        $out   = Invoke-Validate -Masterdata "./lab/$name/masterdata"
        $fails = @($out | Where-Object { $_ -match 'FAIL V\d+' })
        $codes = @($fails | ForEach-Object { if ($_ -match 'FAIL (V\d+)') { $Matches[1] } } | Select-Object -Unique)

        Write-Host ""
        Write-Host ("[{0}] {1}" -f $p.key.ToUpper(), $p.title) -ForegroundColor Cyan
        Write-Host ("      고친 곳: {0}" -f $p.file) -ForegroundColor DarkGray
        if ($fails.Count -eq 0) {
            Write-Host "      통과해 버렸다 — 검증기의 구멍일 수 있다" -ForegroundColor Red
        }
        foreach ($f in $fails) { Write-Host ("      {0}" -f $f.Trim()) -ForegroundColor Yellow }

        $results += [pscustomobject]@{
            패치 = $p.key
            예상 = $p.expect
            실제 = if ($codes.Count) { $codes -join '+' } else { '(통과)' }
            위반 = $fails.Count
            일치 = if ($codes -contains $p.expect) { 'OK' } else { 'MISS' }
        }

        if (-not $Keep) { & (Join-Path $PSScriptRoot '../lab.ps1') -Drop $name | Out-Null }
    }

    Write-Host ""
    $results | Format-Table -AutoSize

    $miss = @($results | Where-Object { $_.일치 -ne 'OK' })
    if ($miss.Count -eq 0) {
        Write-Host "패치 $($results.Count)종이 전부 예상한 규칙에 걸렸다." -ForegroundColor Green
        exit 0
    }
    Write-Host "$($miss.Count)종이 예상과 다르다 — 위 표를 본다." -ForegroundColor Yellow
    exit 1
}
finally {
    Pop-Location
}
