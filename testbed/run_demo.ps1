<#
.SYNOPSIS
    테스트 베드 데모를 한 줄로 띄운다. docs/20 §12.

.DESCRIPTION
    게임서버 → NPC 서버 → 클라이언트 순으로 세 프로세스를 띄우고,
    Ctrl-C 를 누르거나 셋 중 하나가 끝나면 나머지를 함께 내린다.

    순서가 있는 이유는 게임서버가 먼저 떠 있어야 하기 때문이다 —
    다만 NPC 서버와 클라이언트는 둘 다 재접속을 돌리므로(docs/20 §5.6 · §9.2)
    순서가 어긋나도 죽지는 않는다. 몇 초 늦게 붙을 뿐이다.

    포트: 7010 링크 · 7020 클라이언트 · 5080 NPC 서버 HTTP (docs/20 §12).
    -Shards 2 면 두 벌을 띄운다 — 샤드마다 포트가 +10 씩 밀린다.

.PARAMETER Scenario
    day | siege | blackout. testbed/scenarios/demo_<이름>.jsonl 을 쓴다 (docs/20 §11).
    같은 파일을 양쪽에 준다 — 게임서버는 KillSwitch 줄을 무시하고
    NPC 서버는 KillSwitch 줄만 처리한다 (docs/20 §11.4).

.PARAMETER Npcs
    NPC 수. 게임서버와 NPC 서버에 같은 값이 간다.
    다르면 로스터 해시가 어긋나 핸드셰이크에서 거절된다 — 그것이 의도된 동작이다.

.PARAMETER TimeScale
    시간 압축. 데모 시나리오의 틱 좌표가 60 기준이다(게임 1시간 = 600틱 = 실시간 60초).
    바꾸면 시나리오의 타임라인이 통째로 어긋난다.

.PARAMETER Tier
    NPC 서버의 재계획 티어. 기본 none (LLM 을 부르지 않는다).
    demo_blackout 을 t2 로 돌리면 T2 → T1 차단이 실제로 보인다 (docs/20 §11.4).

.PARAMETER Shards
    띄울 샤드 수 (A-08). 1 이면 오늘과 같다.

    2 이상이면 deploy/shards.json 의 샤드마다 게임서버 + NPC 서버 한 쌍을 띄운다 —
    1 프로세스 = 1 샤드 = 1 링크가 이 설계의 규칙이고, 대역도 그것을 따른다.
    클라이언트는 첫 샤드에만 붙는다 (뷰어는 한 세계만 그린다).

.PARAMETER NoBuild
    dotnet build 를 건너뛴다. 방금 빌드했을 때만 쓴다.

.EXAMPLE
    ./testbed/run_demo.ps1 -Scenario siege

.EXAMPLE
    ./testbed/run_demo.ps1 -Scenario blackout -Tier t2

.EXAMPLE
    ./testbed/run_demo.ps1 -Shards 2
