#requires -Version 5.1
<#
.SYNOPSIS
    로컬 CI. CLAUDE.md §1 · §5 의 3단계를 그대로 돈다.

.DESCRIPTION
    1. dotnet build -c Release
    2. dotnet format --verify-no-changes   (스타일 검사)
    3. dotnet test --filter "Category!=Golden&Category!=Gate&Category!=Load"
       Golden 은 LLM 호출·비용이 발생하고,
       Gate  는 실측 산출물이 없으면 실패하도록 만든 항목이며,
       Load  는 수 분이 걸리고 docs/measurements/*.csv 를 덮어쓴다 (CLAUDE.md §5 — 야간).

.PARAMETER Configuration
    빌드 구성. 기본 Release.

.PARAMETER SkipFormat
    스타일 검사를 건너뛴다. 로컬에서 빠르게 돌 때만 쓴다.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug -SkipFormat
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $SkipFormat
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

Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host "실패한 단계: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}

Write-Host '전체 통과' -ForegroundColor Green
exit 0
