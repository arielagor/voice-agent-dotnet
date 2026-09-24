# The same scripted five-turn call, through the same bridge, against each provider, N times.
#   pwsh tools/benchmark.ps1 -Runs 2
param([int] $Runs = 2, [string[]] $Providers = @('xai', 'openai', 'gpt-live', 'gemini'))

$ErrorActionPreference = 'Continue'
$repo = Split-Path $PSScriptRoot -Parent
foreach ($run in 1..$Runs) {
    foreach ($p in $Providers) {
        Write-Output "=== $p run $run ==="
        & "$PSScriptRoot/live-call.ps1" -Name "bench-$p-$run" -Provider $p | Select-String -Pattern 'greeting|0[1-5]-|ended|median'
        Start-Sleep -Seconds 3
    }
}
python "$PSScriptRoot/summarize_benchmark.py" "$repo/docs/evidence"
