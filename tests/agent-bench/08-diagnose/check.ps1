#Requires -Version 5.1
# 과제 8 채점 (E-06).
. "$PSScriptRoot/../lib/Check.ps1"

Test-FileExists -Name 'answer.md 를 남겼다' -Path 'answer.md'

Test-FileContains -Name '근거 1 — 플랜 출처를 적었다' -Path 'answer.md' -Pattern 'planKind'
Test-FileContains -Name '근거 2 — 스텝을 적었다' -Path 'answer.md' -Pattern 'step'
Test-FileContains -Name '근거 3 — requires 플래그를 적었다' -Path 'answer.md' -Pattern 'requires'

Complete-Check -Task '08-diagnose'
