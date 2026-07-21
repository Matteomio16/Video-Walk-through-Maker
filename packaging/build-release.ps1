<#
.SYNOPSIS
  Builds the Windows distribution: self-contained app + bundled ffmpeg, Piper TTS,
  voices and libvlc, wrapped into a one-click installer (VideoWalkthroughMaker-Setup.exe).

  Run on a machine with internet access (only needed at BUILD time - the packaged
  app makes no network calls). Requires the .NET 8 SDK, and Inno Setup 6 for the
  installer step (winget install JRSoftware.InnoSetup). Without Inno Setup a plain
  zip is produced instead.

    powershell -ExecutionPolicy Bypass -File packaging\build-release.ps1 [-Version 1.2.3]
#>
param([string]$Version = "1.0.0")
$ErrorActionPreference = "Stop"
# Windows PowerShell 5.1 renders a per-byte progress bar for Invoke-WebRequest
# that throttles downloads 10-50x and is invisible once the console scrolls -
# making the ~100MB+ ffmpeg/voice downloads look frozen. Suppressing it both
# speeds them up and stops them looking hung.
$ProgressPreference = "SilentlyContinue"
# Some CDNs (gyan.dev) return 503 to PowerShell's default "WindowsPowerShell/x.y"
# user agent, apparently via WAF bot-filtering. A browser-like UA avoids it.
$ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"

# gyan.dev (and occasionally huggingface) throw transient 503s / connection
# resets on the large-file fetches, especially under repeated builds. Retry
# with backoff so one blip doesn't sink the whole build.
function Get-File($Url, $OutFile) {
  $max = 5
  for ($i = 1; $i -le $max; $i++) {
    try {
      Invoke-WebRequest $Url -OutFile $OutFile -UserAgent $ua
      return
    } catch {
      if ($i -eq $max) { throw }
      $wait = 5 * $i
      Write-Warning "    download failed (attempt $i/$max): $($_.Exception.Message). Retrying in ${wait}s..."
      Start-Sleep -Seconds $wait
    }
  }
}

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$app  = Join-Path $dist "VideoWalkthroughMaker"
$tools = Join-Path $app "tools"

Write-Host "==> Publishing app and CLI (self-contained win-x64)"
# -p:Platform=x64 is required for VideoLAN.LibVLC.Windows' MSBuild targets to fire -
# they gate the native libvlc\win-x64 copy on the classic $(Platform) property, which
# `dotnet publish -r win-x64` does not set on its own. Without this the app publishes
# fine but silently has no libvlc, and the in-app preview falls back to the OS player.
dotnet publish (Join-Path $root "src/Vwm.App") -c Release -f net8.0-windows10.0.19041.0 `
  -r win-x64 --self-contained -p:PublishSingleFile=true -p:Platform=x64 -o $app
if ($LASTEXITCODE -ne 0) { throw "app publish failed" }
dotnet publish (Join-Path $root "src/Vwm.Cli") -c Release -f net8.0-windows10.0.19041.0 `
  -r win-x64 --self-contained -p:PublishSingleFile=true -o $app
if ($LASTEXITCODE -ne 0) { throw "cli publish failed" }

New-Item -ItemType Directory -Force -Path $tools, (Join-Path $tools "voices") | Out-Null
$tmp = Join-Path $dist "downloads"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

Write-Host "==> Downloading ffmpeg (gyan.dev release essentials)"
$ffzip = Join-Path $tmp "ffmpeg.zip"
Get-File "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" $ffzip
Expand-Archive $ffzip -DestinationPath $tmp -Force
Get-ChildItem $tmp -Recurse -Include ffmpeg.exe, ffprobe.exe, ffplay.exe | ForEach-Object {
  Copy-Item $_.FullName $tools -Force
}

Write-Host "==> Downloading Piper TTS"
$piperzip = Join-Path $tmp "piper.zip"
Get-File "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip" $piperzip
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
  Get-File "$base/$($v.Id).onnx"      (Join-Path $tools "voices/$($v.Id).onnx")
  Get-File "$base/$($v.Id).onnx.json" (Join-Path $tools "voices/$($v.Id).onnx.json")
}

Remove-Item $tmp -Recurse -Force

# --- Installer (preferred) or zip fallback -----------------------------------
# winget installs Inno Setup 6.7+ per-user under %LOCALAPPDATA%\Programs by
# default, not Program Files - so search there too or we'd silently fall back
# to the zip even with Inno Setup installed.
$iscc = @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA "Programs")) |
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
  Write-Host "    Hand users the setup exe - double-click, next, done. Installs per-user"
  Write-Host "    (no admin rights), adds a Start Menu shortcut, never shows a console."
}
else {
  Write-Warning "Inno Setup not found (winget install JRSoftware.InnoSetup) - producing a plain zip instead."
  $zip = Join-Path $dist "VideoWalkthroughMaker-win-x64.zip"
  if (Test-Path $zip) { Remove-Item $zip }
  # Compress-Archive is single-threaded and gives no progress output - on the
  # ~800MB app folder it can silently take minutes and look hung. The .NET
  # ZipFile API with Fastest compression is much quicker and we log first so
  # a slow run doesn't look like a stall.
  Write-Host "==> Zipping $app (this can take a minute)..."
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [System.IO.Compression.ZipFile]::CreateFromDirectory(
    $app, $zip, [System.IO.Compression.CompressionLevel]::Fastest, $false)
  Write-Host "==> Done: $zip (users extract it and run VideoWalkthroughMaker.exe)"
}
