#Requires -Version 5.1
# 과제 5 채점 (E-06).
. "$PSScriptRoot/../lib/Check.ps1"

Test-Command -Name 'npc validate' -File 'dotnet' -Arguments @(
    'run', '--project', 'tools/Npc.Cli', '--', 'validate') | Out-Null

# <b>없는 필드를 만들어 넣지 않았는가.</b> 로더가 조용히 무시하므로 검증으로는 안 걸린다.
Test-FileContains -Name 'cooldown_s 를 만들어 넣지 않았다' `
    -Path 'masterdata/interrupts.json' -Pattern 'cooldown' -Absent

Test-ChangedFiles -Allowed @('masterdata/interrupts.json')

Complete-Check -Task '05-interrupt'
