#Requires -Version 5.1
<#
.SYNOPSIS
    18장 — 검증에서 반려된 플랜을 집계한다.

.DESCRIPTION
    planstore/rejected/ 에는 버려지지 않은 실패가 쌓여 있다.
    프리베이크가 반려한 플랜을 응답 원문·실패 코드·토큰·비용과 함께 남긴 것이다.

    "검증 실패분을 조용히 폐기하면 품질 개선의 원자료가 사라진다" 는 규칙의 실물이고,
    이 스크립트는 그 더미에서 다음에 무엇을 고칠지를 뽑아낸다.

    집계 축
      stage        어느 단계에서 걸렸나 (Schema · Vocabulary · Coherence · DryRun)
      code         무슨 규칙인가 (V1.STEP_COUNT · V3.PRECONDITION_UNMET · …)
      archetype    어느 직업의 플랜이 잘 실패하나
      step         플랜의 몇 번째 스텝에서 걸리나

.EXAMPLE
    ./samples/ch18_review/failures.ps1
    ./samples/ch18_review/failures.ps1 -Top 15 -Csv ./lab/ch18_failures.csv
    ./samples/ch18_review/failures.ps1 -Show V3.PRECONDITION_UNMET
#>
[CmdletBinding()]
param(
    [string]$Rejected = './planstore/rejected',

    # 상위 몇 개까지 볼 것인가.
    [int]$Top = 10,

    # 이 코드의 실패 사례 하나를 원문까지 펼쳐 본다.
    [string]$Show = '',

    [string]$Csv = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $root

try {
    if (-not (Test-Path $Rejected)) {
        Write-Host "반려 폴더가 없다: $Rejected" -ForegroundColor Yellow
        Write-Host "17장의 프리베이크를 한 번이라도 돌려야 생긴다." -ForegroundColor DarkGray
        exit 1
    }

    $files = @(Get-ChildItem -Path $Rejected -Filter *.json -File)
    if ($files.Count -eq 0) {
        Write-Host "반려 기록이 하나도 없다." -ForegroundColor Yellow
        exit 0
    }

    Write-Host ""
    Write-Host ("반려 기록 {0}건 — {1}" -f $files.Count, (Resolve-Path $Rejected).Path) -ForegroundColor Cyan

    $records = @()
    foreach ($f in $files) {
        try {
            $j = Get-Content -Path $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch { continue }

        $records += [pscustomobject]@{
            bucket     = $j.bucket
            archetype  = $j.archetype
            attempt    = $j.attempt
            stage      = $j.stage
            code       = $j.code
            step       = $j.step
            detail     = $j.detail
            cost       = [double]$j.cost_usd
            prompt     = [int]$j.prompt_tokens
            cached     = [int]$j.cached_tokens
            completion = [int]$j.completion_tokens
            latency    = [double]$j.latency_ms
            file       = $f.Name
        }
    }

    # ── 단계별 ────────────────────────────────────────────────────────────
    Write-Host ""
    Write-Host "① 어느 단계에서 걸렸나" -ForegroundColor Cyan
    $records | Group-Object stage | Sort-Object Count -Descending |
        ForEach-Object {
            [pscustomobject]@{
                단계 = $_.Name
                건수 = $_.Count
                '비율 %' = [math]::Round(100.0 * $_.Count / $records.Count, 1)
            }
        } | Format-Table -AutoSize

    # ── 코드별 ────────────────────────────────────────────────────────────
    Write-Host "② 무슨 규칙에 걸렸나 (상위 $Top)" -ForegroundColor Cyan
    $byCode = $records | Group-Object code | Sort-Object Count -Descending |
        Select-Object -First $Top |
        ForEach-Object {
            [pscustomobject]@{
                코드 = $_.Name
                건수 = $_.Count
                '비율 %' = [math]::Round(100.0 * $_.Count / $records.Count, 1)
                단계 = ($_.Group | Select-Object -First 1).stage
            }
        }
    $byCode | Format-Table -AutoSize

    # ── 아키타입별 ────────────────────────────────────────────────────────
    Write-Host "③ 어느 직업이 잘 실패하나 (상위 $Top)" -ForegroundColor Cyan
    $records | Group-Object archetype | Sort-Object Count -Descending |
        Select-Object -First $Top |
        ForEach-Object { [pscustomobject]@{ 아키타입 = $_.Name; 반려 = $_.Count } } |
        Format-Table -AutoSize

    # ── 스텝 위치 ─────────────────────────────────────────────────────────
    Write-Host "④ 플랜의 몇 번째 스텝에서 걸리나" -ForegroundColor Cyan
    $records | Where-Object { $_.step -ge 0 } | Group-Object step | Sort-Object { [int]$_.Name } |
        ForEach-Object { [pscustomobject]@{ 스텝 = $_.Name; 건수 = $_.Count } } |
        Format-Table -AutoSize

    # ── 비용 ──────────────────────────────────────────────────────────────
    $cost   = ($records | Measure-Object cost -Sum).Sum
    $prompt = ($records | Measure-Object prompt -Sum).Sum
    $cached = ($records | Measure-Object cached -Sum).Sum
    $lat    = ($records | Measure-Object latency -Average).Average

    Write-Host "⑤ 이 실패들이 쓴 것" -ForegroundColor Cyan
    Write-Host ("   비용        `${0:N4}" -f $cost)
    Write-Host ("   입력 토큰   {0:N0}  (그중 캐시 적중 {1:N0} · {2}%)" -f `
        $prompt, $cached, [math]::Round(100.0 * $cached / [math]::Max($prompt, 1), 1))
    Write-Host ("   평균 지연   {0:N0} ms" -f $lat)
    Write-Host ""
    Write-Host "   실패도 돈이 든다. 통과율을 올리는 것이 곧 비용 절감이다." -ForegroundColor DarkGray

    # ── 사례 하나 펼치기 ──────────────────────────────────────────────────
    if ($Show) {
        $one = $records | Where-Object { $_.code -eq $Show } | Select-Object -First 1
        if (-not $one) {
            Write-Host ""
            Write-Host "그 코드의 사례가 없다: $Show" -ForegroundColor Yellow
        }
        else {
            Write-Host ""
            Write-Host "⑥ 사례 하나 — $($one.code)" -ForegroundColor Cyan
            Write-Host ("   버킷    {0}" -f $one.bucket)
            Write-Host ("   단계    {0}  (시도 {1}회차)" -f $one.stage, $one.attempt)
            Write-Host ("   스텝    {0}" -f $one.step)
            Write-Host ("   파일    {0}" -f $one.file)
            Write-Host ""
            Write-Host "   detail:" -ForegroundColor DarkGray
            ($one.detail -split '(?<=\.)\s+') | ForEach-Object { Write-Host "     $_" -ForegroundColor DarkGray }
            Write-Host ""
            Write-Host "   이 detail 이 재시도 프롬프트의 서픽스에 그대로 실린다." -ForegroundColor DarkGray
            Write-Host "   프리픽스는 건드리지 않는다 — 건드리면 캐시가 깨진다." -ForegroundColor DarkGray
        }
    }

    if ($Csv) {
        $dir = Split-Path -Parent $Csv
        if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $records | Export-Csv -Path $Csv -NoTypeInformation -Encoding UTF8
        Write-Host ""
        Write-Host "CSV 저장: $Csv ($($records.Count)행)" -ForegroundColor Green
    }
    Write-Host ""
}
finally {
    Pop-Location
}
