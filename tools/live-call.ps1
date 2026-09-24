# Runs one scripted call through the bridge against the real xAI realtime model and keeps the evidence:
# server log (with per-turn timelines), simulator output, and a mixed recording of both sides.
#   pwsh tools/live-call.ps1 -Name xai
#   pwsh tools/live-call.ps1 -Name openai -Provider openai
#   pwsh tools/live-call.ps1 -Name gemini -Provider gemini
#   pwsh tools/live-call.ps1 -Name xai-silence500 -SilenceDurationMs 500
param(
    [Parameter(Mandatory)] [string] $Name,
    [ValidateSet('xai', 'openai', 'gpt-live', 'gemini')] [string] $Provider = 'xai',
    [string] $Model = '',
    [string] $Voice = '',
    [int] $SilenceDurationMs = 0,
    [double] $VadThreshold = 0
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$out = Join-Path $repo "docs/evidence/live-$Name"
New-Item -ItemType Directory -Force $out | Out-Null

$defaults = @{
    xai    = @{ Key = 'XAI_API_KEY';    Url = 'wss://api.x.ai/v1/realtime';     Model = 'grok-voice-think-fast-2.0'; Voice = 'Leo';   Stt = 'grok-2-audio' }
    openai = @{ Key = 'OPENAI_API_KEY'; Url = 'wss://api.openai.com/v1/realtime'; Model = 'gpt-realtime-2.1';       Voice = 'marin'; Stt = 'gpt-live-transcribe' }
    'gpt-live' = @{ Key = 'OPENAI_API_KEY'; Url = 'wss://api.openai.com/v1/live/sessions'; Model = 'gpt-live-1';     Voice = 'marin'; Stt = '' }
    gemini = @{ Key = 'GEMINI_API_KEY'; Url = '';                                Model = 'gemini-3.8-live';          Voice = 'Puck';  Stt = '' }
}[$Provider]
$env:Realtime__TracePath = Join-Path $out 'provider-trace.jsonl'
Remove-Item -ErrorAction SilentlyContinue $env:Realtime__TracePath
$key = [Environment]::GetEnvironmentVariable($defaults.Key)
if (-not $key) { throw "$($defaults.Key) is not set" }

$env:Realtime__Provider = $Provider
$env:Realtime__ApiKey = $key
$env:Realtime__Url = $defaults.Url
$env:Realtime__Model = if ($Model) { $Model } else { $defaults.Model }
$env:Realtime__Voice = if ($Voice) { $Voice } else { $defaults.Voice }
$env:Realtime__TranscriptionModel = $defaults.Stt
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
