#Requires -Version 5.1
<#
.SYNOPSIS
    SBOM(CycloneDX) 생성. G-04.

.DESCRIPTION
    무엇이 들어 있는지 모르면 취약점이 나왔을 때 영향 범위를 알 수 없다.

    <b>도구가 없으면 조용히 넘어가지 않는다.</b> 설치 방법을 알려 주고 비0 으로 끝난다 —
    "SBOM 을 만들었다" 는 거짓 신호가 되면 안 된다.

.PARAMETER Out
    산출 폴더. 기본 artifacts/sbom (gitignore).

.EXAMPLE
    dotnet tool install --global CycloneDX
    .\tools\sbom.ps1
#>
[CmdletBinding()]
param(
    [string] $Out = 'artifacts/sbom'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'NpcServer.sln'
$target = Join-Path $root $Out

if (-not (Get-Command 'dotnet-CycloneDX' -ErrorAction SilentlyContinue)) {
    Write-Host 'CycloneDX 도구가 없다.' -ForegroundColor Red
    Write-Host '  dotnet tool install --global CycloneDX' -ForegroundColor Yellow
    exit 1
}

New-Item -ItemType Directory -Force $target | Out-Null

Write-Host "=== SBOM ===" -ForegroundColor Cyan
& dotnet-CycloneDX $solution --out $target --json

if ($LASTEXITCODE -ne 0) {
    Write-Host "SBOM 생성 실패 (exit $LASTEXITCODE)" -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host "=== 취약점 ===" -ForegroundColor Cyan

# 이쪽은 SDK 기본이라 도구 설치가 필요 없다. 항상 돈다.
& dotnet list $solution package --vulnerable --include-transitive

Write-Host ''
Write-Host "$target 에 SBOM 을 썼다." -ForegroundColor Green
Write-Host '이미지 스캔과 시크릿 스캔은 도구를 고르지 않았다 — docs/security/threat_model.md §5.' -ForegroundColor DarkGray
