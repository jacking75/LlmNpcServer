# 참조 코덱 확인 (B-03).
#
# docs/wire/reference/ 의 파이썬·C++ 코덱이 골든 바이트 벡터와 맞는지 본다.
#
#   .\tools\check_wire_reference.ps1
#
# 이 저장소는 파이썬도 C++ 컴파일러도 **빌드 의존이 아니다.** 없으면 그 항목을
# "미실시" 로 적고 넘어간다 — 없는 것을 통과로 세지 않되, 남의 빌드를 깨지도 않는다.
# 숫자(크기·오프셋) 대조는 도구 없이도 `dotnet test --filter WireReferenceTests` 가 한다.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$codec = Join-Path $root 'docs\wire\reference\npc_wire.py'
$header = Join-Path $root 'docs\wire\reference\npc_wire.h'

$failed = $false
$skipped = @()

Write-Host '== 참조 코덱 확인 ==' -ForegroundColor Cyan

# --- 파이썬: 골든 벡터로 자체 시험 ---------------------------------------

$python = Get-Command python -ErrorAction SilentlyContinue

if ($null -eq $python) {
    $python = Get-Command python3 -ErrorAction SilentlyContinue
}

if ($null -eq $python) {
    Write-Host 'python 없음 — 파이썬 참조 코덱 검증 미실시' -ForegroundColor Yellow
    $skipped += 'python'
}
else {
    $env:PYTHONIOENCODING = 'utf-8'
    & $python.Source $codec
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'python 참조 코덱 실패' -ForegroundColor Red
        $failed = $true
    }
}

# --- C++: 컴파일만 (링크하지 않는다) --------------------------------------
#
# 헤더 온리라 컴파일이 통과하면 static_assert 도 통과한 것이다.

$cxx = $null
foreach ($name in @('g++', 'clang++')) {
    $found = Get-Command $name -ErrorAction SilentlyContinue
    if ($null -ne $found) { $cxx = $found; break }
}

if ($null -eq $cxx) {
    Write-Host 'C++ 컴파일러 없음 — 헤더 컴파일 검증 미실시' -ForegroundColor Yellow
    $skipped += 'c++'
}
else {
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) 'npc_wire_check.cpp'
    @"
#include "$($header -replace '\\', '/')"
int main() { return 0; }
"@ | Out-File -FilePath $temp -Encoding utf8

    & $cxx.Source -std=c++17 -Wall -Wextra -fsyntax-only $temp
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'C++ 헤더 컴파일 실패' -ForegroundColor Red
        $failed = $true
    }
    else {
        Write-Host '참조 코덱(C++): 컴파일 통과.' -ForegroundColor Green
    }

    Remove-Item $temp -ErrorAction SilentlyContinue
}

# --- 판정 ------------------------------------------------------------------

if ($failed) {
    Write-Host ''
    Write-Host '참조 코덱이 골든 벡터와 어긋난다. docs/wire/layout_v2.md 를 먼저 본다.' -ForegroundColor Red
    exit 1
}

if ($skipped.Count -gt 0) {
    Write-Host ''
    Write-Host "미실시: $($skipped -join ', '). 숫자 대조는 WireReferenceTests 가 했다." -ForegroundColor Yellow
}

exit 0
