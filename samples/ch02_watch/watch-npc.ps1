#Requires -Version 5.1
<#
.SYNOPSIS
    2장 — NPC 한 마리를 끝까지 따라간다.

.DESCRIPTION
    GET /npc/{id} 를 주기적으로 읽어서 <b>바뀐 것만</b> 한 줄로 찍는다.
    전부 찍으면 초당 수십 줄이라 아무것도 안 보인다. 변화만 남기면 하루가 20줄로 줄어든다.

    보는 것 여섯 가지
      step    몇 번째 스텝을 하고 있나 (플랜 안의 첨자)
      status  그 스텝의 상태 (Ready / Issued / Waiting / Done / Failed)
      plan    플랜 id 와 출처 (bucket / individual / fallback)
      flags   월드 플래그가 서고 지는 순간
      inv     인벤토리 증감
      where   존 · POI 이동

    NPC 서버가 먼저 떠 있어야 한다. 터미널을 하나 더 열고 1장의 실행을 돌린 뒤 이걸 켠다.

.EXAMPLE
    ./samples/ch02_watch/watch-npc.ps1 -Npc 7
    ./samples/ch02_watch/watch-npc.ps1 -Npc 7 -Seconds 60 -IntervalMs 500
#>
[CmdletBinding()]
param(
    # 추적할 NPC 첨자. 0 .. (--npcs - 1).
    [int]$Npc = 7,

    [string]$BaseUrl = 'http://localhost:5080',

    # 폴링 간격. 0.1초(1틱)보다 잘게 볼 이유는 없다.
    [int]$IntervalMs = 700,

    # 몇 초 동안 볼 것인가. 0 이면 Ctrl-C 까지.
    [int]$Seconds = 0
)

$ErrorActionPreference = 'Stop'

function Get-Trace {
    try {
        return Invoke-RestMethod -Uri "$BaseUrl/npc/$Npc" -TimeoutSec 3
    }
    catch {
        return $null
    }
}

# 첫 응답을 기다린다. 서버가 아직 안 떴을 수도 있다.
$t = $null
for ($i = 0; $i -lt 30; $i++) {
    $t = Get-Trace
    if ($t) { break }
    Start-Sleep -Milliseconds 500
}

if (-not $t) {
    Write-Host "NPC 서버에 못 붙었다: $BaseUrl" -ForegroundColor Red
    Write-Host "터미널을 하나 더 열고 먼저 이것을 돌린다:" -ForegroundColor Yellow
    Write-Host "  ./samples/ch01_first_run/run.ps1 -Npcs 20"
    exit 1
}

if (-not $t.found) {
    Write-Host "그 첨자에 NPC 가 없다: $Npc (--npcs 보다 작아야 한다)" -ForegroundColor Red
    exit 1
}

# ── 머리말 — 이 NPC 가 누구인가 ───────────────────────────────────────────
Write-Host ""
Write-Host ("npc #{0}  {1}" -f $t.npc, $t.archetype) -ForegroundColor Cyan
Write-Host ("  집 {0} · 일터 {1}" -f `
    $(if ($t.homePoi) { $t.homePoi } else { '(없음)' }), `
    $(if ($t.workPoi) { $t.workPoi } else { '(없음)' }))
