#Requires -Version 5.1
# 과제 7 채점 (E-06).
. "$PSScriptRoot/../lib/Check.ps1"

$root = Get-RepoRoot
$outDir = Join-Path $root 'bench-out/07'
$broken = Get-ChildItem (Join-Path $PSScriptRoot 'broken') -Filter '*.json' -ErrorAction SilentlyContinue

Add-Check -Name '깨진 플랜 표본이 있다' -Pass ($broken.Count -gt 0) -Detail "$($broken.Count)건"

$passed = 0

foreach ($file in $broken) {
    $bucket = [IO.Path]::GetFileNameWithoutExtension($file.Name)
    $fixed = Join-Path $outDir $file.Name

    if (-not (Test-Path $fixed)) { continue }

    $previous = Get-Location
    Set-Location $root
    try {
        & dotnet run --project tools/Npc.Cli -- plan validate $fixed --bucket $bucket 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { $passed++ }
    }
    finally { Set-Location $previous }
}

Add-Check -Name '8건 전부 통과한다' -Pass ($passed -eq $broken.Count) `
    -Detail "$passed/$($broken.Count) 통과"

Test-ChangedFiles -Name '원본을 고치지 않았다' -Allowed @('bench-out/*', 'bench-out/**')

Complete-Check -Task '07-repair-plans'
