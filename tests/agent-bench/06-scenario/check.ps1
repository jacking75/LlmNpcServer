#Requires -Version 5.1
# 과제 6 채점 (E-06).
. "$PSScriptRoot/../lib/Check.ps1"

Test-FileExists -Name 'scenarios/storm.jsonl 을 남겼다' -Path 'scenarios/storm.jsonl'

$path = Join-Path (Get-RepoRoot) 'scenarios/storm.jsonl'

if (Test-Path $path) {
    $events = @()
    $bad = 0

    foreach ($line in Get-Content $path -Encoding UTF8) {
        if (-not $line.Trim()) { continue }
        try { $events += ($line | ConvertFrom-Json) } catch { $bad++ }
    }

    Add-Check -Name '모든 줄이 JSON 이다' -Pass ($bad -eq 0) -Detail "$bad 줄이 깨졌다"

    $weather = @($events | Where-Object { $_.event -eq 'WeatherChanged' })
    Add-Check -Name 'WeatherChanged 가 2건이다' -Pass ($weather.Count -eq 2) `
        -Detail "$($weather.Count)건"

    $ticks = @($weather | ForEach-Object { $_.at_tick } | Sort-Object)
    Add-Check -Name '배속 환산이 맞다 (09:00=180 · 12:00=360)' `
        -Pass ($ticks.Count -eq 2 -and $ticks[0] -eq 180 -and $ticks[1] -eq 360) `
        -Detail "at_tick = $($ticks -join ', ')"

    $zones = (Get-Content -Encoding UTF8 (Join-Path (Get-RepoRoot) 'masterdata/zones.json') -Raw | ConvertFrom-Json).zones.id
    $unknown = @($weather | Where-Object { $zones -notcontains $_.zone })
    Add-Check -Name '존 id 가 zones.json 의 것이다' -Pass ($unknown.Count -eq 0) `
        -Detail "$($unknown.Count)건이 모르는 존"
}

Complete-Check -Task '06-scenario'