Write-Host ("  플랜 #{0} [{1}]  goal={2}  bucket={3}  loop={4}" -f `
    $t.planId, $t.planKind, $t.planGoal, $t.planBucket, $t.planLoop)
Write-Host "  스텝"
foreach ($s in $t.steps) {
    $poi = ''
    if ($s.poi) { $poi = " -> $($s.poi)" }
    $cnt = ''
    if ($s.count -gt 0) { $cnt = " x$($s.count)" }
    Write-Host ("    {0}. {1}{2}{3}   timeout {4}s" -f $s.index, $s.action, $poi, $cnt, $s.timeoutSeconds)
}
Write-Host ""
Write-Host "  이제 바뀌는 것만 찍는다. 멈추려면 Ctrl-C." -ForegroundColor DarkGray
Write-Host "───────────────────────────────────────────────────────────────────"

# ── 변화 추적 ─────────────────────────────────────────────────────────────
function Get-CurrentStep {
    param($Trace)
    foreach ($s in $Trace.steps) { if ($s.current) { return $s } }
    return $null
}

function Format-Step {
    param($Step)
    if (-not $Step) { return '(스텝 없음)' }
    $poi = ''
    if ($Step.poi) { $poi = "($($Step.poi))" }
    return ("{0} {1}{2}" -f $Step.index, $Step.action, $poi)
}

# "wool × 115" 꼴의 문자열 배열을 이름 -> 수량 표로 바꾼다. 증감만 찍기 위한 것이다.
function ConvertTo-InvMap {
    param($Items)
    $map = @{}
    foreach ($s in $Items) {
        if ($s -match '^(.+?)\s+.\s+(\d+)$') { $map[$Matches[1]] = [int]$Matches[2] }
    }
    return $map
}

$prev    = $null
$started = Get-Date
$lines   = 0

while ($true) {
    if ($Seconds -gt 0 -and ((Get-Date) - $started).TotalSeconds -ge $Seconds) { break }

    $t = Get-Trace
    if (-not $t -or -not $t.found) {
        # 서버가 내려갔다. 종료가 정상이다 — --days 만큼 돌고 스스로 끝난다.
        Write-Host "───────────────────────────────────────────────────────────────────"
        Write-Host ("NPC 서버 응답 없음. {0}줄 기록하고 멈춘다." -f $lines) -ForegroundColor DarkGray
        break
    }

    if ($prev) {
        $changes = @()

        $ps = Get-CurrentStep -Trace $prev
        $cs = Get-CurrentStep -Trace $t
        if ((Format-Step $ps) -ne (Format-Step $cs)) {
            $changes += ("step {0} -> {1}" -f (Format-Step $ps), (Format-Step $cs))
        }

        if ($prev.stepStatus -ne $t.stepStatus) {
            $changes += ("status {0} -> {1}" -f $prev.stepStatus, $t.stepStatus)
        }

        if ($prev.planId -ne $t.planId -or $prev.planKind -ne $t.planKind) {
            $changes += ("plan #{0}[{1}] -> #{2}[{3}]" -f $prev.planId, $prev.planKind, $t.planId, $t.planKind)
        }

        if ($prev.poi -ne $t.poi) {
            $from = $prev.poi; if (-not $from) { $from = '-' }
            $to   = $t.poi;    if (-not $to)   { $to   = '-' }
            $changes += ("where {0} -> {1}" -f $from, $to)
        }

        if ($prev.zone -ne $t.zone) {
            $changes += ("zone {0} -> {1}" -f $prev.zone, $t.zone)
        }

        $gone = @($prev.flags | Where-Object { $t.flags -notcontains $_ })
        $came = @($t.flags | Where-Object { $prev.flags -notcontains $_ })
        if ($came.Count -or $gone.Count) {
            $f = ''
            if ($came.Count) { $f += '+' + ($came -join ' +') }
            if ($gone.Count) { if ($f) { $f += ' ' }; $f += '-' + ($gone -join ' -') }
            $changes += "flags $f"
        }

        $before = ConvertTo-InvMap $prev.inventory
        $after  = ConvertTo-InvMap $t.inventory
        $deltas = @()
        foreach ($k in @($before.Keys + $after.Keys | Sort-Object -Unique)) {
            $o = 0; if ($before.ContainsKey($k)) { $o = $before[$k] }
            $n = 0; if ($after.ContainsKey($k))  { $n = $after[$k] }
            if ($n -ne $o) {
                $sign = ''
                if ($n -gt $o) { $sign = '+' }
                $deltas += ("{0} {1}{2} (={3})" -f $k, $sign, ($n - $o), $n)
            }
        }
        if ($deltas.Count) { $changes += ("inv {0}" -f ($deltas -join ', ')) }

        if ($t.pendingPlanId -ne 0 -and $prev.pendingPlanId -eq 0) {
            $changes += ("pending plan #{0} (urgency {1})" -f $t.pendingPlanId, $t.pendingUrgency)
        }

        foreach ($c in $changes) {
            $color = 'Gray'
            if ($c.StartsWith('step'))   { $color = 'White' }
            if ($c.StartsWith('plan'))   { $color = 'Magenta' }
            if ($c.StartsWith('flags'))  { $color = 'DarkCyan' }
            if ($c.StartsWith('inv'))    { $color = 'DarkYellow' }
            Write-Host ("[t={0,6}] {1}" -f $t.tick, $c) -ForegroundColor $color
            $lines++
        }
    }

    $prev = $t
    Start-Sleep -Milliseconds $IntervalMs
}

Write-Host "───────────────────────────────────────────────────────────────────"
Write-Host ("기록 {0}줄" -f $lines) -ForegroundColor DarkGray
