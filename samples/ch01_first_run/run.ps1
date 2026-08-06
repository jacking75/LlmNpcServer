#Requires -Version 5.1
<#
.SYNOPSIS
    1장 — 5분 만에 NPC 를 살린다.

.DESCRIPTION
    이 스크립트가 하는 일은 명령 한 줄을 대신 쳐 주는 것뿐이다.
    책에 적힌 그 한 줄을 그대로 쳐도 결과는 같다.

    돌리기 전에 옵션이 각각 무엇을 뜻하는지 화면에 적어 주고,
    끝난 뒤에는 종료 요약에서 무엇을 봐야 하는지 짚어 준다.

.EXAMPLE
    ./samples/ch01_first_run/run.ps1
    ./samples/ch01_first_run/run.ps1 -Npcs 100 -Days 2
#>
[CmdletBinding()]
param(
    [int]$Npcs = 20,
    [int]$TimeScale = 600,
    [int]$Days = 1,
    [int]$Port = 5080,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $root

try {
    $realSeconds = [math]::Round(86400.0 * $Days / $TimeScale, 0)
    $ticks       = [math]::Round($realSeconds * 10, 0)

    Write-Host ""
    Write-Host "1장 — 첫 실행" -ForegroundColor Cyan
    Write-Host "───────────────────────────────────────────────────────────────"
    Write-Host "  --loopback           게임서버 대역(Npc.Sim)을 같은 프로세스에 붙인다"
    Write-Host "  --npcs $Npcs              NPC 수"
    Write-Host "  --time-scale $TimeScale     실시간 1초 = 게임 $TimeScale 초. 1틱(0.1초) = 게임 $($TimeScale / 10)초"
    Write-Host "  --days $Days               게임 $Days 일을 돌고 스스로 끝난다 (약 $realSeconds 초 · $ticks 틱)"
    Write-Host "  --no-llm             LLM 을 한 번도 부르지 않는다 (--tier none 의 별칭)"
    Write-Host "  --port $Port           대시보드 · 메트릭 포트"
    Write-Host "───────────────────────────────────────────────────────────────"
    Write-Host ""
    Write-Host "  돌기 시작하면 브라우저로 열어 본다:" -ForegroundColor Cyan
    Write-Host "    http://localhost:$Port/dashboard"
    Write-Host "    http://localhost:$Port/status"
    Write-Host ""

    # $args 는 자동 변수라 덮어쓰면 안 된다. 이름을 따로 준다.
    $cli = @(
        'run', '-c', 'Release', '--no-launch-profile', '--project', 'src/Npc.Host'
    )
    if ($NoBuild) { $cli += '--no-build' }
    $cli += @(
        '--',
        '--loopback',
        '--npcs', $Npcs,
        '--time-scale', $TimeScale,
        '--days', $Days,
        '--no-llm',
        '--port', $Port
    )

    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & dotnet @cli 2>&1 | ForEach-Object { $_.ToString() }
    }
    finally {
        $ErrorActionPreference = $prev
    }

    $out | ForEach-Object { Write-Host $_ }

    # ── 종료 요약에서 무엇을 봐야 하나 ────────────────────────────────────
    $text = ($out | Where-Object { $_ -match '^tick p50 |^ticks \d' }) -join ' '
    if (-not $text) { return }

    function Field {
        param([string]$Pattern)
        if ($text -match $Pattern) { return $Matches[1] }
        return '?'
    }

    $commands = Field 'commands (\d+)'
    $llm      = Field 'llm (\d+)'
    $p99      = Field 'p99 ([\d.]+)ms'
    $bytes    = Field 'bytes/tick (\d+)'

    Write-Host ""
    Write-Host "여기서 볼 것 세 줄" -ForegroundColor Green
    Write-Host "───────────────────────────────────────────────────────────────"
    Write-Host "  llm $llm 인데 commands $commands" -ForegroundColor Green
    Write-Host "     LLM 을 한 번도 안 불렀는데 명령이 $commands 건 나갔다."
    Write-Host "     플랜은 이미 만들어져 있었고, 런타임은 그것을 실행만 했다."
    Write-Host "     이 한 줄이 'LLM 은 게임 크리티컬 패스에 없다' 의 실물이다."
    Write-Host ""
    Write-Host "  틱 p99 $p99 ms" -ForegroundColor Green
    Write-Host "     예산은 20ms 다. 한 틱(100ms) 의 20% 를 넘지 않기로 한 값이다."
    Write-Host ""
    Write-Host "  bytes/tick $bytes" -ForegroundColor Green
    Write-Host "     틱 루프가 틱마다 힙에 할당한 바이트. 0 이어야 한다."
    Write-Host "───────────────────────────────────────────────────────────────"
    Write-Host ""
}
finally {
    Pop-Location
}
