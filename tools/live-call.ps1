# Runs one scripted call through the bridge against the real xAI realtime model and keeps the evidence:
# server log (with per-turn timelines), simulator output, and a mixed recording of both sides.
#   pwsh tools/live-call.ps1 -Name baseline
#   pwsh tools/live-call.ps1 -Name silence500 -SilenceDurationMs 500
param(
    [Parameter(Mandatory)] [string] $Name,
    [int] $SilenceDurationMs = 0,
    [double] $VadThreshold = 0
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$out = Join-Path $repo "docs/evidence/live-$Name"
New-Item -ItemType Directory -Force $out | Out-Null
if (-not $env:XAI_API_KEY) { throw 'XAI_API_KEY is not set' }

$env:Realtime__Provider = 'xai'
$env:Realtime__ApiKey = $env:XAI_API_KEY
if ($SilenceDurationMs -gt 0) { $env:Realtime__SilenceDurationMs = "$SilenceDurationMs" }
if ($VadThreshold -gt 0) { $env:Realtime__VadThreshold = "$VadThreshold" }

$server = Start-Process -FilePath dotnet -PassThru -WindowStyle Hidden `
    -ArgumentList 'run', '--project', "$repo/src/VoiceAgent", '-c', 'Release', '--no-build', '--launch-profile', 'VoiceAgent' `
    -RedirectStandardOutput "$out/server.log" -RedirectStandardError "$out/server.err.log"
try {
    $up = $false
    foreach ($i in 1..60) {
        Start-Sleep -Milliseconds 500
        try { if ((Invoke-WebRequest http://localhost:5080/healthz -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { $up = $true; break } } catch {}
    }
    if (-not $up) { throw 'bridge did not start' }
    python "$repo/tools/simulate_call.py" --script "$repo/tools/caller-audio" --record "$out/call.wav" 2>&1 |
        Tee-Object -FilePath "$out/simulator.txt"
}
finally {
    Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    Get-Process VoiceAgent -ErrorAction SilentlyContinue | Stop-Process -Force
}
