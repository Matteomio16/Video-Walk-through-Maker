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
param(
  [string]$Version = "1.0.0",
  # Code signing (optional). Supply either a .pfx path (+ password) or the SHA-1
  # thumbprint of a cert already in the machine/user store. When neither is given,
  # signing is skipped and an unsigned build is produced (SmartScreen will warn).
  [string]$SignPfx = "",
  [string]$SignPassword = "",
  [string]$SignThumbprint = ""
)
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
# with backoff so one blip doesn't sink the whole build. When a SHA-256 is
# given the download is verified against it and a mismatch (corrupt or tampered
# artifact) aborts the build; a bad hash also triggers a re-download in case it
# was just a truncated transfer.
function Get-File($Url, $OutFile, $Sha256) {
  $max = 5
  for ($i = 1; $i -le $max; $i++) {
    try {
      Invoke-WebRequest $Url -OutFile $OutFile -UserAgent $ua
      if ($Sha256) {
        $actual = (Get-FileHash $OutFile -Algorithm SHA256).Hash
        if ($actual -ne $Sha256) {
          throw "SHA-256 mismatch for $Url (expected $Sha256, got $actual)"
        }
      }
      return
    } catch {
      if ($i -eq $max) { throw }
      $wait = 5 * $i
      Write-Warning "    download failed (attempt $i/$max): $($_.Exception.Message). Retrying in ${wait}s..."
      Start-Sleep -Seconds $wait
    }
  }
}

# Pinned build artifacts and their SHA-256 hashes. Pinning makes builds
# reproducible; the hashes (also published in README.md) are verified on every
# download so a tampered or corrupted CDN artifact aborts the build. To bump a
# version, change the URL and its hash together. gyan.dev keeps versioned
# ffmpeg builds under builds/packages/.
$ffmpegUrl = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip"
$ffmpegSha = "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec"
$piperUrl  = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip"
$piperSha  = "f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea"

