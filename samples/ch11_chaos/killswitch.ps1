#Requires -Version 5.1
<#
.SYNOPSIS
    11장 — 킬스위치로 T2 → T1 → PlanStore 를 차례로 끊는다.

.DESCRIPTION
    시나리오 C("만들 수 있는가"가 아니라 "끊겨도 도는가")를 손으로 재현한다.

      T2         외부 LLM 차단     남는 것: 캐시 + T1
      T1         로컬 LLM 차단     남는 것: 캐시만
      PlanStore  플랜 캐시 차단    남는 것: 아키타입 폴백 40개

    <b>볼 것은 "아무 일도 일어나지 않는다" 이다.</b> NPC 는 계속 움직이고 크래시가 없다.
    추적기의 planKind 가 bucket → fallback 으로 내려가는 것이 성공 신호다.

    POST /control/killswitch 는 --dev-control 로 띄웠을 때만 <b>라우트가 등록된다.</b>
    없으면 401 이 아니라 404 다 — 조건부 인증이 아니라 조건부 등록이기 때문이다.

.EXAMPLE
    ./samples/ch11_chaos/killswitch.ps1
    ./samples/ch11_chaos/killswitch.ps1 -Npc 7 -Port 5093
#>
[CmdletBinding()]
param(
    [int]$Npcs = 200,
    [int]$Npc = 7,
    [int]$TimeScale = 60,
    [int]$Port = 5093,
    [string]$Masterdata = './masterdata',

    # 각 단계 사이에 몇 초 지켜볼 것인가.
    [int]$StepSeconds = 6
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$base = "http://127.0.0.1:$Port"

Push-Location $root
try {
    $md  = (Resolve-Path $Masterdata).Path
    $dll = Join-Path $root 'src/Npc.Host/bin/Release/net10.0/Npc.Host.dll'
    if (-not (Test-Path $dll)) { throw "빌드 산출물이 없다. dotnet build -c Release 를 먼저 돌린다." }

    $logDir = Join-Path $root 'lab/ch11'
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $log = Join-Path $logDir 'host.out.txt'
    $err = Join-Path $logDir 'host.err.txt'

    Write-Host ""
    Write-Host "① NPC 서버 기동 (--dev-control)" -ForegroundColor Cyan
    $p = Start-Process -FilePath 'dotnet' -PassThru -NoNewWindow `
        -WorkingDirectory $root -RedirectStandardOutput $log -RedirectStandardError $err `
        -ArgumentList @($dll, '--loopback', '--no-llm', '--dev-control',
                        '--masterdata', $md, '--npcs', $Npcs,
                        '--time-scale', $TimeScale, '--days', '0', '--port', $Port)

    try {
        $ready = $false
        for ($i = 0; $i -lt 40; $i++) {
            Start-Sleep -Milliseconds 500
            try { Invoke-RestMethod "$base/status" -TimeoutSec 2 | Out-Null; $ready = $true; break } catch { }
        }
        if (-not $ready) { throw "서버가 안 떴다. 로그: $log" }

        # 한 마리만 보면 그 NPC 의 버킷이 원래 비어 있을 수 있다.
        # 표본 32마리의 planKind 분포를 세면 캐시가 끊기는 순간이 눈에 보인다.
        function Get-KindMix {
            $bucket = 0; $fallback = 0; $other = 0
            for ($i = 0; $i -lt 32; $i++) {
                try {
                    $t = Invoke-RestMethod "$base/npc/$i" -TimeoutSec 3
                    if (-not $t.found) { continue }
                    switch -Wildcard ($t.planKind) {
                        'bucket'      { $bucket++ }
                        'fallback'    { $fallback++ }
                        default       { $other++ }
                    }
                }
                catch { }
            }
            return "bucket $bucket / fallback $fallback" + $(if ($other) { " / 기타 $other" } else { '' })
        }

        function Show-State {
            param([string]$Label)

            $s = Invoke-RestMethod "$base/status" -TimeoutSec 3
            $mix = Get-KindMix

            Write-Host ("   {0,-24} tick {1,6} · 명령 {2,7} · 스텝 {3,7} · 표본32 {4}" -f `
                $Label, $s.tick, $s.commandsEmitted, $s.stepsAdvanced, $mix)

            return [pscustomobject]@{
                단계 = $Label; 틱 = $s.tick; 명령 = $s.commandsEmitted
                스텝 = $s.stepsAdvanced; '표본32 planKind' = $mix
            }
        }

        $rows = @()
        Write-Host ""
        Write-Host "② 단계별로 끊는다" -ForegroundColor Cyan
        $rows += Show-State '차단 전'

        foreach ($target in @('T2', 'T1', 'PlanStore')) {
            try {
                $r = Invoke-RestMethod -Method Post -Uri "$base/control/killswitch?target=$target" -TimeoutSec 5
                Write-Host ("   -> {0} 차단 (fired={1})" -f $r.target, $r.fired) -ForegroundColor Yellow
            }
            catch {
                Write-Host "   -> $target 차단 실패. --dev-control 없이 떴는지 확인한다 (404)." -ForegroundColor Red
                continue
            }
            Start-Sleep -Seconds $StepSeconds
            $rows += Show-State "$target 차단 후"
        }

        Write-Host ""
        Write-Host "③ 판정" -ForegroundColor Cyan
        $rows | Format-Table -AutoSize

        $first = $rows[0]; $last = $rows[-1]
        $moved = ($last.스텝 -gt $first.스텝)

        Write-Host ""
        if ($moved) {
            Write-Host ("   전부 끊은 뒤에도 스텝이 {0} → {1} 로 계속 늘었다." -f $first.스텝, $last.스텝) -ForegroundColor Green
            Write-Host "   NPC 는 멈추지 않았다. 이것이 이 장의 성공 신호다." -ForegroundColor Green
        }
        else {
            Write-Host "   스텝이 안 늘었다. 로그를 본다: $log" -ForegroundColor Red
        }
        Write-Host ("   planKind 표본: {0} → {1}" -f $first."표본32 planKind", $last."표본32 planKind") -ForegroundColor DarkGray
    }
    finally {
        if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
}
finally {
    Pop-Location
}

