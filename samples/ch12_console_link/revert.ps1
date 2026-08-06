#Requires -Version 5.1
<#
.SYNOPSIS
    12장 — 콘솔 링크 실습을 되돌린다.

.EXAMPLE
    ./samples/ch12_console_link/revert.ps1 -Yes
#>
[CmdletBinding()]
param([switch]$Yes)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

Push-Location $root
try {
    $added   = Join-Path $root 'src/Npc.Gateway/ConsoleGameServerLink.cs'
    $tracked = @(& git status --porcelain -- src | Where-Object { $_ })

    if ($tracked.Count -eq 0 -and -not (Test-Path $added)) {
        Write-Host "되돌릴 변경이 없다." -ForegroundColor DarkGray
        exit 0
    }

    Write-Host ""
    Write-Host "되돌릴 것" -ForegroundColor Cyan
    $tracked | ForEach-Object { Write-Host "   $_" -ForegroundColor DarkGray }

    if (-not $Yes) {
        Write-Host ""
        Write-Host "정말 되돌리려면 -Yes 를 붙여 다시 실행한다." -ForegroundColor Yellow
        exit 1
    }

    & git restore src 2>&1 | Out-Null
    if (Test-Path $added) { Remove-Item $added -Force }

    Write-Host ""
    Write-Host "파일 복원 완료. 다시 빌드한다." -ForegroundColor Green
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & dotnet build -c Release 2>&1 | Select-Object -Last 4 | ForEach-Object { Write-Host "   $($_.ToString())" -ForegroundColor DarkGray }
    $ErrorActionPreference = $prev

    Write-Host ""
    Write-Host "되돌리기 완료." -ForegroundColor Green
}
finally {
    Pop-Location
}