# Signs a file with signtool when signing is configured; a no-op stub otherwise so
# unsigned dev builds still work. Timestamps so signatures outlive the cert.
function Sign-File($Path) {
  if (-not $SignPfx -and -not $SignThumbprint) { return }
  $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
  if (-not $signtool) {
    $signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
      Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending |
      Select-Object -First 1 -ExpandProperty FullName
  }
  if (-not $signtool) { throw "signing requested but signtool.exe not found (install the Windows SDK)" }
  $sargs = @("sign", "/fd", "SHA256", "/tr", "http://timestamp.digicert.com", "/td", "SHA256")
  if ($SignThumbprint) { $sargs += @("/sha1", $SignThumbprint) }
  else { $sargs += @("/f", $SignPfx); if ($SignPassword) { $sargs += @("/p", $SignPassword) } }
  $sargs += $Path
  & $signtool @sargs
  if ($LASTEXITCODE -ne 0) { throw "signtool failed for $Path" }
  Write-Host "    signed $Path"
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

# Sign our own executables (not the third-party tools, which ship pre-signed upstream).
Get-ChildItem $app -Filter *.exe | ForEach-Object { Sign-File $_.FullName }

New-Item -ItemType Directory -Force -Path $tools, (Join-Path $tools "voices") | Out-Null
$tmp = Join-Path $dist "downloads"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

Write-Host "==> Downloading ffmpeg (gyan.dev essentials, pinned + verified)"
$ffzip = Join-Path $tmp "ffmpeg.zip"
Get-File $ffmpegUrl $ffzip $ffmpegSha
Expand-Archive $ffzip -DestinationPath $tmp -Force
Get-ChildItem $tmp -Recurse -Include ffmpeg.exe, ffprobe.exe, ffplay.exe | ForEach-Object {
  Copy-Item $_.FullName $tools -Force
}

Write-Host "==> Downloading Piper TTS"
$piperzip = Join-Path $tmp "piper.zip"
Get-File $piperUrl $piperzip $piperSha
Expand-Archive $piperzip -DestinationPath $tools -Force   # creates tools\piper\piper.exe + data

Write-Host "==> Downloading voice models"
# Each entry is lang/name/quality on huggingface rhasspy/piper-voices; the model id
# is "{lang}-{name}-{quality}". The app lists every .onnx it finds in tools/voices,
# sorted medium-quality first, so hfc_female (the most natural medium voice) is the
# default and ryan-high is the larger, best-quality option.
$voices = @(
  @{ Path = "en/en_US/hfc_female/medium"; Id = "en_US-hfc_female-medium"
     OnnxSha = "914c473788fc1fa8b63ace1cdcdb44588f4ae523d3ab37df1536616835a140b7"
     JsonSha = "03f1fa0622b80463283592d97aca9f6e89aec345a5c56b7257723e0093c58b6c" },
  @{ Path = "en/en_US/ryan/high";         Id = "en_US-ryan-high"
     OnnxSha = "b3990d7606e183ec8dbfba70a4607074f162de1a0c412e0180d1ff60bb154eca"
     JsonSha = "c6d3b98f08315cb4bebf0d49d50fc4ff491b503c64b940cd3d5ca28543b48011" }
)
foreach ($v in $voices) {
  Write-Host "    $($v.Id)"
  $base = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/$($v.Path)"
  Get-File "$base/$($v.Id).onnx"      (Join-Path $tools "voices/$($v.Id).onnx")      $v.OnnxSha
  Get-File "$base/$($v.Id).onnx.json" (Join-Path $tools "voices/$($v.Id).onnx.json") $v.JsonSha
}

# --- Third-party license notices (GPL/LGPL compliance) -----------------------
# Ship the verbatim upstream license texts (FFmpeg is a GPL build) plus our notices
# and a CycloneDX SBOM of the managed dependencies. installer.iss ships everything
# under $app, so these land in the installed folder for review.
Write-Host "==> Collecting third-party license notices"
$licenses = Join-Path $app "licenses"
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
# LICENSE/COPYING files that ship inside the downloaded ffmpeg + piper archives.
Get-ChildItem $tmp, (Join-Path $tools "piper") -Recurse -Include LICENSE, LICENSE.*, COPYING, COPYING.* -ErrorAction SilentlyContinue |
  Group-Object Name | ForEach-Object {
    $src = $_.Group[0]
    $prefix = if ($src.FullName -match '\\piper\\') { "piper" } else { "ffmpeg" }
    Copy-Item $src.FullName (Join-Path $licenses "$prefix-$($src.Name)") -Force
  }
Copy-Item (Join-Path $PSScriptRoot "THIRD-PARTY-NOTICES.md") $app -Force

Write-Host "==> Generating SBOM (CycloneDX)"
try {
  dotnet tool restore | Out-Null
  dotnet dotnet-CycloneDX (Join-Path $root "VideoWalkthroughMaker.sln") -o $app -fn sbom.xml -t
  if ($LASTEXITCODE -ne 0) { throw "CycloneDX returned $LASTEXITCODE" }
} catch {
  Write-Warning "    SBOM generation failed ($($_.Exception.Message)); continuing without sbom.xml."
}

Remove-Item $tmp -Recurse -Force

# --- Sealed-tools manifest ---------------------------------------------------
# The app runs "sealed" at runtime: it resolves only these bundled executables and
# verifies each against the SHA-256 recorded here, refusing to run a substituted
# ffmpeg/piper. Presence of this file is what switches the app out of dev (PATH) mode.
Write-Host "==> Writing tools manifest (SHA-256 of bundled executables)"
$manifest = [ordered]@{}
Get-ChildItem $tools -Recurse -Filter *.exe | Sort-Object Name | ForEach-Object {
  $manifest[$_.Name] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
}
$manifest | ConvertTo-Json | Set-Content (Join-Path $tools "tools.manifest.json") -Encoding UTF8

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

$artifact = $null
if ($iscc) {
  Write-Host "==> Building installer (Inno Setup)"
  & $iscc /Qp "/DAppVersion=$Version" "/DDistDir=$app" (Join-Path $PSScriptRoot "installer.iss")
  if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
  $artifact = Join-Path $dist "VideoWalkthroughMaker-Setup.exe"
  Sign-File $artifact
  Write-Host "==> Done: $artifact"
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
  $artifact = $zip
  Write-Host "==> Done: $zip (users extract it and run VideoWalkthroughMaker.exe)"
}

# --- Release artifact manifest -----------------------------------------------
# Records what shipped: version, artifact hash, whether it was signed, and the
# bundled-tool hashes. Publish this next to the download so recipients can verify.
if ($artifact -and (Test-Path $artifact)) {
  $manifestObj = [ordered]@{
    product   = "Video Walk-through Maker"
    version   = $Version
    built     = (Get-Date).ToUniversalTime().ToString("o")
    signed    = [bool]($SignPfx -or $SignThumbprint)
    artifact  = [ordered]@{
      name   = Split-Path $artifact -Leaf
      sha256 = (Get-FileHash $artifact -Algorithm SHA256).Hash.ToLower()
      bytes  = (Get-Item $artifact).Length
    }
    tools     = (Get-Content (Join-Path $tools "tools.manifest.json") | ConvertFrom-Json)
  }
  $manifestOut = Join-Path $dist "VideoWalkthroughMaker-$Version.manifest.json"
  $manifestObj | ConvertTo-Json -Depth 5 | Set-Content $manifestOut -Encoding UTF8
  Write-Host "==> Wrote artifact manifest: $manifestOut"
}
