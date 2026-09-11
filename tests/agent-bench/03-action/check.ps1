#Requires -Version 5.1
# 과제 3 채점 (E-06).
. "$PSScriptRoot/../lib/Check.ps1"

Test-Command -Name 'npc validate' -File 'dotnet' -Arguments @(
    'run', '--project', 'tools/Npc.Cli', '--', 'validate') | Out-Null

Test-FileExists -Name 'answer.md 를 남겼다' -Path 'answer.md'
Test-FileContains -Name '프리픽스 변경을 보고했다' -Path 'answer.md' -Pattern '프리픽스'
Test-FileContains -Name '플랜 스토어 무효화를 보고했다' -Path 'answer.md' -Pattern '플랜 스토어'

$actions = Get-Content -Encoding UTF8 (Join-Path (Get-RepoRoot) 'masterdata/actions.json') -Raw | ConvertFrom-Json
Add-Check -Name '액션이 40개를 넘지 않았다' -Pass ($actions.actions.Count -le 40) `
    -Detail "$($actions.actions.Count)/40"

Complete-Check -Task '03-action'
