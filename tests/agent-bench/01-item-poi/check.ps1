#Requires -Version 5.1
# 과제 1 채점 (E-06).
. "$PSScriptRoot/../lib/Check.ps1"

Test-Command -Name 'npc validate' -File 'dotnet' -Arguments @(
    'run', '--project', 'tools/Npc.Cli', '--', 'validate') | Out-Null

Test-Command -Name 'npc regen --check (거리표를 다시 만들었는가)' -File 'dotnet' -Arguments @(
    'run', '--project', 'tools/Npc.Cli', '--', 'regen', '--check') | Out-Null

Test-ChangedFiles -Allowed @(
    'masterdata/items.json',
    'masterdata/pois.json',
    'masterdata/poi_distances.bin',
    'masterdata/derived.lock.json')

Complete-Check -Task '01-item-poi'
