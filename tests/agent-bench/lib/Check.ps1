#Requires -Version 5.1
<#
.SYNOPSIS
    에이전트 벤치 채점 공통 함수 (E-06).

.DESCRIPTION
    과제마다 check.ps1 이 있고, 전부 이 파일을 점으로 불러 쓴다.

    <b>채점은 결정론이어야 한다.</b> 같은 산출물에 같은 점수가 나와야 전후 대조가 의미를 갖는다.
    그래서 여기에는 시각도 난수도 없고, 판정은 전부 "파일이 있나 · 명령이 0 으로 끝났나 ·
    바뀐 파일이 허용 집합 안인가" 세 가지로만 이뤄진다.

    <b>부분 점수를 주지 않는다.</b> 하나라도 떨어지면 그 과제는 실패다 —
    "절반 맞았다" 는 콘텐츠 파이프라인에서 아무 의미가 없다(검증에 떨어진 마스터데이터는
    기동을 막는다).
#>

$script:Checks = @()

# 판정 하나를 기록한다. 통과·실패와 사람이 읽는 한 줄.
function Add-Check {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][bool]$Pass,
        [string]$Detail = ''
    )

    $script:Checks += [pscustomobject]@{
        Name   = $Name
        Pass   = $Pass
        Detail = $Detail
    }
}

# 저장소 루트. check.ps1 은 tests/agent-bench/<과제>/ 에 있다.
function Get-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
}

# 명령 하나를 돌리고 종료 코드를 본다. 출력은 판정 근거로 남긴다.
function Test-Command {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$File,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory = ''
    )

    if (-not $WorkingDirectory) { $WorkingDirectory = Get-RepoRoot }

    $previous = Get-Location
    Set-Location $WorkingDirectory

    try {
        $output = & $File @Arguments 2>&1 | Out-String
        $code = $LASTEXITCODE
    }
    catch {
        $output = $_.Exception.Message
        $code = 1
    }
    finally {
        Set-Location $previous
    }

    $first = ($output -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 1)
    Add-Check -Name $Name -Pass ($code -eq 0) -Detail "exit $code · $first"

    return $output
}

# 파일이 있어야 한다.
function Test-FileExists {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $full = Join-Path (Get-RepoRoot) $Path
    Add-Check -Name $Name -Pass (Test-Path $full) -Detail $Path
}

# 파일 안에 이 문자열이 있어야 한다(또는 없어야 한다).
function Test-FileContains {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [switch]$Absent
    )

    $full = Join-Path (Get-RepoRoot) $Path

    if (-not (Test-Path $full)) {
        Add-Check -Name $Name -Pass $false -Detail "$Path 이 없다"
        return
    }

    $found = (Select-String -Path $full -Pattern $Pattern -SimpleMatch -Quiet) -eq $true
    $pass = if ($Absent) { -not $found } else { $found }

    Add-Check -Name $Name -Pass $pass -Detail "$Path : '$Pattern' $(if ($found) { '있음' } else { '없음' })"
}

<#
    바뀐 파일이 허용 집합 안인가.

    <b>이것이 이 벤치의 핵심 판정이다.</b> 과제를 "해냈는가" 만 보면 에이전트가 옆 파일을
    부수고도 통과한다 — 실제로 가장 흔한 사고가 "요청하지 않은 파일이 같이 바뀌었다" 다.
#>
function Test-ChangedFiles {
    param(
        [Parameter(Mandatory = $true)][string[]]$Allowed,
        [string]$Name = '바뀐 파일이 허용 집합 안인가'
    )

    $root = Get-RepoRoot
    $previous = Get-Location
    Set-Location $root

    try {
        $changed = @(git status --porcelain |
            ForEach-Object { $_.Substring(3).Trim('"') } |
            Where-Object { $_ })
    }
    finally {
        Set-Location $previous
    }

    $extra = @($changed | Where-Object {
        $file = $_ -replace '\\', '/'
        $ok = $false
        foreach ($pattern in $Allowed) {
            if ($file -like $pattern) { $ok = $true; break }
        }
        -not $ok
    })

    Add-Check -Name $Name -Pass ($extra.Count -eq 0) `
        -Detail $(if ($extra.Count -eq 0) { "$($changed.Count)개 전부 허용" } else { "허용 밖: $($extra -join ', ')" })
}

<#
    판정을 출력하고 종료 코드를 정한다.

    <b>0 = 통과, 1 = 실패.</b> 러너가 이 값만 본다 — 표준출력은 사람이 읽는 것이다.
#>
function Complete-Check {
    param([Parameter(Mandatory = $true)][string]$Task)

    $failed = @($script:Checks | Where-Object { -not $_.Pass })

    Write-Host ""
    Write-Host "과제 $Task" -ForegroundColor Cyan

    foreach ($check in $script:Checks) {
        $mark = if ($check.Pass) { 'PASS' } else { 'FAIL' }
        $color = if ($check.Pass) { 'Green' } else { 'Red' }
        Write-Host ("  {0}  {1}" -f $mark, $check.Name) -ForegroundColor $color
        if ($check.Detail) { Write-Host ("        {0}" -f $check.Detail) -ForegroundColor DarkGray }
    }

    Write-Host ""
    Write-Host ("판정 {0}/{1}" -f ($script:Checks.Count - $failed.Count), $script:Checks.Count)

    exit $(if ($failed.Count -eq 0) { 0 } else { 1 })
}
