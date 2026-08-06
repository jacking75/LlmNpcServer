#Requires -Version 5.1
<#
.SYNOPSIS
    16장 — LLM 엔진 설정을 읽고, 지금 무엇을 켤 수 있는지 판정한다.

.DESCRIPTION
    appsettings.Llm.json 은 <b>API 키를 담지 않는다.</b> 환경변수 <b>이름</b>만 적는다.
    이 스크립트는 그 이름들을 훑어 어느 엔진이 지금 쓸 수 있는지 알려 준다.

    그리고 --tier none 회차를 한 번 돌려 <b>비용 패널이 꺼져 있는 것</b>을 보여 준다.
    16장의 요지는 "티어를 켜면 무엇이 늘어나고 무엇이 안 늘어나는가" 이고,
    안 늘어나는 쪽(틱 p99 · 틱당 할당)을 먼저 재 두는 것이 비교의 출발점이다.

.EXAMPLE
    ./samples/ch16_tier/check-engines.ps1
#>
[CmdletBinding()]
param(
    [string]$Config = './appsettings.Llm.json',
    [int]$Npcs = 200,
    [switch]$SkipRun,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $root

try {
    if (-not (Test-Path $Config)) {
        Write-Host "설정 파일이 없다: $Config" -ForegroundColor Red
        exit 1
    }

    $j = Get-Content $Config -Raw -Encoding UTF8 | ConvertFrom-Json

    Write-Host ""
    Write-Host "① 등록된 엔진" -ForegroundColor Cyan

    $engines = @()
    foreach ($e in $j.engines) {
        $keyName = $e.api_key_env
        $hasKey = $false
        if ($keyName) {
            $hasKey = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($keyName))
        }
        # 키 이름이 없는 엔진은 로컬 엔드포인트다 — 키 대신 서버가 떠 있어야 한다.
        $local = -not $keyName

        $engines += [pscustomobject]@{
            id     = $e.id
            연결   = $(if ($local) { '로컬 엔드포인트' } else { '외부 API' })
            모델   = $e.model
            키변수 = $(if ($keyName) { $keyName } else { '(없음)' })
            상태   = $(if ($local) { '서버가 떠 있어야 함' } elseif ($hasKey) { '키 있음' } else { '키 없음' })
        }
    }

    if ($engines.Count -eq 0) {
        Write-Host "   engines 배열을 못 읽었다. 설정 스키마가 바뀌었는지 본다." -ForegroundColor Yellow
    }
    else {
        $engines | Format-Table -AutoSize
    }

    if ($j.preferred) {
        Write-Host "② 우선순위 (앞에서부터 키가 채워진 첫 엔진을 쓴다)" -ForegroundColor Cyan
        $i = 1
        foreach ($p in $j.preferred) { Write-Host ("   {0}. {1}" -f $i++, $p) }
        Write-Host ""
    }

    $usable = @($engines | Where-Object { $_.상태 -eq '키 있음' })
    Write-Host "③ 판정" -ForegroundColor Cyan
    if ($usable.Count -gt 0) {
        Write-Host ("   키가 채워진 외부 엔진 {0}개. --tier t2 를 켤 수 있다." -f $usable.Count) -ForegroundColor Green
    }
    else {
        Write-Host "   키가 채워진 외부 엔진이 없다." -ForegroundColor Yellow
        Write-Host "   16~18장은 읽기 전용으로 진행한다 — 책에 캡처된 출력이 실려 있다." -ForegroundColor DarkGray
        Write-Host "   키를 넣으려면 (셸에만, 파일에는 쓰지 않는다):" -ForegroundColor DarkGray
        Write-Host '     $env:OPENROUTER_API_KEY = "..."' -ForegroundColor DarkGray
    }

    if ($SkipRun) { exit 0 }

    # ── 기준선 — 티어를 켜기 전 값 ────────────────────────────────────────
    Write-Host ""
    Write-Host "④ 기준선 (--tier none)" -ForegroundColor Cyan

    $cli = @('run', '-c', 'Release', '--no-launch-profile', '--project', 'src/Npc.Host')
    if ($NoBuild) { $cli += '--no-build' }
    $cli += @('--', '--loopback', '--no-llm', '--max-speed', '--no-dashboard',
              '--npcs', $Npcs, '--time-scale', 600, '--days', 1)

    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $out = & dotnet @cli 2>&1 | ForEach-Object { $_.ToString() }
    $ErrorActionPreference = $prev

    ($out | Where-Object { $_ -match '^tick p50 |^ticks \d' }) | ForEach-Object { Write-Host "   $_" -ForegroundColor DarkGray }

    Write-Host ""
    Write-Host "   이 두 줄을 적어 둔다. 티어를 켠 뒤 다시 재서 견준다." -ForegroundColor DarkGray
    Write-Host "   같아야 하는 것: 틱 p99 · bytes/tick · overruns" -ForegroundColor DarkGray
    Write-Host "   달라져야 하는 것: llm 호출 수 · /metrics 의 cost 패널 · 일부 NPC 의 planKind" -ForegroundColor DarkGray
    Write-Host ""
}
finally {
    Pop-Location
}
