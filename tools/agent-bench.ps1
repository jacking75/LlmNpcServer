#Requires -Version 5.1
<#
.SYNOPSIS
    LLM 에이전트 벤치마크 러너 (E-06).

.DESCRIPTION
    온보딩 팩(E-01)·스키마(E-02)·MCP(E-03)가 실제로 에이전트의 성공률을 올리는지 잰다.
    재지 않으면 문서는 다시 추측으로 돌아간다.

    <b>이 스크립트는 에이전트를 부르지 않는다.</b> 에이전트마다 호출 방식이 다르고,
    여기서 그것을 흉내 내면 "이 러너로 잰 점수" 가 되어 버린다. 사람이 과제를 에이전트에게
    주고, 끝나면 이 스크립트로 채점한다 — 러너가 하는 일은 <b>채점과 기록</b>이다.

    흐름:
      1. -List 로 과제를 본다.
      2. 사람이 task.md 를 에이전트에게 그대로 준다.
      3. 에이전트가 끝나면 -Task <id> -Agent <이름> 으로 채점한다.
      4. 결과가 docs/measurements/agent_bench.csv 에 append 된다.

    <b>시각은 인자로 받는다.</b> 벽시계를 스크립트가 읽으면 같은 회차를 다시 채점했을 때
    다른 줄이 남는다 (CLAUDE.md §2.3 과 같은 원칙 — 기록은 재현 가능해야 한다).

.PARAMETER Task
    채점할 과제 id. 예: 01-item-poi. 'all' 이면 전부.

.PARAMETER Agent
    에이전트 이름. CSV 에 그대로 남는다. 예: claude-code · cursor · gpt-5

.PARAMETER Run
    같은 과제의 몇 번째 회차인가. 기본 1.

.PARAMETER Minutes
    에이전트가 쓴 시간(분). 사람이 잰다 — 스크립트가 벽시계를 읽지 않는다.

.PARAMETER Stamp
    CSV 에 남길 날짜. 기본은 빈 값이고, 그때는 'unspecified' 로 남는다.

.PARAMETER Out
    결과 CSV. 기본 docs/measurements/agent_bench.csv

.PARAMETER Note
    회차에 대한 한 줄. <b>깨끗하지 않은 저장소에서 채점했다면 반드시 적는다</b> —
    changed_files 가 그 회차의 값이 아니게 되기 때문이다.

.PARAMETER List
    과제 목록만 찍는다.

.PARAMETER SelfTest
    <b>채점기 자신을 시험한다.</b> 과제를 풀지 않은 상태(= 지금 저장소)에서 채점해
    "아무것도 안 했는데 통과" 가 없는지 본다. 채점기가 언제나 통과하면 벤치는 거짓이 된다.

.EXAMPLE
    powershell -File tools/agent-bench.ps1 -List

.EXAMPLE
    powershell -File tools/agent-bench.ps1 -Task 04-fallback-plan -Agent claude-code -Minutes 7 -Stamp 2026-09-12

.EXAMPLE
    powershell -File tools/agent-bench.ps1 -SelfTest
