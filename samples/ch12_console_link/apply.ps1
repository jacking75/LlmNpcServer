#Requires -Version 5.1
<#
.SYNOPSIS
    12장 — 내가 만든 링크를 NPC 서버에 끼운다.

.DESCRIPTION
    ConsoleGameServerLink.cs 를 src/Npc.Gateway/ 에 넣고 --link console 로 고를 수 있게 배선한다.

    고치는 곳 셋 — 전부 <b>배선</b>이고 로직이 아니다.
      ① src/Npc.Gateway/ConsoleGameServerLink.cs   새 파일 (약 150줄)
      ② src/Npc.Host/HostOptions.cs                LinkKind 에 Console 추가
      ③ src/Npc.Host/Program.cs                    switch 에 case 하나

    <b>Npc.Runtime · Npc.Planning · Npc.Core · Npc.Contracts 는 한 줄도 안 바뀐다.</b>
    그것이 이 장에서 확인할 것이다.

    본체를 고치므로 전용 브랜치에서 한다.

.EXAMPLE
    git switch -c lab/ch12-console-link
    ./samples/ch12_console_link/apply.ps1
    ./samples/ch12_console_link/apply.ps1 -Run
    ./samples/ch12_console_link/revert.ps1 -Yes
#>
[CmdletBinding()]
param(
    [switch]$Force,

    # 배선 후 짧게 돌려 명령이 콘솔에 흐르는 것을 본다.
    [switch]$Run,

    [int]$Npcs = 5,
    [int]$Lines = 25
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

Push-Location $root
try {
    $branch = (& git rev-parse --abbrev-ref HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $branch -in @('main', 'master') -and -not $Force) {
        Write-Host ""
        Write-Host "지금 '$branch' 브랜치다. 이 실습은 본체를 고치므로 전용 브랜치에서 한다." -ForegroundColor Red
        Write-Host "  git switch -c lab/ch12-console-link" -ForegroundColor Yellow
        exit 1
    }

    function Edit-File {
        param([string]$Path, [string]$From, [string]$To, [string]$Label)

        $full = Join-Path $root $Path
        $text = ([IO.File]::ReadAllText($full)) -replace "`r`n", "`n"

        if ($text.IndexOf($To) -ge 0) { Write-Host "  이미 적용됨: $Label" -ForegroundColor DarkGray; return }
        if ($text.IndexOf($From) -lt 0) { throw "$Path 에서 앵커를 못 찾았다 ($Label)." }

        $at   = $text.IndexOf($From)
        $text = $text.Substring(0, $at) + $To + $text.Substring($at + $From.Length)
        [IO.File]::WriteAllText($full, $text, (New-Object Text.UTF8Encoding $false))
        Write-Host "  $Label" -ForegroundColor Green
    }

    # ── ① 링크 구현을 Gateway 에 넣는다 ──────────────────────────────────
    Write-Host ""
    Write-Host "① src/Npc.Gateway/ConsoleGameServerLink.cs" -ForegroundColor Cyan
    $src = Join-Path $PSScriptRoot 'ConsoleGameServerLink.cs'
    $dst = Join-Path $root 'src/Npc.Gateway/ConsoleGameServerLink.cs'
    Copy-Item -Path $src -Destination $dst -Force
    Write-Host "  복사했다 (Npc.Gateway 는 Npc.Contracts 만 참조한다)" -ForegroundColor Green

    # ── ② LinkKind 에 값 하나 ────────────────────────────────────────────
    Write-Host ""
    Write-Host "② src/Npc.Host/HostOptions.cs — LinkKind.Console" -ForegroundColor Cyan
    Edit-File -Path 'src/Npc.Host/HostOptions.cs' -Label 'LinkKind 에 Console 추가' `
        -From "    /// <summary>Loopback 을 감싸 명령·이벤트를 jsonl 로 기록한다.</summary>`n    Record," `
        -To   "    /// <summary>명령을 콘솔에 찍고 완료를 즉시 합성한다 (활용 실습서 12장).</summary>`n    Console,`n`n    /// <summary>Loopback 을 감싸 명령·이벤트를 jsonl 로 기록한다.</summary>`n    Record,"

    # ── ③ 팩토리에 case 하나 ─────────────────────────────────────────────
    Write-Host ""
    Write-Host "③ src/Npc.Host/Program.cs — switch 에 case 하나" -ForegroundColor Cyan
    Edit-File -Path 'src/Npc.Host/Program.cs' -Label 'case LinkKind.Console' `
        -From "            case LinkKind.Loopback:`n            default:" `
        -To   "            case LinkKind.Console:`n                // 활용 실습서 12장. 대역(Sim)을 만들지 않는다 — 이 링크가 스스로 응답한다.`n                // 다만 세계 시간을 미는 것은 호스트의 펌프이고, 펌프는 nullLink 로 TickSync 를 넣는다.`n                // 그래서 Null 을 감싼다 (RecordingGameServerLink 와 같은 데코레이터다).`n                nullLink = new NullGameServerLink();`n                link = new ConsoleGameServerLink(nullLink);`n                break;`n`n            case LinkKind.Loopback:`n            default:"

    # ── 빌드 ──────────────────────────────────────────────────────────────
    Write-Host ""
    Write-Host "④ 빌드" -ForegroundColor Cyan
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & dotnet build -c Release 2>&1 | Select-Object -Last 4 | ForEach-Object { Write-Host "   $($_.ToString())" -ForegroundColor DarkGray }
    $ok = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $prev

    if (-not $ok) {
        Write-Host ""
        Write-Host "빌드 실패. 위 메시지를 읽는다." -ForegroundColor Red
        exit 1
    }

    # ── 런타임이 안 바뀌었는지 ────────────────────────────────────────────
    Write-Host ""
    Write-Host "⑤ 무엇이 바뀌었나" -ForegroundColor Cyan
    $changed = @(& git status --porcelain -- src | Where-Object { $_ })
    $changed | ForEach-Object { Write-Host "   $_" }

    $forbidden = @($changed | Where-Object { $_ -match 'Npc\.(Runtime|Planning|Core|Contracts)/' })
    Write-Host ""
    if ($forbidden.Count -eq 0) {
        Write-Host "   Npc.Runtime · Npc.Planning · Npc.Core · Npc.Contracts diff = 0줄" -ForegroundColor Green
        Write-Host "   링크는 갈아 끼우는 부품이다. 그것이 IGameServerLink 가 존재하는 이유다." -ForegroundColor DarkGray
    }
    else {
        Write-Host "   런타임 쪽에 diff 가 생겼다 — 배선이 잘못됐다." -ForegroundColor Red
        $forbidden | ForEach-Object { Write-Host "     $_" -ForegroundColor Red }
    }

    if (-not $Run) {
        Write-Host ""
        Write-Host "돌려 보려면 -Run. 되돌리려면 ./samples/ch12_console_link/revert.ps1 -Yes" -ForegroundColor DarkGray
        exit 0
    }

    # ── 돌려 본다 ─────────────────────────────────────────────────────────
    Write-Host ""
    Write-Host "⑥ --link console 로 짧게 (NPC $Npcs · 게임 1일)" -ForegroundColor Cyan
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $out = & dotnet run -c Release --no-build --no-launch-profile --project src/Npc.Host -- `
        --link console --npcs $Npcs --time-scale 600 --days 1 --no-llm --max-speed --no-dashboard 2>&1 |
        ForEach-Object { $_.ToString() }
    $ErrorActionPreference = $prev

    $cmds = @($out | Where-Object { $_ -match '^\[t=' })
    $cmds | Select-Object -First $Lines | ForEach-Object { Write-Host "   $_" -ForegroundColor DarkGray }
    Write-Host ("   … 명령 {0}줄" -f $cmds.Count) -ForegroundColor DarkGray

    Write-Host ""
    ($out | Where-Object { $_ -match '^tick p50 |^ticks \d' }) | ForEach-Object { Write-Host "   $_" }
    Write-Host ""
    Write-Host "게임서버가 없는데 NPC 가 하루를 살았다." -ForegroundColor Green
}
finally {
    Pop-Location
}
