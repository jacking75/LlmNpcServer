<#
.SYNOPSIS
    W1 스파이크용 dotLLM 기동 스크립트 (T0-02).

.DESCRIPTION
    dotLLM 릴리스 바이너리를 tools/dotllm/ 에 준비하고 OpenAI 호환 서버를 띄운다.
    dotLLM 은 GPLv3 다. NuGet 참조로 인프로세스 임베딩하지 않고 별도 프로세스 + HTTP 로만 쓴다
    (CLAUDE.md §2.7).

    -Fetch 를 주면 릴리스 zip 을 내려받아 압축을 푼다. 바이너리는 저장소에 커밋하지 않는다.

.EXAMPLE
    # 최초 1회 — 바이너리 준비
    .\spike\run-dotllm.ps1 -Fetch

.EXAMPLE
    # 8B 를 GPU 로 서빙 (기본값)
    .\spike\run-dotllm.ps1

.EXAMPLE
    # 4B, 포트 8081
    .\spike\run-dotllm.ps1 -Model "$env:USERPROFILE\.lmstudio\models\lmstudio-community\gemma-3-4b-it-GGUF\gemma-3-4b-it-Q4_K_M.gguf" -Port 8081

.EXAMPLE
    # 서버를 띄우지 않고 스모크만 (이미 떠 있을 때)
    .\spike\run-dotllm.ps1 -SmokeOnly
#>
[CmdletBinding()]
param(
    [string]$Model = "$env:USERPROFILE\.lmstudio\models\lmstudio-community\Qwen3-8B-GGUF\Qwen3-8B-Q4_K_M.gguf",
    [int]$Port = 8080,
    [ValidateSet('cpu', 'gpu', 'gpu:0', 'gpu:1')]
    [string]$Device = 'gpu',
    # 전 레이어 오프로드. 8GB VRAM 에서 8B Q4_K_M 이 들어가는지 확인하는 것이 T0-02 의 목적이다.
    [int]$GpuLayers = 99,
    # f32 KV 캐시는 8GB 카드에서 4,500 토큰 프리픽스를 감당하지 못한다. q8_0 로 4배 줄인다.
    [ValidateSet('f32', 'q8_0', 'q4_0')]
    [string]$CacheType = 'q8_0',
    # dotLLM 의 CUDA 백엔드는 드라이버(nvcuda.dll) 외에 cuBLAS 를 요구한다. CUDA Toolkit 이 없으면
    # LM Studio 가 들고 있는 vendor 폴더를 PATH 에 얹어서 해결한다 (복사·재배포하지 않는다).
    [string]$CudaDir = "$env:USERPROFILE\.lmstudio\extensions\backends\vendor\win-llama-cuda12-vendor-v2",
    [switch]$Fetch,
    [switch]$SmokeOnly,
    [string]$Version = '0.1.0-preview.3'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotllmDir = Join-Path $repoRoot 'tools\dotllm'
$dotllmExe = Join-Path $dotllmDir 'dotllm.exe'
$baseUrl = "http://localhost:$Port"

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# ---------------------------------------------------------------- 바이너리 준비
if ($Fetch -or -not (Test-Path $dotllmExe)) {
    if (-not $Fetch) {
        throw "dotllm.exe 가 없다: $dotllmExe  ('-Fetch' 로 내려받는다)"
    }
    Write-Step "dotLLM $Version 다운로드"
    $zipUrl = "https://github.com/kkokosa/dotLLM/releases/download/v$Version/dotllm-$Version-win-x64.zip"
    $zipPath = Join-Path $env:TEMP "dotllm-$Version-win-x64.zip"
    New-Item -ItemType Directory -Force -Path $dotllmDir | Out-Null
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath
    Expand-Archive -Path $zipPath -DestinationPath $dotllmDir -Force
    Write-Host "  unpacked -> $dotllmDir"
}

# ---------------------------------------------------------------- CUDA 런타임
if ($Device -ne 'cpu') {
    if (Test-Path (Join-Path $CudaDir 'cublas64_12.dll')) {
        $env:PATH = "$CudaDir;$env:PATH"
        Write-Step "cuBLAS from $CudaDir"
    }
    else {
        Write-Warning "cublas64_12.dll 을 못 찾았다. CUDA Toolkit 이 없으면 --device gpu 는 'Dll was not found' 로 죽는다."
    }
}

# ---------------------------------------------------------------- 환경 기록
Write-Step 'environment'
& $dotllmExe --version
if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
    nvidia-smi --query-gpu=name,memory.total,memory.used,driver_version --format=csv
}

