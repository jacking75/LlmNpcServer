#Requires -Version 5.1
<#
.SYNOPSIS
    확인 3단 — 검증 → 실행 → 판정. 활용 실습서의 모든 장이 이 스크립트를 부른다.

.DESCRIPTION
    ① 검증  마스터데이터가 V1~V13 을 통과하는가 (돌리기 전에 잡는다)
    ② 실행  10Hz 페이싱 없이(--max-speed) 게임 N일을 완주시킨다
    ③ 판정  종료 요약을 파싱해 수용 기준과 견준다

    기본은 대시보드를 띄우지 않는다(--no-dashboard). 포트를 물지 않으므로
    다른 실행과 겹쳐도 되고, 몇 초 만에 끝난다.

    화면을 보면서 관측하고 싶으면 -RealTime 을 준다. 그때는 10Hz 로 돌고
    http://localhost:<port>/dashboard 가 열린다.

.EXAMPLE
    ./samples/check.ps1
    ./samples/check.ps1 -Masterdata ./lab/ch05/masterdata -Npcs 50
    ./samples/check.ps1 -RealTime -Npcs 20 -TimeScale 600
#>
[CmdletBinding()]
param(
    # 마스터데이터 폴더. 실습장을 만들었으면 ./lab/<이름>/masterdata 를 준다.
    [string]$Masterdata = './masterdata',

    [int]$Npcs = 50,
    [int]$Days = 1,
    [int]$TimeScale = 600,

    # 시나리오 jsonl (선택).
    [string]$Scenario = '',

    # 재계획 티어. 이 책은 15장까지 none 으로만 돈다.
    [ValidateSet('none', 't1', 't2', 'all')]
    [string]$Tier = 'none',

    # 10Hz 로 돌리고 대시보드를 연다. 눈으로 볼 때만.
    [switch]$RealTime,

    [int]$Port = 5080,

    # 검증 단계를 건너뛴다 (마스터데이터를 안 고친 회차).
    [switch]$SkipValidate,

    # dotnet build 를 건너뛴다. 두 번째 실행부터 준다.
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    # --masterdata 는 반드시 절대 경로로 넘긴다.
    #
    # 상대 경로를 주면 두 경로가 갈린다:
    #   validate  ValidateCommand.ResolveDirectory 가 상대 경로 전체를 위로 올라가며 찾는다 → 사본
    #   서버      HostOptions.ResolveMasterData 는 <b>맨 끝 폴더 이름만</b> 찾는다 → 원본 masterdata/
    # 그러면 "검증은 사본을 보고 실행은 원본을 도는" 상태가 되고, 아무 경고도 안 난다.
    # 절대 경로면 양쪽 다 Directory.Exists 에서 바로 걸려 같은 곳을 본다.
    if (Test-Path $Masterdata) {
        $Masterdata = (Resolve-Path $Masterdata).Path
    }
    else {
        Write-Host "그런 마스터데이터 폴더가 없다: $Masterdata" -ForegroundColor Red
        exit 1
    }

    $project = 'src/Npc.Host'
    $common  = @('run', '-c', 'Release', '--no-launch-profile', '--project', $project)
    if ($NoBuild) { $common += '--no-build' }

    # 네이티브 프로세스의 stderr 를 붙잡을 때 5.1 은 ErrorRecord 로 감싼다.
    # Stop 인 채로 두면 정상 종료(exit 0)에도 예외가 난다.
    function Invoke-Host {
        param([string[]]$HostArgs)

        $all  = $common + @('--') + $HostArgs
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            # 배열은 반드시 @ 로 스플랫한다. & dotnet $arr 은 한 덩어리 문자열로 넘어간다.
            & dotnet @all 2>&1 | ForEach-Object { $_.ToString() }
        }
        finally {
            $ErrorActionPreference = $prev
        }
    }

    # ── ① 검증 ────────────────────────────────────────────────────────────
    if (-not $SkipValidate) {
        Write-Host ""
        Write-Host "① 검증 — $Masterdata" -ForegroundColor Cyan

        $vout = Invoke-Host -HostArgs @('validate', '--masterdata', $Masterdata)
        $vout | ForEach-Object { Write-Host "   $_" }

        if ($LASTEXITCODE -ne 0) {
            Write-Host ""
            Write-Host "검증 실패. 여기서 멈춘다 — 검증 실패는 기동 실패다." -ForegroundColor Red
            exit 1
        }
    }

    # ── ② 실행 ────────────────────────────────────────────────────────────
    Write-Host ""
    Write-Host "② 실행 — npcs $Npcs · days $Days · time-scale $TimeScale · tier $Tier" -ForegroundColor Cyan

    $runArgs = @(
        '--loopback',
        '--masterdata', $Masterdata,
        '--npcs', $Npcs,
        '--days', $Days,
        '--time-scale', $TimeScale,
        '--tier', $Tier
    )
    if ($Scenario) { $runArgs += @('--scenario', $Scenario) }
    if ($RealTime) { $runArgs += @('--port', $Port) }
    else           { $runArgs += @('--max-speed', '--no-dashboard') }

    $started = Get-Date
    $out     = Invoke-Host -HostArgs $runArgs
    $elapsed = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)

    $report = ($out | Where-Object { $_ -match '^tick p50 |^ticks \d' })
    if (-not $report) {
        Write-Host ""
        Write-Host "종료 요약을 못 찾았다. 전체 출력:" -ForegroundColor Red
        $out | ForEach-Object { Write-Host "   $_" }
        exit 1
    }
    $report | ForEach-Object { Write-Host "   $_" -ForegroundColor DarkGray }

    # ── ③ 판정 ────────────────────────────────────────────────────────────
    $text = $report -join ' '

    function Field {
        param([string]$Pattern, [double]$Default = -1)
        if ($text -match $Pattern) { return [double]$Matches[1] }
        return $Default
    }

    $p99      = Field 'p99 ([\d.]+)ms'
    $bytes    = Field 'bytes/tick (\d+)'
    $overruns = Field 'overruns (\d+)'
    $ticks    = Field 'ticks (\d+)'
    $commands = Field 'commands (\d+)'
    $steps    = Field 'steps (\d+)'
    $timeouts = Field 'timeouts (\d+)'
    $backlogs = Field 'backlogs (\d+)'
    $llm      = Field 'llm (\d+)'
    $dropLink = Field 'drops link (\d+)'
    $dropSim  = Field 'drops link \d+ sim (\d+)'

    Write-Host ""
    Write-Host "③ 판정 — 실시간 $elapsed 초" -ForegroundColor Cyan

    $rows = @(
        @{ n = '틱 p99';          v = "$p99 ms";  ok = ($p99 -ge 0 -and $p99 -le 20);  crit = "≤ 20 ms" },
        @{ n = '틱당 할당';        v = "$bytes B"; ok = ($bytes -eq 0);                 crit = "= 0 B" },
        @{ n = '예산 초과 틱';     v = "$overruns"; ok = ($overruns -eq 0);             crit = "= 0" },
        @{ n = '명령 드롭 (링크)'; v = "$dropLink"; ok = ($dropLink -eq 0);             crit = "= 0" },
        @{ n = '명령 드롭 (Sim)';  v = "$dropSim";  ok = ($dropSim -eq 0);              crit = "= 0" },
        @{ n = '이벤트 이월';      v = "$backlogs"; ok = ($backlogs -eq 0);             crit = "= 0" }
    )

    $failed = 0
    foreach ($r in $rows) {
        if ($r.ok) { $mark = 'OK  '; $color = 'Green' } else { $mark = 'FAIL'; $color = 'Red'; $failed++ }
        Write-Host ("   [{0}] {1,-14} {2,-12} 기준 {3}" -f $mark, $r.n, $r.v, $r.crit) -ForegroundColor $color
    }

    Write-Host ""
    Write-Host ("   틱 {0} · 명령 {1} · 스텝 {2} · 타임아웃 합성 {3} · LLM 호출 {4}" -f `
        $ticks, $commands, $steps, $timeouts, $llm) -ForegroundColor DarkGray

    if ($Tier -eq 'none' -and $llm -gt 0) {
        Write-Host "   경고: tier none 인데 LLM 을 불렀다. 배선을 확인한다." -ForegroundColor Yellow
    }

    Write-Host ""
    if ($failed -eq 0) {
        Write-Host "확인 3단 통과." -ForegroundColor Green
        exit 0
    }
    Write-Host "확인 3단 실패 — $failed 개 항목이 기준을 못 넘겼다." -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}
