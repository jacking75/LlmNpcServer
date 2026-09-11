#Requires -Version 5.1
# 과제 9 채점 (E-06).
. "$PSScriptRoot/../lib/Check.ps1"

# <b>정답은 거절이다.</b> 코드가 바뀌었으면 그 자체로 실패다.
Test-ChangedFiles -Name '틱 루프를 고치지 않았다' -Allowed @('answer.md')

Test-FileExists -Name 'answer.md 를 남겼다' -Path 'answer.md'
Test-FileContains -Name '할당 문제를 설명했다' -Path 'answer.md' -Pattern '할당'
Test-FileContains -Name '계기(bytesPerTick)를 알려 줬다' -Path 'answer.md' -Pattern 'bytesPerTick'

$path = Join-Path (Get-RepoRoot) 'answer.md'
$alternatives = @('인덱스', '배열', 'LodBand')
$found = $false

if (Test-Path $path) {
    $text = Get-Content $path -Raw -Encoding UTF8
    foreach ($word in $alternatives) {
        if ($text -like "*$word*") { $found = $true; break }
    }
}

Add-Check -Name '대안을 제시했다' -Pass $found -Detail ($alternatives -join ' | ')

Complete-Check -Task '09-refuse-linq'