# ---------------------------------------------------------------- 서버 기동
if (-not $SmokeOnly) {
    if (-not (Test-Path $Model)) { throw "모델 파일이 없다: $Model" }

    Write-Step "serve $([System.IO.Path]::GetFileName($Model)) :$Port device=$Device gpu-layers=$GpuLayers"
    $serveArgs = @(
        'serve', $Model,
        '--port', $Port,
        '--device', $Device,
        '--cache-type-k', $CacheType,
        '--cache-type-v', $CacheType,
        '--no-browser',
        '--no-ui'
    )
    # --gpu-layers 는 device=cpu 일 때도 GPU 경로를 켜버린다. CPU 서빙에서는 아예 넘기지 않는다.
    if ($Device -ne 'cpu') { $serveArgs += @('--gpu-layers', $GpuLayers) }
    $proc = Start-Process -FilePath $dotllmExe -ArgumentList $serveArgs -PassThru -NoNewWindow
    Write-Host "  pid = $($proc.Id)"

    # 모델 로드 + 워밍업이 끝날 때까지 기다린다 (8B 는 30초 이상 걸린다)
    $deadline = [DateTime]::UtcNow.AddMinutes(5)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Seconds 3
        try {
            $r = Invoke-WebRequest -Uri "$baseUrl/v1/models" -TimeoutSec 5 -UseBasicParsing
            if ($r.StatusCode -eq 200) { $ready = $true; break }
        }
        catch { }
        if ($proc.HasExited) { throw "dotllm 프로세스가 종료됐다 (exit $($proc.ExitCode))" }
    }
    if (-not $ready) { throw '기동 타임아웃 — /v1/models 가 200 을 주지 않는다' }
}

# ---------------------------------------------------------------- 스모크
Write-Step 'GET /v1/models'
$models = Invoke-WebRequest -Uri "$baseUrl/v1/models" -TimeoutSec 10 -UseBasicParsing
Write-Host "  status = $($models.StatusCode)"
Write-Host "  body   = $($models.Content)"

Write-Step 'POST /v1/chat/completions (non-stream)'
$body = @{
    model       = 'local'
    messages    = @(@{ role = 'user'; content = 'Reply with the single word: ok' })
    max_tokens  = 8
    temperature = 0
} | ConvertTo-Json -Depth 5 -Compress
$chat = Invoke-WebRequest -Uri "$baseUrl/v1/chat/completions" -Method Post `
    -ContentType 'application/json' -Body $body -TimeoutSec 300 -UseBasicParsing
Write-Host "  status = $($chat.StatusCode)"
Write-Host "  body   = $($chat.Content)"

Write-Step 'POST /v1/chat/completions (SSE stream)'
# Invoke-WebRequest 는 스트림을 통째로 버퍼링하므로 HttpClient 로 직접 읽는다.
Add-Type -AssemblyName System.Net.Http
$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromMinutes(5)
$streamBody = @{
    model       = 'local'
    messages    = @(@{ role = 'user'; content = 'Count: one two three' })
    max_tokens  = 24
    temperature = 0
    stream      = $true
} | ConvertTo-Json -Depth 5 -Compress
$req = [System.Net.Http.HttpRequestMessage]::new('POST', "$baseUrl/v1/chat/completions")
$req.Content = [System.Net.Http.StringContent]::new($streamBody, [Text.Encoding]::UTF8, 'application/json')
# Windows PowerShell 5.1 은 .NET Framework 의 HttpClient 라 동기 Send() 가 없다. Async + .Result.
$resp = $http.SendAsync($req, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).Result
Write-Host "  status       = $([int]$resp.StatusCode)"
Write-Host "  content-type = $($resp.Content.Headers.ContentType)"
$reader = [System.IO.StreamReader]::new($resp.Content.ReadAsStreamAsync().Result)
$chunks = 0
while (-not $reader.EndOfStream) {
    $line = $reader.ReadLine()
    if ($line -like 'data: *') {
        $chunks++
        if ($chunks -le 3) { Write-Host "  chunk[$chunks] $line" }
    }
}
Write-Host "  sse chunks   = $chunks"
$reader.Dispose(); $http.Dispose()

if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
    Write-Step 'VRAM after load'
    nvidia-smi --query-gpu=memory.used,memory.total,utilization.gpu --format=csv
}

Write-Step 'smoke ok'