#>
[CmdletBinding(DefaultParameterSetName = 'List')]
param(
    [Parameter(ParameterSetName = 'Score', Mandatory = $true, Position = 0)]
    [string]$Task,

    [Parameter(ParameterSetName = 'Score', Mandatory = $true)]
    [string]$Agent,

    [Parameter(ParameterSetName = 'Score')]
    [int]$Run = 1,

    [Parameter(ParameterSetName = 'Score')]
    [double]$Minutes = 0,

    [Parameter(ParameterSetName = 'Score')]
    [string]$Stamp = '',

    [Parameter(ParameterSetName = 'Score')]
    [string]$Out = '',

    # 회차에 대한 한 줄. 쉼표를 쓰지 않는다 — CSV 다.
    [Parameter(ParameterSetName = 'Score')]
    [string]$Note = '',

    [Parameter(ParameterSetName = 'List')]
    [switch]$List,

    [Parameter(ParameterSetName = 'SelfTest', Mandatory = $true)]
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$benchRoot = Join-Path $root 'tests/agent-bench'

if (-not $Out) { $Out = Join-Path $root 'docs/measurements/agent_bench.csv' }

# 과제 하나. task.md 의 front matter 에서 읽는다.
function Get-Tasks {
    $tasks = @()

    foreach ($dir in Get-ChildItem $benchRoot -Directory | Where-Object { $_.Name -ne 'lib' } | Sort-Object Name) {
        $taskFile = Join-Path $dir.FullName 'task.md'
        if (-not (Test-Path $taskFile)) { continue }

        $title = ''
        # task.md 는 BOM 없는 UTF-8 이다. PS 5.1 은 기본이 ANSI 라 한글이 깨진다.
        foreach ($line in Get-Content $taskFile -Encoding UTF8) {
            if ($line -match '^title:\s*(.+)$') { $title = $Matches[1].Trim(); break }
        }

        $tasks += [pscustomobject]@{
            Id    = $dir.Name
            Title = $title
            Dir   = $dir.FullName
            Check = Join-Path $dir.FullName 'check.ps1'
        }
    }

    return $tasks
}

$all = Get-Tasks

if ($all.Count -eq 0) {
    Write-Host "과제를 찾지 못했다: $benchRoot" -ForegroundColor Red
    exit 1
}

if ($PSCmdlet.ParameterSetName -eq 'List') {
    Write-Host "에이전트 벤치 과제 $($all.Count)종 (E-06)" -ForegroundColor Cyan
    Write-Host ""

    foreach ($item in $all) {
        Write-Host ("  {0,-20} {1}" -f $item.Id, $item.Title)
    }

    Write-Host ""
    Write-Host "과제를 주려면 tests/agent-bench/<id>/task.md 를 그대로 에이전트에게 준다."
    Write-Host "채점: powershell -File tools/agent-bench.ps1 -Task <id> -Agent <이름> -Minutes <분>"
    exit 0
}

# 채점 하나. 종료 코드가 0 이면 통과다.
function Invoke-Check {
    param([pscustomobject]$Item)

    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $Item.Check 2>&1 | Out-String
    $pass = $LASTEXITCODE -eq 0

    return [pscustomobject]@{
        Id     = $Item.Id
        Pass   = $pass
        Output = $output
    }
}

if ($PSCmdlet.ParameterSetName -eq 'SelfTest') {
    # <b>채점기가 언제나 통과하면 벤치는 거짓이다.</b> 아무것도 안 한 상태에서
    # 전부 통과하는 채점기가 있으면 그것을 여기서 잡는다.
    Write-Host "채점기 자체 시험 — 과제를 풀지 않은 상태에서 채점한다" -ForegroundColor Cyan
    Write-Host ""

    $alwaysPass = @()

    foreach ($item in $all) {
        $result = Invoke-Check -Item $item
        $mark = if ($result.Pass) { '통과(의심)' } else { '실패(정상)' }
        $color = if ($result.Pass) { 'Red' } else { 'Green' }

        Write-Host ("  {0,-20} {1}" -f $item.Id, $mark) -ForegroundColor $color

        if ($result.Pass) { $alwaysPass += $item.Id }
    }

    Write-Host ""

    if ($alwaysPass.Count -gt 0) {
        Write-Host "아무것도 안 했는데 통과하는 채점기: $($alwaysPass -join ', ')" -ForegroundColor Red
        Write-Host "그 과제는 점수가 아니라 상수다. 채점 조건을 고친다." -ForegroundColor Red
        exit 1
    }

    Write-Host "전부 실패했다 — 채점기가 실제로 판정하고 있다." -ForegroundColor Green
    exit 0
}

$targets = @(if ($Task -eq 'all') { $all } else { $all | Where-Object { $_.Id -eq $Task } })

if ($targets.Count -eq 0) {
    Write-Host "모르는 과제다: $Task" -ForegroundColor Red
    Write-Host "목록: powershell -File tools/agent-bench.ps1 -List"
    exit 1
}

if (-not $Stamp) { $Stamp = 'unspecified' }

$outDir = Split-Path -Parent $Out
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

if (-not (Test-Path $Out)) {
    Set-Content -Path $Out -Value 'stamp,agent,task,run,pass,minutes,changed_files,note' -Encoding utf8
}

$failed = 0

foreach ($item in $targets) {
    $result = Invoke-Check -Item $item
    Write-Host $result.Output

    Push-Location $root
    try {
        $changed = @(git status --porcelain | Where-Object { $_ }).Count
    }
    finally {
        Pop-Location
    }

    $row = '{0},{1},{2},{3},{4},{5},{6},{7}' -f `
        $Stamp, $Agent, $item.Id, $Run, $(if ($result.Pass) { 1 } else { 0 }), $Minutes, $changed, $Note

    Add-Content -Path $Out -Value $row -Encoding utf8

    if (-not $result.Pass) { $failed++ }
}

Write-Host ""
Write-Host ("기록: {0}" -f $Out)
Write-Host ("판정: {0}/{1} 통과" -f ($targets.Count - $failed), $targets.Count)

exit $(if ($failed -eq 0) { 0 } else { 1 })
