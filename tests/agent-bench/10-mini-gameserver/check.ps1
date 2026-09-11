#Requires -Version 5.1
# 과제 10 채점 (E-06).
. "$PSScriptRoot/../lib/Check.ps1"

$log = Join-Path (Get-RepoRoot) 'bench-out/10/run.log'

Test-FileExists -Name '회차 로그를 남겼다' -Path 'bench-out/10/run.log'

if (Test-Path $log) {
    $text = Get-Content $log -Raw

    Add-Check -Name '핸드셰이크가 수락됐다' `
        -Pass (($text -notlike '*LinkReject*') -and ($text -like '*link*')) `
        -Detail '로그에 LinkReject 가 없다'

    $match = [regex]::Match($text, 'timeouts\s+(\d+)')
    Add-Check -Name 'timeouts 가 0 이다' `
        -Pass ($match.Success -and $match.Groups[1].Value -eq '0') `
        -Detail $(if ($match.Success) { "timeouts $($match.Groups[1].Value)" } else { '로그에 timeouts 줄이 없다' })
}

Test-ChangedFiles -Name '저장소의 다른 파일을 고치지 않았다' -Allowed @('bench-out/*', 'bench-out/**')

Complete-Check -Task '10-mini-gameserver'
