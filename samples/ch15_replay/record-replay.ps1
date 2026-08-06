#Requires -Version 5.1
<#
.SYNOPSIS
    15장 — 같은 회차를 두 번 돌려 결과가 바이트 단위로 같은지 본다. 그리고 기록·재생.

.DESCRIPTION
    네 회차를 돌린다.

      A  --loopback                     기준
      B  --loopback (A 와 같은 인자)     A 와 <b>완전히 같아야</b> 한다
      C  --link record --trace <파일>    링크 이벤트를 jsonl 로 기록하며 돈다
      D  --link replay  --trace <파일>   그 기록을 되민다. C 와 같아야 한다

    비교 대상은 종료 요약 두 줄 전체다. 틱·이벤트·명령·스텝·타임아웃·인터럽트가
    <b>하나라도 다르면</b> 결정론이 깨진 것이다.

    소켓 경로(--link tcp)에서는 이 성질이 성립하지 않는다. 명령이 몇 틱 늦게 도착해
    스텝 경계가 바뀌기 때문이고, <b>결함이 아니라 사실</b>이다.

.EXAMPLE
    ./samples/ch15_replay/record-replay.ps1
    ./samples/ch15_replay/record-replay.ps1 -Npcs 500 -Days 2
#>
[CmdletBinding()]
param(
    [int]$Npcs = 200,
    [int]$TimeScale = 600,
    [int]$Days = 1,
    [int]$Seed = 20260725,
    [string]$Masterdata = './masterdata',
    [string]$TraceDir = './lab/ch15',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $root

try {
    $md = (Resolve-Path $Masterdata).Path
    New-Item -ItemType Directory -Path $TraceDir -Force | Out-Null
    $trace = Join-Path ((Resolve-Path $TraceDir).Path) 'link.jsonl'

    function Invoke-Run {
        param([string[]]$Extra)

        $cli = @('run', '-c', 'Release', '--no-launch-profile', '--project', 'src/Npc.Host')
        if ($NoBuild) { $cli += '--no-build' }
        $cli += @('--', '--no-llm', '--max-speed', '--no-dashboard',
                  '--masterdata', $md, '--npcs', $Npcs, '--time-scale', $TimeScale,
                  '--days', $Days, '--seed', $Seed) + $Extra

        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $out = & dotnet @cli 2>&1 | ForEach-Object { $_.ToString() }
        $ErrorActionPreference = $prev

        return (($out | Where-Object { $_ -match '^ticks \d' }) -join ' ').Trim()
    }

    Write-Host ""
    Write-Host "① 같은 인자로 두 번 (루프백)" -ForegroundColor Cyan
    Write-Host "   A 실행중…" -ForegroundColor DarkGray
    $a = Invoke-Run -Extra @('--loopback')
    Write-Host "   B 실행중…" -ForegroundColor DarkGray
    $b = Invoke-Run -Extra @('--loopback')

    Write-Host ""
    Write-Host "   A  $a" -ForegroundColor DarkGray
    Write-Host "   B  $b" -ForegroundColor DarkGray
    Write-Host ""
    if ($a -eq $b -and $a) {
        Write-Host "   일치 — 같은 입력에 같은 출력이다." -ForegroundColor Green
    }
    else {
        Write-Host "   불일치! 결정론이 깨졌다." -ForegroundColor Red
    }

    Write-Host ""
    Write-Host "② 기록하며 한 번 (--link record)" -ForegroundColor Cyan
    if (Test-Path $trace) { Remove-Item $trace -Force }
    $c = Invoke-Run -Extra @('--link', 'record', '--trace', $trace)

    if (-not (Test-Path $trace)) {
        Write-Host "   기록 파일이 안 생겼다: $trace" -ForegroundColor Red
    }
    else {
        $size  = [math]::Round((Get-Item $trace).Length / 1KB, 1)
        $lines = (Get-Content $trace -ReadCount 0).Count
        Write-Host ("   {0}  ·  {1} KB  ·  {2:N0} 줄" -f (Split-Path $trace -Leaf), $size, $lines)
    }
    Write-Host "   C  $c" -ForegroundColor DarkGray

    Write-Host ""
    Write-Host "③ 되밀어 한 번 (--link replay)" -ForegroundColor Cyan
    $d = Invoke-Run -Extra @('--link', 'replay', '--trace', $trace)
    Write-Host "   D  $d" -ForegroundColor DarkGray

    Write-Host ""
    Write-Host "④ 판정" -ForegroundColor Cyan

    $rows = @(
        [pscustomobject]@{ 비교 = 'A vs B  (루프백 두 번)'; 결과 = $(if ($a -eq $b -and $a) { '일치' } else { '불일치' }) },
        [pscustomobject]@{ 비교 = 'C vs D  (기록 vs 재생)'; 결과 = $(if ($c -eq $d -and $c) { '일치' } else { '불일치' }) },
        [pscustomobject]@{ 비교 = 'A vs C  (루프백 vs 기록)'; 결과 = $(if ($a -eq $c -and $a) { '일치' } else { '불일치' }) }
    )
    $rows | Format-Table -AutoSize

    Write-Host "   기록 파일: $trace" -ForegroundColor DarkGray
    Write-Host "   Npc.Narrate 로 사람 말로 풀어 볼 수 있다:" -ForegroundColor DarkGray
    Write-Host ("     dotnet run -c Release --project tools/Npc.Narrate -- --trace {0} --npc 7 --time-scale {1} --population {2}" -f $trace, $TimeScale, $Npcs)
    Write-Host ""
}
finally {
    Pop-Location
}
