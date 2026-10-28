# Pixous Assistant - self-hosted chat model setup (Windows PowerShell, x64 only).
# STATUS: authored and statically reviewed, NOT executed on Windows - if any
# step fails, follow the manual commands in docs/v2/RUN-CHAT-ON-LAPTOP.md.
# Downloads the pinned public GGUF and verifies size + SHA256. No account,
# no login, no paid service. Weights download at install time on THIS machine.
$ErrorActionPreference = "Stop"

# --- prerequisites: x64 Windows, Python 3.10-3.13 (prebuilt CPU wheel range) ---
if (-not [Environment]::Is64BitOperatingSystem) { throw "64-bit Windows required." }
if ($env:PROCESSOR_ARCHITECTURE -ne "AMD64") { throw "Only x64 (AMD64) Windows has prebuilt wheels. ARM64 Windows is NOT supported by this setup." }
try { $pyv = (python -c "import sys; print('%d.%d' % sys.version_info[:2])") } catch { throw "Python not found on PATH. Install Python 3.10-3.13 from python.org first (tick 'Add to PATH')." }
$parts = $pyv.Split("."); $maj = [int]$parts[0]; $min = [int]$parts[1]
if ($maj -ne 3 -or $min -lt 10 -or $min -gt 13) { throw "Python $pyv found; prebuilt CPU wheels exist only for Python 3.10-3.13. Install one of those." }

$Cache = if ($env:PIXOUS_MODEL_CACHE) { "$env:PIXOUS_MODEL_CACHE\chat" } else { "$env:LOCALAPPDATA\pixous-models\chat" }
$Repo = "bartowski/Qwen_Qwen3-4B-Instruct-2507-GGUF"
$Rev = "ae44f08e1392f39c0e474af10c3ff8355c8b6688"
$File = "Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf"
$Url = "https://huggingface.co/$Repo/resolve/$Rev/$File"
$ExpectedSize = 2497280736
$ExpectedSha = "2fde00ce69dd4899c70d020845e2638353015bba0fdf161b3eb965f2bca4464e"

New-Item -ItemType Directory -Force $Cache | Out-Null
$Dest = Join-Path $Cache $File

$need = $true
if (Test-Path $Dest) {
  if ((Get-Item $Dest).Length -eq $ExpectedSize) {
    $h = (certutil -hashfile $Dest SHA256 | Select-Object -Index 1).Replace(" ", "").ToLower()
    if ($h -eq $ExpectedSha) { $need = $false; Write-Host "Model already present, hash verified." } else { Write-Host "Existing file failed hash check - re-downloading."; Remove-Item $Dest }
  } else { Remove-Item $Dest }
}
if ($need) {
  Write-Host "Downloading $File (2.33 GiB, pinned rev) ..."
  curl.exe -fL --retry 3 -o "$Dest.part" $Url
  if ((Get-Item "$Dest.part").Length -ne $ExpectedSize) { Remove-Item "$Dest.part"; throw "Download size mismatch - incomplete file removed. Re-run the script." }
  $h = (certutil -hashfile "$Dest.part" SHA256 | Select-Object -Index 1).Replace(" ", "").ToLower()
  if ($h -ne $ExpectedSha) { Remove-Item "$Dest.part"; throw "Download hash mismatch - file removed. Do NOT use it; re-run the script." }
  Move-Item "$Dest.part" $Dest
  Write-Host "Download verified (size + SHA256)."
}

Write-Host ""
Write-Host "Installing the local server (pinned 0.3.19 - prebuilt Windows CPU wheel) ..."
python -m pip install --upgrade pip
python -m pip install --prefer-binary --extra-index-url https://abetlen.github.io/llama-cpp-python/whl/cpu "llama-cpp-python[server]==0.3.19"
if ($LASTEXITCODE -ne 0) { throw "pip install failed. See docs/v2/RUN-CHAT-ON-LAPTOP.md 'If the wheel install fails'." }

Write-Host ""
Write-Host "Model ready: $Dest"
Write-Host "Next: run  .\start-chat.ps1   then follow docs/v2/RUN-CHAT-ON-LAPTOP.md section 3-4."
