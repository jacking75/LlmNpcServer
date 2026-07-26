<#
.SYNOPSIS
    재계획 가중치 A/B. docs/14 §2 튜닝 절차 · T4-17.

.DESCRIPTION
    docs/14 §2 표의 4세트(A 근접우선 / B 기본 / C 이탈우선 / D 균등)를
    시나리오 A(살아있는 마을)로 각각 돌려 비교한다.

    감으로 튜닝하면 재현이 안 된다 (docs/14 §10). 그래서:
      · 세트 정의는 src/Npc.Planning/ReplanScorer.cs 의 Weights.AbSets 하나뿐이다
      · 실 LLM 을 쓰지 않는다 — 지연·성공률이 회차마다 달라 가중치가 아니라 엔진을 재게 된다.
        컴파일러 자리에 결정론 드레이너를 두고 예산 상한과 실측 지연(51틱)만 흉내낸다

    소요는 4세트 합쳐 약 2분이다.

.PARAMETER Out
    산출 경로. 기본 docs/measurements/W10_weights.md

.EXAMPLE
    ./tools/run_weight_ab.ps1
#>
[CmdletBinding()]
param(
    [string] $Out
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $repo

if (-not $Out) {
    $Out = Join-Path $repo 'docs/measurements/W10_weights.md'
}

$env:NPC_WEIGHTS_OUT = $Out

Write-Host "산출: $Out"
Write-Host ''

# Release 로 잰다. Debug 는 틱 지연이 몇 배 나와 세트 간 비교가 흐려진다.
dotnet build -c Release --nologo | Out-Null

$started = Get-Date

dotnet test -c Release --no-build --nologo `
    --filter 'FullyQualifiedName~WeightAbTests.Weights_RunsAbMatrix'

$exit = $LASTEXITCODE
$elapsed = (Get-Date) - $started

Write-Host ''
Write-Host ("소요: {0:mm\:ss}" -f $elapsed)

if (Test-Path $Out) {
    # 선정 결과를 바로 보여 준다 — 파일을 열지 않아도 무엇이 뽑혔는지 알아야 한다.
    $chosen = Select-String -Path $Out -Pattern '^## 선정' | Select-Object -First 1

    if ($chosen) {
        Write-Host ''
        Write-Host $chosen.Line -ForegroundColor Green
        Write-Host 'Weights.Default 가 이 세트인지 확인한다 (src/Npc.Planning/ReplanScorer.cs).'
    }
}
else {
    Write-Host "warn: $Out 이 생기지 않았다." -ForegroundColor Yellow
}

exit $exit
