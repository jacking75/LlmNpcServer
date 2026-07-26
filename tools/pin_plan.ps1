<#
.SYNOPSIS
    검수에서 수정 채택된 플랜을 pinned/ 로 승격한다. docs/13 §5 · §8 · T3-19.

.DESCRIPTION
    plans/<bucket>.json 을 pinned/<bucket>.json 으로 옮기고 origin 을 Pinned 로 바꾼다.
    승격된 플랜은 PlanStore.SetBucket 이 덮어쓰지 않으므로 재프리베이크에서 살아남는다 (T3-02).

    pinned/ 는 반드시 버전 관리에 올린다. 잃으면 검수 작업이 통째로 날아간다 (CLAUDE.md §6).
    이 스크립트는 pinned/ 가 .gitignore 에 걸려 있지 않은지도 확인한다.

.PARAMETER Store
    플랜 스토어 폴더. 기본 ./planstore

.PARAMETER Bucket
    승격할 버킷 이름. 예: blacksmith@Evening.War.Cold (확장자 없이). 여러 개 줄 수 있다.

.PARAMETER FromReview
    검수 기록 jsonl. verdict 가 edit 인 것을 전부 승격한다. docs/measurements/review_W8.jsonl

.PARAMETER Copy
    옮기지 않고 복사한다. plans/ 쪽을 남겨 두고 비교하고 싶을 때.

.PARAMETER WhatIf
    무엇을 하게 되는지만 보여 준다.

.EXAMPLE
    powershell -File tools/pin_plan.ps1 -Bucket blacksmith@Evening.War.Cold

.EXAMPLE
    powershell -File tools/pin_plan.ps1 -FromReview docs/measurements/review_W8.jsonl
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]   $Store = './planstore',
    [string[]] $Bucket = @(),
    [string]   $FromReview = '',
    [switch]   $Copy
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$plansDir = Join-Path $Store 'plans'
$pinnedDir = Join-Path $Store 'pinned'

if (-not (Test-Path $plansDir)) {
    Write-Error "플랜 폴더가 없다: $plansDir"
}

# ---------------------------------------------------------------- 대상 산출

$targets = New-Object System.Collections.Generic.List[string]

foreach ($b in $Bucket) {
    if (-not [string]::IsNullOrWhiteSpace($b)) {
        $targets.Add(($b -replace '\.json$', ''))
    }
}

if ($FromReview) {
    if (-not (Test-Path $FromReview)) {
        Write-Error "검수 기록이 없다: $FromReview"
    }

    foreach ($line in (Get-Content -Path $FromReview -Encoding UTF8)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }

        $entry = ConvertFrom-Json $line
        $names = $entry.PSObject.Properties.Name

        if (($names -contains 'verdict') -and $entry.verdict -eq 'edit' -and ($names -contains 'bucket')) {
            $targets.Add($entry.bucket)
        }
    }
}

$targets = @($targets | Select-Object -Unique)

if ($targets.Count -eq 0) {
    Write-Host '승격할 것이 없다. -Bucket 또는 -FromReview 를 준다.' -ForegroundColor Yellow
    return
}

Write-Host ''
Write-Host "pinned 승격 — docs/13 §5" -ForegroundColor Cyan
Write-Host ("스토어 {0} · 대상 {1}건" -f $Store, $targets.Count)
Write-Host ''

# ---------------------------------------------------------------- .gitignore 확인
#
# pinned/ 가 무시되면 승격해도 커밋되지 않는다 — 검수 작업이 조용히 사라진다.

if (-not (Test-Path $pinnedDir)) {
    New-Item -ItemType Directory -Force $pinnedDir | Out-Null
}

if (Get-Command git -ErrorAction SilentlyContinue) {
    # 네이티브 exe 의 stderr 를 Stop 정책으로 받으면 NativeCommandError 로 죽는다.
    # 저장소 밖의 스토어(테스트용 임시 폴더)에서도 승격은 되어야 하므로 여기서만 정책을 풀어 둔다.
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'

    $inRepo = & git -C $pinnedDir rev-parse --is-inside-work-tree 2>$null
    $ignored = $false

    if ($LASTEXITCODE -eq 0 -and "$inRepo".Trim() -eq 'true') {
        & git -C $pinnedDir check-ignore -q -- (Join-Path $pinnedDir 'probe.json') 2>$null
        $ignored = ($LASTEXITCODE -eq 0)
    }
    else {
        $inRepo = 'false'
    }

    $ErrorActionPreference = $saved

    if ($ignored) {
        Write-Error "$pinnedDir 이 .gitignore 에 걸려 있다. 승격해도 커밋되지 않는다 (CLAUDE.md §6)."
    }

    if ("$inRepo".Trim() -eq 'true') {
        Write-Host '확인: pinned/ 는 .gitignore 에 걸려 있지 않다.' -ForegroundColor DarkGreen
    }
    else {
        Write-Host '알림: 스토어가 git 저장소 밖이라 .gitignore 검사를 건너뛴다.' -ForegroundColor DarkGray
    }
}

# ---------------------------------------------------------------- 승격

$promoted = 0
$missing = 0
$already = 0

foreach ($bucket in $targets) {
    $source = Join-Path $plansDir "$bucket.json"
    $destination = Join-Path $pinnedDir "$bucket.json"

    if (-not (Test-Path $source)) {
        if (Test-Path $destination) {
            Write-Host ("  = {0} 은 이미 pinned 다." -f $bucket) -ForegroundColor DarkGray
            $already++
        }
        else {
            Write-Warning ("{0} : plans/ 에 없다." -f $bucket)
            $missing++
        }

        continue
    }

    $raw = Get-Content -Path $source -Raw -Encoding UTF8
    $meta = ConvertFrom-Json $raw

    if ($meta.PSObject.Properties.Name -contains 'origin') {
        $meta.origin = 'Pinned'
    }
    else {
        $meta | Add-Member -NotePropertyName 'origin' -NotePropertyValue 'Pinned'
    }

    if ($PSCmdlet.ShouldProcess($destination, $(if ($Copy) { '복사 + origin=Pinned' } else { '이동 + origin=Pinned' }))) {
        # BOM 없는 UTF-8 로 쓴다. PlanStoreIo 가 읽는 파일이고 해시에 BOM 이 섞이면 안 된다.
        $json = $meta | ConvertTo-Json -Depth 12
        [System.IO.File]::WriteAllText($destination, $json, (New-Object System.Text.UTF8Encoding($false)))

        if (-not $Copy) {
            Remove-Item -Path $source -Force
        }
    }

    Write-Host ("  + {0}" -f $bucket) -ForegroundColor Green
    $promoted++
}

# ---------------------------------------------------------------- 요약

Write-Host ''
Write-Host ("승격 {0}건{1}{2}" -f
    $promoted,
    $(if ($already -gt 0) { " · 이미 pinned $already" } else { '' }),
    $(if ($missing -gt 0) { " · 없음 $missing" } else { '' }))

$pinnedCount = @(Get-ChildItem -Path $pinnedDir -Filter '*.json' -ErrorAction SilentlyContinue).Count

Write-Host ("현재 pinned {0}건 → {1}" -f $pinnedCount, $pinnedDir)
Write-Host ''
Write-Host 'pinned/ 를 커밋한다. 재프리베이크는 이 플랜을 덮어쓰지 않는다 (T3-02).' -ForegroundColor Yellow

if ($missing -gt 0) {
    exit 1
}
