#Requires -Version 5.1
# 과제 2 채점 (E-06).
. "$PSScriptRoot/../lib/Check.ps1"

Test-Command -Name 'npc validate (V5·V6·V7·V8)' -File 'dotnet' -Arguments @(
    'run', '--project', 'tools/Npc.Cli', '--', 'validate') | Out-Null

Test-FileContains -Name 'archetypes.json 에 beekeeper 가 있다' `
    -Path 'masterdata/archetypes.json' -Pattern 'beekeeper'

Test-FileContains -Name 'fallback_plans.json 에 폴백이 있다' `
    -Path 'masterdata/fallback_plans.json' -Pattern 'beekeeper'

# <b>코드가 바뀌면 F-05 가 무의미해진다.</b> 아키타입 수는 데이터가 정한다.
Test-ChangedFiles -Name 'C# 코드가 바뀌지 않았다 (F-05)' -Allowed @(
    'masterdata/*',
    'masterdata/**')

Complete-Check -Task '02-archetype'
