<#
.SYNOPSIS
  Builds the Windows distribution: self-contained app + bundled ffmpeg, Piper TTS,
  voices and libvlc, wrapped into a one-click installer (VideoWalkthroughMaker-Setup.exe).

  Run on a machine with internet access (only needed at BUILD time — the packaged
  app makes no network calls). Requires the .NET 8 SDK, and Inno Setup 6 for the
  installer step (winget install JRSoftware.InnoSetup). Without Inno Setup a plain
  zip is produced instead.

    powershell -ExecutionPolicy Bypass -File packaging\build-release.ps1 [-Version 1.2.3]
#>
param([string]$Version = "1.0.0")
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

Write-Host "==> Downloading voice models"
# Each entry is lang/name/quality on huggingface rhasspy/piper-voices; the model id
# is "{lang}-{name}-{quality}". The app lists every .onnx it finds in tools/voices,
# sorted medium-quality first, so hfc_female (the most natural medium voice) is the
# default and ryan-high is the larger, best-quality option.
$voices = @(
  @{ Path = "en/en_US/hfc_female/medium"; Id = "en_US-hfc_female-medium" },
  @{ Path = "en/en_US/ryan/high";         Id = "en_US-ryan-high" }
)
foreach ($v in $voices) {
  Write-Host "    $($v.Id)"
  $base = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/$($v.Path)"
  Invoke-WebRequest "$base/$($v.Id).onnx"      -OutFile (Join-Path $tools "voices/$($v.Id).onnx")
  Invoke-WebRequest "$base/$($v.Id).onnx.json" -OutFile (Join-Path $tools "voices/$($v.Id).onnx.json")
}

Remove-Item $tmp -Recurse -Force

# --- Installer (preferred) or zip fallback -----------------------------------
$iscc = @(${env:ProgramFiles(x86)}, $env:ProgramFiles) |
  Where-Object { $_ } |
  ForEach-Object { Join-Path $_ "Inno Setup 6\ISCC.exe" } |
  Where-Object { Test-Path $_ } |
  Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }

if ($iscc) {
  Write-Host "==> Building installer (Inno Setup)"
  & $iscc /Qp "/DAppVersion=$Version" "/DDistDir=$app" (Join-Path $PSScriptRoot "installer.iss")
  if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
  Write-Host "==> Done: $(Join-Path $dist 'VideoWalkthroughMaker-Setup.exe')"
  Write-Host "    Hand users the setup exe — double-click, next, done. Installs per-user"
  Write-Host "    (no admin rights), adds a Start Menu shortcut, never shows a console."
}
else {
  Write-Warning "Inno Setup not found (winget install JRSoftware.InnoSetup) — producing a plain zip instead."
  $zip = Join-Path $dist "VideoWalkthroughMaker-win-x64.zip"
  if (Test-Path $zip) { Remove-Item $zip }
  Compress-Archive -Path $app -DestinationPath $zip
  Write-Host "==> Done: $zip (users extract it and run VideoWalkthroughMaker.exe)"
}