#>
[CmdletBinding()]
param(
    [ValidateSet('day', 'siege', 'blackout')]
    [string] $Scenario = 'day',

    [ValidateRange(1, 5000)]
    [int] $Npcs = 300,

    [ValidateRange(1, 600)]
    [int] $TimeScale = 60,

    [ValidateSet('none', 't1', 't2', 'all')]
    [string] $Tier = 'none',

    [ValidateRange(1, 8)]
    [int] $Shards = 1,

    [switch] $NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 포트는 docs/20 §12 의 것이다. 바꾸려면 세 곳이 같이 바뀌어야 하므로 여기 한 번만 적는다.
$LinkPort = 7010
$ClientPort = 7020
$HttpPort = 5080

$Repo = Split-Path -Parent $PSScriptRoot
$ScenarioPath = Join-Path $PSScriptRoot ('scenarios/demo_{0}.jsonl' -f $Scenario)

# 시작한 순서대로 담는다. 내릴 때는 이 순서를 거꾸로 쓴다 —
# 클라이언트를 먼저 닫아야 "서버가 죽었다" 는 화면을 사람이 안 본다.
$Started = New-Object System.Collections.ArrayList

# 무엇을 보는 회차인지 한 줄. 시나리오 파일 첫 줄의 _comment 와 같은 내용이다.
$Watch = @{
    'day'      = '이벤트가 없다. 시간대가 넘어갈 때 인스펙터의 plan id 가 바뀌는 것을 본다. 약 17분.'
    'siege'    = 'tick 1200 에 존 테두리가 빨개지고 그 존의 NPC 전원이 동시에 갈아탄다. 약 4분.'
    'blackout' = '아무 일도 일어나지 않는 것을 본다. PlanKind 가 bucket → fallback 으로 내려간다. 약 3분.'
}

function Format-Arg {
    param([string] $Value)

    # Start-Process 는 ArgumentList 를 공백으로 이어 붙이기만 한다. 저장소 경로에
    # 공백이 있으면 인자가 둘로 쪼개져서 "모르는 인자다" 로 죽는다.
    if ($Value -match '\s') {
        return '"' + $Value + '"'
    }

    return $Value
}

function Assert-Exe {
    param([string] $Name, [string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw ("{0} 실행 파일이 없다: {1}`n먼저 빌드한다: dotnet build -c Release" -f $Name, $Path)
    }
}

function Start-Part {
    param([string] $Name, [string] $Path, [string[]] $Arguments)

    Write-Host ('→ {0}' -f $Name) -ForegroundColor Cyan

    # 작업 폴더가 저장소 루트여야 ./masterdata · ./planstore 가 풀린다.
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -WorkingDirectory $Repo -PassThru

    [void] $Started.Add([pscustomobject] @{ Name = $Name; Process = $process })
}

function Stop-All {
    # 거꾸로 내린다. 클라이언트 → NPC 서버 → 게임서버.
    for ($i = $Started.Count - 1; $i -ge 0; $i--) {
        $entry = $Started[$i]
        $process = $entry.Process

        if ($null -eq $process -or $process.HasExited) {
            continue
        }

        try {
            # 창이 있는 것은 닫아 준다. 콘솔 앱은 이것을 X 버튼으로 받으므로
            # 우아한 종료(요약 출력)까지는 가지 않는다 — 데모라 그 비용을 치르지 않는다.
            [void] $process.CloseMainWindow()

            if (-not $process.WaitForExit(1500)) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
        }
        catch {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }

        Write-Host ('← {0} 내림' -f $entry.Name) -ForegroundColor DarkGray
    }
}

if (-not (Test-Path -LiteralPath $ScenarioPath)) {
    throw ('시나리오 파일이 없다: {0}' -f $ScenarioPath)
}

if (-not $NoBuild) {
    Write-Host '빌드 중… (-NoBuild 로 건너뛴다)' -ForegroundColor DarkGray

    & dotnet build -c Release (Join-Path $Repo 'NpcServer.sln') | Out-Host

    if ($LASTEXITCODE -ne 0) {
        throw '빌드 실패. 위 오류를 먼저 고친다.'
    }
}

$GameServerExe = Join-Path $Repo 'testbed/Npc.TestGameServer/bin/Release/net10.0/Npc.TestGameServer.exe'
$NpcServerExe = Join-Path $Repo 'src/Npc.Host/bin/Release/net10.0/Npc.Host.exe'
$ClientExe = Join-Path $Repo 'testbed/Npc.TestClient/bin/Release/net10.0-windows/Npc.TestClient.exe'

Assert-Exe '게임서버' $GameServerExe
Assert-Exe 'NPC 서버' $NpcServerExe
Assert-Exe '클라이언트' $ClientExe

Write-Host ''
Write-Host ('데모: {0}   npcs {1}   time-scale {2}   tier {3}   샤드 {4}' -f $Scenario, $Npcs, $TimeScale, $Tier, $Shards) -ForegroundColor White
Write-Host ('볼 것: {0}' -f $Watch[$Scenario]) -ForegroundColor Gray
Write-Host ''

try {
    # A-08 — 샤드마다 게임서버 + NPC 서버 한 쌍. 1 프로세스 = 1 샤드 = 1 링크다.
    # 포트는 샤드마다 +10 밀어 겹치지 않게 한다.
    for ($shard = 0; $shard -lt $Shards; $shard++) {
        $offset = $shard * 10
        $shardId = if ($Shards -eq 1) { 0 } else { $shard + 1 }
        $label = if ($Shards -eq 1) { '' } else { (' (샤드 {0})' -f $shardId) }

        $gsArgs = @(
            '--npcs', $Npcs,
            '--time-scale', $TimeScale,
            '--link-port', ($LinkPort + $offset),
            '--client-port', ($ClientPort + $offset),
            '--scenario', (Format-Arg $ScenarioPath)
        )

        if ($shardId -ne 0) { $gsArgs += @('--shard', $shardId) }

        Start-Part ('게임서버' + $label) $GameServerExe $gsArgs

        Start-Sleep -Milliseconds 800

        # --dev-control 이 있어야 제어 패널의 킬스위치 버튼이 먹는다 (docs/20 §11.4).
        # 없으면 라우트 자체가 없어서 404 이고, 화면에는 "미연결" 로만 나온다.
        #
        # --scenario 를 여기에도 준다. NPC 서버는 KillSwitch 줄만 본다 —
        # 이벤트 주입은 게임서버의 일이고, 여기서 같이 내면 세계가 두 번 밀린다.
        $npcArgs = @(
            '--link', 'tcp',
            '--gs-port', ($LinkPort + $offset),
            '--npcs', $Npcs,
            '--time-scale', $TimeScale,
            '--days', '0',
            '--tier', $Tier,
            '--planstore', './planstore',
            '--port', ($HttpPort + $offset),
            '--dev-control',
            '--scenario', (Format-Arg $ScenarioPath)
        )

        if ($shardId -ne 0) { $npcArgs += @('--shard', $shardId) }

        Start-Part ('NPC 서버' + $label) $NpcServerExe $npcArgs

        Start-Sleep -Milliseconds 800
    }

    Start-Part '클라이언트' $ClientExe @(
        '--host', '127.0.0.1',
        '--port', $ClientPort,
        '--npc-http', ('http://localhost:{0}' -f $HttpPort)
    )

    Write-Host ''
    Write-Host 'Ctrl-C 로 셋 다 내린다. 창을 하나 닫아도 나머지가 따라 내려간다.' -ForegroundColor Green
    Write-Host ('대시보드: http://localhost:{0}/dashboard' -f $HttpPort) -ForegroundColor DarkGray
    Write-Host ''

    while ($true) {
        Start-Sleep -Milliseconds 500

        foreach ($entry in $Started) {
            if ($entry.Process.HasExited) {
                Write-Host ''
                Write-Host ('{0} 가 끝났다 (exit {1}). 나머지를 내린다.' -f $entry.Name, $entry.Process.ExitCode) -ForegroundColor Yellow

                # finally 로 간다. Ctrl-C 도 같은 길이다.
                return
            }
        }
    }
}
finally {
    Stop-All
}
