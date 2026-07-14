<#
.SYNOPSIS
  Builds the distributable Windows zip: self-contained app + bundled ffmpeg, Piper TTS and voice.

  Run on a machine with internet access (only needed at BUILD time — the packaged
  app makes no network calls). Requires the .NET 8 SDK.

    powershell -ExecutionPolicy Bypass -File packaging\build-release.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$app  = Join-Path $dist "VideoWalkthroughMaker"
$tools = Join-Path $app "tools"

Write-Host "==> Publishing app and CLI (self-contained win-x64)"
dotnet publish (Join-Path $root "src/Vwm.App") -c Release -f net8.0-windows10.0.19041.0 `
  -r win-x64 --self-contained -p:PublishSingleFile=true -o $app
if ($LASTEXITCODE -ne 0) { throw "app publish failed" }
dotnet publish (Join-Path $root "src/Vwm.Cli") -c Release -f net8.0-windows10.0.19041.0 `
  -r win-x64 --self-contained -p:PublishSingleFile=true -o $app
if ($LASTEXITCODE -ne 0) { throw "cli publish failed" }

New-Item -ItemType Directory -Force -Path $tools, (Join-Path $tools "voices") | Out-Null
$tmp = Join-Path $dist "downloads"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

Write-Host "==> Downloading ffmpeg (gyan.dev release essentials)"
$ffzip = Join-Path $tmp "ffmpeg.zip"
Invoke-WebRequest "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" -OutFile $ffzip
Expand-Archive $ffzip -DestinationPath $tmp -Force
Get-ChildItem $tmp -Recurse -Include ffmpeg.exe, ffprobe.exe, ffplay.exe | ForEach-Object {
  Copy-Item $_.FullName $tools -Force
}

Write-Host "==> Downloading Piper TTS"
$piperzip = Join-Path $tmp "piper.zip"
Invoke-WebRequest "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip" -OutFile $piperzip
Expand-Archive $piperzip -DestinationPath $tools -Force   # creates tools\piper\piper.exe + data

Write-Host "==> Downloading voice model (en_US lessac, medium)"
$voiceBase = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium"
Invoke-WebRequest "$voiceBase/en_US-lessac-medium.onnx"      -OutFile (Join-Path $tools "voices/en_US-lessac-medium.onnx")
Invoke-WebRequest "$voiceBase/en_US-lessac-medium.onnx.json" -OutFile (Join-Path $tools "voices/en_US-lessac-medium.onnx.json")

Write-Host "==> Zipping"
$zip = Join-Path $dist "VideoWalkthroughMaker-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $app -DestinationPath $zip
Remove-Item $tmp -Recurse -Force

Write-Host "==> Done: $zip"
Write-Host "    Distribute the zip; users just extract it and run VideoWalkthroughMaker.exe."
