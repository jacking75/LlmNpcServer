<#
.SYNOPSIS
    프리베이크된 플랜의 사람 검수 도구. docs/13 §5 · T3-17.

.DESCRIPTION
    planstore/plans/ 에서 표본을 뽑아 버킷 상황과 플랜을 사이드바이사이드로 보여 주고,
    검수자의 판정([채택 / 수정 후 채택 / 폐기])과 소요 시간을 jsonl 로 기록한다.

    이 파일이 W12 보고서의 핵심 수치다:
        오써링 절감률 = 1 - (LLM 생성 + 검수 시간) / (수작성 시간)

    표본 추출은 시드 고정이다. 검수 대상이 실행마다 바뀌면 재현이 안 된다.
    이미 판정한 버킷은 건너뛰므로 여러 번에 나눠 검수할 수 있다.

.PARAMETER Store
    플랜 스토어 폴더. 기본 ./planstore

.PARAMETER Sample
    뽑을 표본 수. 기본 40 (docs/13 §5)

.PARAMETER Seed
    표본 추출 시드. 같은 시드면 같은 40건이 나온다. 기본 20260726

.PARAMETER Out
    판정 기록 jsonl. 기본 ./docs/measurements/review_W8.jsonl

.PARAMETER MasterData
    마스터데이터 폴더. 아키타입 설명을 여기서 읽는다. 기본 ./masterdata

.PARAMETER ListOnly
    표본 목록만 찍고 끝낸다. 무엇을 검수하게 되는지 미리 볼 때.

.EXAMPLE
    powershell -File tools/review.ps1 -Sample 40

.EXAMPLE
    powershell -File tools/review.ps1 -Sample 5 -ListOnly
#>
[CmdletBinding()]
param(
    [string] $Store = './planstore',
    [int]    $Sample = 40,
    [int]    $Seed = 20260726,
    [string] $Out = './docs/measurements/review_W8.jsonl',
    [string] $MasterData = './masterdata',
    [switch] $ListOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 콘솔이 ANSI 면 한국어가 깨진다. 출력 인코딩을 UTF-8 로 고정한다.
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# ---------------------------------------------------------------- 표본 추출

$plansDir = Join-Path $Store 'plans'
$pinnedDir = Join-Path $Store 'pinned'

if (-not (Test-Path $plansDir)) {
    Write-Error "플랜 폴더가 없다: $plansDir. 먼저 프리베이크를 돌린다 (tools/Npc.Prebake)."
}

# 파일 순서를 이름으로 고정한다. 파일시스템 열거 순서에 기대면 시드가 무의미해진다.
$all = @(Get-ChildItem -Path $plansDir -Filter '*.json' | Sort-Object -Property Name)

if ($all.Count -eq 0) {
    Write-Error "$plansDir 에 플랜이 없다."
}

# Fisher-Yates. 시드 고정 Random 이라 같은 입력이면 같은 표본이 나온다.
$rng = New-Object System.Random($Seed)
$order = 0..($all.Count - 1)

for ($i = $all.Count - 1; $i -gt 0; $i--) {
    $j = $rng.Next($i + 1)
    $tmp = $order[$i]; $order[$i] = $order[$j]; $order[$j] = $tmp
}

$take = [Math]::Min($Sample, $all.Count)
$chosen = @(for ($i = 0; $i -lt $take; $i++) { $all[$order[$i]] })

# ---------------------------------------------------------------- 이미 판정한 것

$done = @{}

if (Test-Path $Out) {
    foreach ($line in (Get-Content -Path $Out -Encoding UTF8)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $done[(ConvertFrom-Json $line).bucket] = $true } catch { }
    }
}

Write-Host ''
Write-Host "검수 도구 — docs/13 §5" -ForegroundColor Cyan
Write-Host ("스토어 {0} · 전체 {1}건 · 표본 {2}건 · 시드 {3}" -f $Store, $all.Count, $take, $Seed)
Write-Host ("기록   {0}{1}" -f $Out, $(if ($done.Count -gt 0) { " (이미 판정 $($done.Count)건)" } else { '' }))
Write-Host ''

if ($ListOnly) {
    $index = 0
    foreach ($file in $chosen) {
        $index++
        $mark = if ($done.ContainsKey([IO.Path]::GetFileNameWithoutExtension($file.Name))) { '완료' } else { '  ' }
        Write-Host ("  {0,3}. {1} {2}" -f $index, $mark, [IO.Path]::GetFileNameWithoutExtension($file.Name))
    }
    Write-Host ''
    return
}

# ---------------------------------------------------------------- 아키타입 설명

$archetypeById = @{}
$archetypePath = Join-Path $MasterData 'archetypes.json'

