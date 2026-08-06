#Requires -Version 5.1
<#
.SYNOPSIS
    3장 — 핸드셰이크를 일부러 어긋내 본다.

.DESCRIPTION
    게임서버 대역과 NPC 서버를 소켓으로 붙일 때, 양쪽의 --npcs · --time-scale · --zone ·
    마스터데이터가 하나라도 다르면 <b>연결 시점에 거절한다.</b> 우회 옵션은 없다.

    이 스크립트는 두 회차를 자동으로 돌린다.
      ① 어긋난 회차   게임서버 --npcs N   vs   NPC 서버 --npcs (N-1)   → 거절되어야 한다
      ② 맞춘 회차     양쪽 --npcs N                                    → 붙어야 한다

    WinForms 뷰어가 없어도 된다. 게임서버를 --headless 로 띄우므로 Windows 가 아니어도
    이 실험만은 돌아간다.

.EXAMPLE
    ./samples/ch03_demo/handshake-fail.ps1
    ./samples/ch03_demo/handshake-fail.ps1 -Npcs 200 -Seconds 12
#>
[CmdletBinding()]
param(
    # 게임서버가 들고 있는 NPC 수. NPC 서버는 ① 회차에서 이보다 하나 적게 뜬다.
    [int]$Npcs = 300,

    [int]$TimeScale = 60,
    [int]$LinkPort = 7010,
    [int]$ClientPort = 7020,

    # NPC 서버 HTTP 포트.
    [int]$Port = 5085,

    # 한 회차를 몇 초 지켜볼 것인가.
    [int]$Seconds = 12
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

$gsDll   = Join-Path $root 'testbed/Npc.TestGameServer/bin/Release/net10.0/Npc.TestGameServer.dll'
$hostDll = Join-Path $root 'src/Npc.Host/bin/Release/net10.0/Npc.Host.dll'

foreach ($dll in @($gsDll, $hostDll)) {
    if (-not (Test-Path $dll)) {
        Write-Host "빌드 산출물이 없다: $dll" -ForegroundColor Red
        Write-Host "먼저 dotnet build -c Release 를 돌린다." -ForegroundColor Yellow
        exit 1
    }
}

$logDir = Join-Path $root 'lab/ch03'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

function Start-Proc {
    param([string]$Dll, [string[]]$Arguments, [string]$LogName)

    $out = Join-Path $logDir "$LogName.out.txt"
    $err = Join-Path $logDir "$LogName.err.txt"
    foreach ($f in @($out, $err)) { if (Test-Path $f) { Remove-Item $f -Force } }

    $p = Start-Process -FilePath 'dotnet' `
        -ArgumentList (@($Dll) + $Arguments) `
        -WorkingDirectory $root `
        -NoNewWindow -PassThru `
        -RedirectStandardOutput $out -RedirectStandardError $err

    return [pscustomobject]@{ Proc = $p; Out = $out; Err = $err }
}

function Stop-Proc {
    param($Handle)
    if ($Handle -and $Handle.Proc -and -not $Handle.Proc.HasExited) {
        Stop-Process -Id $Handle.Proc.Id -Force -ErrorAction SilentlyContinue
    }
}

function Read-Log {
    param($Handle)
    # 리다이렉트된 출력은 UTF-8 이다. 5.1 의 기본(ANSI)으로 읽으면 한글이 깨진다.
    $text = ''
    foreach ($f in @($Handle.Out, $Handle.Err)) {
        if (Test-Path $f) { $text += (Get-Content -Path $f -Raw -Encoding UTF8 -ErrorAction SilentlyContinue) }
    }
    return $text
}

function Invoke-Round {
    param([string]$Title, [int]$GsNpcs, [int]$HostNpcs, [bool]$ExpectConnected)

    Write-Host ""
    Write-Host $Title -ForegroundColor Cyan
    Write-Host "   게임서버 --npcs $GsNpcs   ·   NPC 서버 --npcs $HostNpcs"

    $gs = $null
    $np = $null
    try {
        $gs = Start-Proc -Dll $gsDll -LogName "gs_$HostNpcs" -Arguments @(
            '--npcs', $GsNpcs, '--time-scale', $TimeScale,
            '--link-port', $LinkPort, '--client-port', $ClientPort, '--headless')

        # 게임서버가 리스닝을 시작할 때까지 기다린다.
        $ready = $false
        for ($i = 0; $i -lt 40; $i++) {
            Start-Sleep -Milliseconds 300
            $c = Test-NetConnection -ComputerName '127.0.0.1' -Port $LinkPort -InformationLevel Quiet -WarningAction SilentlyContinue
            if ($c) { $ready = $true; break }
        }
        if (-not $ready) {
            Write-Host "   게임서버가 $LinkPort 를 열지 못했다. 로그: $($gs.Out)" -ForegroundColor Red
            return $false
        }

        $np = Start-Proc -Dll $hostDll -LogName "npc_$HostNpcs" -Arguments @(
            '--link', 'tcp', '--gs-port', $LinkPort,
            '--npcs', $HostNpcs, '--time-scale', $TimeScale, '--days', '0',
            '--tier', 'none', '--port', $Port)

        Start-Sleep -Seconds $Seconds

        $log = Read-Log $np
        $connected = $false
        try {
            $s = Invoke-RestMethod -Uri "http://localhost:$Port/status" -TimeoutSec 3
            $connected = ($s.link.commandsFlushed -gt 0)
        }
        catch { $connected = $false }

        $rejected = ($log -match 'RosterMismatch|거절|reject')

        # 증거는 게임서버 쪽 콘솔에 있다 — roster hash 와 매 50틱의 link up/down 이다.
        # NPC 서버는 거절당한 사실을 링크 상태로만 들고 있고 콘솔에 따로 찍지 않는다.
        $gsLog = Read-Log $gs

        Write-Host "   ── 게임서버가 남긴 줄 ─────────────────────────────" -ForegroundColor DarkGray
        $gsLog -split "`r?`n" |
            Where-Object { $_ -match 'roster hash|^game server|^tick ' } |
            Select-Object -First 5 |
            ForEach-Object { Write-Host "   $_" -ForegroundColor DarkGray }

        $linkUp = ($gsLog -match 'link up')

        if ($ExpectConnected) {
            if ($connected -and $linkUp) {
                Write-Host "   결과: link up · 명령이 흐른다" -ForegroundColor Green
                return $true
            }
            Write-Host "   결과: 붙어야 하는데 안 붙었다" -ForegroundColor Red
            return $false
        }
        else {
            if (-not $connected -and -not $linkUp) {
                $why = 'link down 이 계속되고 명령이 하나도 안 나갔다'
                if ($rejected) { $why = '핸드셰이크에서 거절됐다' }
                Write-Host "   결과: 거절됨 — $why" -ForegroundColor Green
                Write-Host "         조용히 도는 대신 연결 시점에 죽는 쪽을 고른 것이다." -ForegroundColor DarkGray
                return $true
            }
            Write-Host "   결과: 어긋났는데도 붙었다 — 이건 문제다" -ForegroundColor Red
            return $false
        }
    }
    finally {
        Stop-Proc $np
        Stop-Proc $gs
        Start-Sleep -Milliseconds 800
    }
}

$a = Invoke-Round -Title "① 어긋난 회차 — NPC 수가 하나 다르다" `
    -GsNpcs $Npcs -HostNpcs ($Npcs - 1) -ExpectConnected $false

$b = Invoke-Round -Title "② 맞춘 회차 — 양쪽이 같다" `
    -GsNpcs $Npcs -HostNpcs $Npcs -ExpectConnected $true

Write-Host ""
if ($a -and $b) {
    Write-Host "실험 통과 — 어긋나면 거절하고, 맞으면 붙는다." -ForegroundColor Green
    Write-Host "로그: $logDir" -ForegroundColor DarkGray
    exit 0
}
Write-Host "실험 실패. 로그를 본다: $logDir" -ForegroundColor Red
exit 1
