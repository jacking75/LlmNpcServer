#Requires -Version 5.1
<#
.SYNOPSIS
    17장 — planstore/manifest.json 을 읽는다. 프리베이크 한 회차의 실측 원자료다.

.DESCRIPTION
    manifest.json 은 "이 플랜 스토어가 어떤 조건에서 무슨 비용으로 만들어졌는가" 를 적은 파일이다.
    <b>산출물이 아니라 원자료</b>라 git 에 커밋한다. 이 스크립트는 그것을 사람이 읽게 편다.

    보는 것
      무효화 근거   masterdata_hash · prefix_hash — 지금 것과 같은가
      생성 조건     티어 · 모델 · 온도 · 동시성
      결과          생성 / 폴백 / 재사용 / 검증 단계별 실패
      비용          달러 · 요청당 단가

.EXAMPLE
    ./samples/ch17_prebake/read-manifest.ps1
    ./samples/ch17_prebake/read-manifest.ps1 -Manifest ./planstore/manifest.json
#>
[CmdletBinding()]
param(
    [string]$Manifest = './planstore/manifest.json',
    [string]$Masterdata = './masterdata'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $root

try {
    if (-not (Test-Path $Manifest)) {
        Write-Host "manifest 가 없다: $Manifest" -ForegroundColor Yellow
        Write-Host "프리베이크를 한 번이라도 돌려야 생긴다." -ForegroundColor DarkGray
        exit 1
    }

    $m = Get-Content $Manifest -Raw -Encoding UTF8 | ConvertFrom-Json

    Write-Host ""
    Write-Host "① 이 스토어는 무엇으로 만들어졌나" -ForegroundColor Cyan
    Write-Host ("   티어      {0} · 모델 {1}" -f $m.generated_by.tier, $m.generated_by.model)
    Write-Host ("   온도      {0}" -f [math]::Round($m.generated_by.temperature, 2))
    Write-Host ("   동시성    시작 {0} · 최고 {1} · 첫 429 {2}" -f `
        $m.generated_by.concurrency, $m.generated_by.peak_concurrency, $m.generated_by.first_rate_limit_concurrency)
    Write-Host ("   부분생성  {0}" -f $m.partial)

    Write-Host ""
    Write-Host "② 무효화 근거 — 지금 마스터데이터와 같은가" -ForegroundColor Cyan
    Write-Host ("   manifest masterdata  {0}" -f $m.masterdata_hash.Substring(0, 16))

    $now = $null
    $mdDir = if (Test-Path $Masterdata) { (Resolve-Path $Masterdata).Path } else { $null }
    if ($mdDir) {
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $out = & dotnet run -c Release --no-build --no-launch-profile --project src/Npc.Host -- `
            validate --masterdata $mdDir 2>&1 | ForEach-Object { $_.ToString() }
        $ErrorActionPreference = $prev
        $line = ($out | Where-Object { $_ -match 'content_hash:' } | Select-Object -First 1)
        if ($line -match 'content_hash:\s*(\w+)') { $now = $Matches[1] }
    }

    if ($now) {
        Write-Host ("   현재     masterdata  {0}" -f $now.Substring(0, 16))
        if ($now -eq $m.masterdata_hash) {
            Write-Host "   -> 같다. 이 스토어의 플랜은 유효하다." -ForegroundColor Green
        }
        else {
            Write-Host "   -> 다르다. 마스터데이터가 바뀌었으므로 재생성 대상이다." -ForegroundColor Yellow
        }
    }
    Write-Host ("   prefix_hash          {0}" -f $m.prefix_hash.Substring(0, 16))
    Write-Host "   (프리픽스 해시는 tools/Npc.Prebake --print-prefix 로 지금 값을 본다)" -ForegroundColor DarkGray

    Write-Host ""
    Write-Host "③ 결과" -ForegroundColor Cyan
    $c = $m.counts
    $rows = @(
        [pscustomobject]@{ 항목 = '전체 버킷'; 값 = $c.total },
        [pscustomobject]@{ 항목 = 'LLM 생성'; 값 = $c.generated },
        [pscustomobject]@{ 항목 = '재사용(인접)'; 값 = $c.reused },
        [pscustomobject]@{ 항목 = '폴백으로 대체'; 값 = $c.fallback },
        [pscustomobject]@{ 항목 = '사람이 고정(pinned)'; 값 = $c.pinned }
    )
    $rows | Format-Table -AutoSize

    Write-Host "④ 검증 단계별 결과" -ForegroundColor Cyan
    $v = $m.validation
    $attempts = $v.pass + $v.fail_schema + $v.fail_vocab + $v.fail_coherence + $v.fail_dryrun + $v.fail_call
    @(
        [pscustomobject]@{ 단계 = '통과';            건수 = $v.pass;           '비율 %' = [math]::Round(100.0 * $v.pass / [math]::Max($attempts,1), 1) },
        [pscustomobject]@{ 단계 = '1 Schema 실패';   건수 = $v.fail_schema;    '비율 %' = [math]::Round(100.0 * $v.fail_schema / [math]::Max($attempts,1), 1) },
        [pscustomobject]@{ 단계 = '2 Vocabulary 실패'; 건수 = $v.fail_vocab;   '비율 %' = [math]::Round(100.0 * $v.fail_vocab / [math]::Max($attempts,1), 1) },
        [pscustomobject]@{ 단계 = '3 Coherence 실패'; 건수 = $v.fail_coherence; '비율 %' = [math]::Round(100.0 * $v.fail_coherence / [math]::Max($attempts,1), 1) },
        [pscustomobject]@{ 단계 = '4 DryRun 실패';   건수 = $v.fail_dryrun;    '비율 %' = [math]::Round(100.0 * $v.fail_dryrun / [math]::Max($attempts,1), 1) },
        [pscustomobject]@{ 단계 = '호출 자체 실패';  건수 = $v.fail_call;      '비율 %' = [math]::Round(100.0 * $v.fail_call / [math]::Max($attempts,1), 1) }
    ) | Format-Table -AutoSize

    Write-Host "⑤ 비용" -ForegroundColor Cyan
    Write-Host ("   총액          `${0:N4}" -f $m.cost_usd)
    if ($attempts -gt 0) {
        Write-Host ("   시도당        `${0:N6}  ({1}회 시도)" -f ($m.cost_usd / $attempts), $attempts)
    }
    if ($c.generated -gt 0) {
        Write-Host ("   통과 1건당    `${0:N6}  (실패분까지 포함한 실효 단가)" -f ($m.cost_usd / $c.generated))
    }
    Write-Host ""
    Write-Host "   실패도 돈이 든다 — 통과율을 올리는 것이 곧 비용 절감이다. 18장이 그 이야기다." -ForegroundColor DarkGray
    Write-Host ""
}
finally {
    Pop-Location
}
