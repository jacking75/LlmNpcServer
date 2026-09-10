#requires -Version 5.1
<#
.SYNOPSIS
    파이프라인. 이 저장소에는 CI 워크플로 파일이 없고 이 스크립트가 그 자리다.

.DESCRIPTION
    <b>워크플로 파일(.github/workflows/*.yml)을 두지 않는다.</b> CI 제공자를 고르지 않았고,
    고르지 않은 채 파일만 두면 "CI 가 있다" 는 거짓 신호가 된다 — 아무도 돌리지 않는
    파이프라인이 녹색으로 보이는 것이 없는 것보다 나쁘다. 대신 파이프라인이 무엇을
    해야 하는가를 여기에 고정한다. 사내 CI 든 GitHub Actions 든 이 한 줄을 부르면 된다.

    1. dotnet build -c Release
    2. dotnet format --verify-no-changes   (스타일 검사)
    3. dotnet test --filter "Category!=Golden&Category!=Gate&Category!=Load"
       Golden 은 LLM 호출·비용이 발생하고,
       Gate  는 실측 산출물이 없으면 실패하도록 만든 항목이며,
       Load  는 수 분이 걸리고 docs/measurements/*.csv 를 덮어쓴다 (CLAUDE.md §5 — 야간).
    4. npc validate        마스터데이터 V1~V13 + 로더 + 파생물 신선도
    5. npc regen --check   파생물이 낡았으면 실패 (F-04)

.PARAMETER Configuration
    빌드 구성. 기본 Release.

.PARAMETER SkipFormat
    스타일 검사를 건너뛴다. 로컬에서 빠르게 돌 때만 쓴다.

.PARAMETER SkipData
    마스터데이터 검증과 파생물 신선도 검사를 건너뛴다. 코드만 고쳤을 때 쓴다.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug -SkipFormat
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $SkipFormat,

    [switch] $SkipData
)

$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot 'NpcServer.sln'
$failed = @()

function Invoke-Step {
    param(
        [Parameter(Mandatory)] [string]   $Name,
        [Parameter(Mandatory)] [string[]] $Arguments
    )

    Write-Host ''
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    Write-Host "    dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        Write-Host "    FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
        $script:failed += $Name
    }
    else {
        Write-Host "    OK" -ForegroundColor Green
    }
}

Invoke-Step -Name 'build' -Arguments @('build', $solution, '-c', $Configuration)

if (-not $SkipFormat) {
    Invoke-Step -Name 'format' -Arguments @('format', $solution, '--verify-no-changes', '--no-restore')
}

Invoke-Step -Name 'test' -Arguments @(
    'test', $solution,
    '-c', $Configuration,
    '--no-build',
    '--filter', 'Category!=Golden&Category!=Gate&Category!=Load'
)

if (-not $SkipData) {
    # 마스터데이터는 코드와 따로 깨진다 — 빌드도 테스트도 통과하는데 기동이 안 되는 상태가 있다.
    $cli = Join-Path $PSScriptRoot 'tools/Npc.Cli'

    Invoke-Step -Name 'validate' -Arguments @(
        'run', '--project', $cli, '-c', $Configuration, '--no-build', '--',
        'validate', '--masterdata', (Join-Path $PSScriptRoot 'masterdata')
    )

    # 파생물이 낡으면 서버는 조용히 낡은 거리표로 돈다. 여기서 멈추는 편이 싸다.
    Invoke-Step -Name 'regen --check' -Arguments @(
        'run', '--project', $cli, '-c', $Configuration, '--no-build', '--',
        'regen', '--check', '--masterdata', (Join-Path $PSScriptRoot 'masterdata')
    )
}

Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host "실패한 단계: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}

Write-Host '전체 통과' -ForegroundColor Green
exit 0
