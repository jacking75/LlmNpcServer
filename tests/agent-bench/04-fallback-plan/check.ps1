#Requires -Version 5.1
# 과제 4 채점 (E-06).
. "$PSScriptRoot/../lib/Check.ps1"

Test-FileExists -Name 'answer.json 을 남겼다' -Path 'answer.json'

Test-Command -Name '4단 검증 통과' -File 'dotnet' -Arguments @(
    'run', '--project', 'tools/Npc.Cli', '--',
    'plan', 'validate', 'answer.json', '--bucket', 'town_guard@Morning.Peace.Fair') | Out-Null

$path = Join-Path (Get-RepoRoot) 'answer.json'

if (Test-Path $path) {
    $plan = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($plan.plan) { $plan = $plan.plan }

    Add-Check -Name 'loop: true 다' -Pass ($plan.loop -eq $true) -Detail "loop=$($plan.loop)"

    $missing = @($plan.steps | Where-Object { -not $_.PSObject.Properties['timeout_s'] })
    Add-Check -Name '모든 스텝에 timeout_s 가 있다' -Pass ($missing.Count -eq 0) `
        -Detail "$($plan.steps.Count)스텝 중 $($missing.Count)개 누락"
}

Complete-Check -Task '04-fallback-plan'