if (Test-Path $archetypePath) {
    $archetypeJson = Get-Content -Path $archetypePath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($a in $archetypeJson.archetypes) { $archetypeById[$a.id] = $a }
}

# ---------------------------------------------------------------- 출력 헬퍼

function Format-Steps {
    param($Plan)

    $lines = New-Object System.Collections.Generic.List[string]
    $n = 0

    foreach ($step in $Plan.steps) {
        $n++
        $stepArgs = @()

        # StrictMode 에서는 없는 속성을 그냥 읽으면 던진다. timeout_s 는 선택 필드다.
        $names = $step.PSObject.Properties.Name

        if ($names -contains 'args' -and $null -ne $step.args) {
            foreach ($p in $step.args.PSObject.Properties) {
                $value = if ($p.Value -is [array]) { $p.Value -join ',' } else { $p.Value }
                $stepArgs += ('{0}={1}' -f $p.Name, $value)
            }
        }

        $timeout = if ($names -contains 'timeout_s' -and $null -ne $step.timeout_s) {
            ' [{0}s]' -f $step.timeout_s
        }
        else { '' }

        $lines.Add(('{0}. {1}({2}){3}' -f $n, $step.action, ($stepArgs -join ' '), $timeout))
    }

    return $lines
}

function Write-SideBySide {
    param(
        [string] $Bucket,
        $Meta,
        $Plan,
        [int] $Index,
        [int] $Total
    )

    $parts = $Bucket -split '@'
    $archetypeId = $parts[0]
    $situation = if ($parts.Count -gt 1) { $parts[1] -split '\.' } else { @('?', '?', '?') }
    $archetype = $archetypeById[$archetypeId]

    $left = New-Object System.Collections.Generic.List[string]
    $left.Add("아키타입  $archetypeId")

    if ($null -ne $archetype) {
        if ($archetype.PSObject.Properties.Name -contains 'workplace_poi_type' -and $archetype.workplace_poi_type) {
            $left.Add("  일터    $($archetype.workplace_poi_type)")
        }
        if ($archetype.PSObject.Properties.Name -contains 'duty_hours' -and $archetype.duty_hours) {
            $left.Add("  근무    $($archetype.duty_hours -join ', ')")
        }
        if ($archetype.PSObject.Properties.Name -contains 'allowed_actions' -and $archetype.allowed_actions) {
            $left.Add("  허용    $($archetype.allowed_actions.Count)종")
        }
    }

    $left.Add('')
    $left.Add("시간대    $($situation[0])")
    $left.Add("지역상태  $($situation[1])")
    $left.Add("기후      $($situation[2])")
    $left.Add('')
    $left.Add("goal      $($Plan.goal)")
    $left.Add("loop      $($Plan.loop)")

    if ($Meta.PSObject.Properties.Name -contains 'origin') { $left.Add("origin    $($Meta.origin)") }
    if ($Meta.PSObject.Properties.Name -contains 'version') { $left.Add("version   $($Meta.version)") }
    if ($Plan.PSObject.Properties.Name -contains 'reasoning' -and $Plan.reasoning) {
        $left.Add('')
        $left.Add("근거      $($Plan.reasoning)")
    }

    $right = Format-Steps -Plan $Plan

    Write-Host ''
    Write-Host ('═' * 100) -ForegroundColor DarkGray
    Write-Host ("[{0}/{1}] {2}" -f $Index, $Total, $Bucket) -ForegroundColor Yellow
    Write-Host ('═' * 100) -ForegroundColor DarkGray
    Write-Host ("{0,-46}│ {1}" -f '상황 (검수 기준)', '플랜 (LLM 산출)') -ForegroundColor Cyan
    Write-Host ("{0,-46}┼{1}" -f ('─' * 46), ('─' * 53)) -ForegroundColor DarkGray

    $rows = [Math]::Max($left.Count, $right.Count)

    for ($i = 0; $i -lt $rows; $i++) {
        $l = if ($i -lt $left.Count) { $left[$i] } else { '' }
        $r = if ($i -lt $right.Count) { $right[$i] } else { '' }
        Write-Host ("{0,-46}│ {1}" -f $l, $r)
    }

    Write-Host ''
}

function Escape-Json {
    param([string] $Text)

    if ([string]::IsNullOrEmpty($Text)) { return '' }

    return $Text.Replace('\', '\\').Replace('"', '\"').Replace("`r", ' ').Replace("`n", ' ')
}

# ---------------------------------------------------------------- 순회

$outDir = Split-Path -Parent $Out
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Force $outDir | Out-Null }

$verdicts = @{ 'a' = 'accept'; 'e' = 'edit'; 'r' = 'reject' }
$counts = @{ 'accept' = 0; 'edit' = 0; 'reject' = 0 }
$totalMinutes = 0.0
$reviewed = 0
$index = 0

Write-Host '판정: [a] 채택   [e] 수정 후 채택   [r] 폐기   [s] 건너뛰기   [q] 종료' -ForegroundColor Green
Write-Host '수정 후 채택으로 판정한 플랜은 tools/pin_plan.ps1 로 pinned/ 에 올린다 (T3-19).'

foreach ($file in $chosen) {
    $index++
    $bucket = [IO.Path]::GetFileNameWithoutExtension($file.Name)

    if ($done.ContainsKey($bucket)) { continue }

    $meta = Get-Content -Path $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json

    if (($meta.PSObject.Properties.Name -notcontains 'plan') -or $null -eq $meta.plan) {
        Write-Warning "$bucket : 'plan' 필드가 없다. 건너뛴다."
        continue
    }

    Write-SideBySide -Bucket $bucket -Meta $meta -Plan $meta.plan -Index $index -Total $take

    # 검수 소요 시간을 자동 계측한다. 이 값이 절감률 계산의 분자다.
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $verdict = $null
    $note = ''

    while ($null -eq $verdict) {
        $key = (Read-Host '판정 [a/e/r/s/q]').Trim().ToLowerInvariant()

        if ($key -eq 'q') { $watch.Stop(); break }
        if ($key -eq 's') { $watch.Stop(); $verdict = 'skip'; break }

        if ($verdicts.ContainsKey($key)) {
            $verdict = $verdicts[$key]
            $watch.Stop()

            if ($verdict -ne 'accept') {
                $note = (Read-Host '이유 (한 줄)').Trim()
            }
        }
        else {
            Write-Host '  a / e / r / s / q 중 하나를 넣는다.' -ForegroundColor DarkYellow
        }
    }

    if ($null -eq $verdict) { break }        # q
    if ($verdict -eq 'skip') { continue }

    $minutes = [Math]::Round($watch.Elapsed.TotalMinutes, 2)
    $totalMinutes += $minutes
    $reviewed++
    $counts[$verdict]++

    $line = '{{"bucket":"{0}","verdict":"{1}","minutes":{2}' -f (Escape-Json $bucket), $verdict, $minutes.ToString([System.Globalization.CultureInfo]::InvariantCulture)

    if ($note) { $line += ',"note":"{0}"' -f (Escape-Json $note) }
    $line += '}'

    Add-Content -Path $Out -Value $line -Encoding UTF8

    Write-Host ("  → {0} · {1}분 기록" -f $verdict, $minutes) -ForegroundColor DarkGreen
}

# ---------------------------------------------------------------- 요약

Write-Host ''
Write-Host ('═' * 100) -ForegroundColor DarkGray

if ($reviewed -eq 0) {
    Write-Host '이번 회차에 판정한 것이 없다.'
    return
}

$adopted = $counts['accept'] + $counts['edit']
$rate = $adopted / $reviewed
$average = $totalMinutes / $reviewed

Write-Host ("판정 {0}건 · 채택 {1} · 수정후채택 {2} · 폐기 {3}" -f $reviewed, $counts['accept'], $counts['edit'], $counts['reject'])
Write-Host ("채택률 {0:P1} (게이트 ≥ 80%)" -f $rate) -ForegroundColor $(if ($rate -ge 0.8) { 'Green' } else { 'Red' })
Write-Host ("검수 시간 합 {0:F2}분 · 평균 {1:F2}분/건" -f $totalMinutes, $average)

# 오써링 절감률 — docs/13 §5. 수작성 가정은 15분/건이다.
$handwritten = 15.0
$saving = 1 - (($average * 2880) / ($handwritten * 2880))

Write-Host ("오써링 절감률 추정 {0:P1} (수작성 {1}분/건 가정 · 생성 시간은 manifest 의 wall_clock_s)" -f $saving, $handwritten)
Write-Host ("기록 → {0}" -f $Out)
Write-Host ''

if ($counts['edit'] -gt 0) {
    Write-Host ("수정 후 채택 {0}건은 tools/pin_plan.ps1 로 pinned/ 에 올린다 — 안 올리면 재프리베이크가 덮어쓴다." -f $counts['edit']) -ForegroundColor Yellow
}

if (Test-Path $pinnedDir) {
    $pinnedCount = @(Get-ChildItem -Path $pinnedDir -Filter '*.json' -ErrorAction SilentlyContinue).Count
    Write-Host ("현재 pinned {0}건" -f $pinnedCount)
}
