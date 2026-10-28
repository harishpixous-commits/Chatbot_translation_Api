$ErrorActionPreference = "Continue"
$script:pass = 0; $script:fail = 0; $script:results = @()
function Check($name, $cond, $detail) {
  if ($cond) { $script:pass++; $script:results += "PASS $name" }
  else { $script:fail++; $script:results += "FAIL $name :: $detail" }
}

$Setup = "/tmp/fixwork/chat-sidecar/setup-chat.ps1"
$Start = "/tmp/fixwork/chat-sidecar/start-chat.ps1"
$ExpectedSha = "2fde00ce69dd4899c70d020845e2638353015bba0fdf161b3eb965f2bca4464e"
$ExpectedSize = 2497280736

# ---- parser validation (real PowerShell parser) ----
foreach ($f in @($Setup, $Start)) {
  $toks = $null; $errs = $null
  [System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$toks, [ref]$errs) > $null
  Check "parse $(Split-Path $f -Leaf)" ($errs.Count -eq 0) ($errs | Out-String)
}

# ---- mocks ----
$script:MockPyVersion = "3.12"
$script:MockPyPresent = $true
$script:MockHashGood = $true
$script:MockCurlMakesFullSize = $true
$script:MockCurlCalled = $false
$script:MockServerArgs = $null

function global:python {
  if (-not $script:MockPyPresent) { throw "python : The term 'python' is not recognized" }
  if ($args[0] -eq "-c") { Write-Output $script:MockPyVersion; return }
  if ($args[0] -eq "-m" -and $args[1] -eq "pip") { $global:LASTEXITCODE = 0; return }
  if ($args[0] -eq "-m" -and $args[1] -eq "llama_cpp.server") { $script:MockServerArgs = ($args | Select-Object -Skip 2) -join " "; return }
}
function global:curl.exe {
  $script:MockCurlCalled = $true
  $out = $args[[Array]::IndexOf($args, "-o") + 1]
  $fs = [System.IO.File]::Create($out)
  if ($script:MockCurlMakesFullSize) { $fs.SetLength($ExpectedSize) } else { $fs.SetLength(1024) }
  $fs.Close()
}
function global:certutil {
  if ($script:MockHashGood) { Write-Output "SHA256 hash of x:"; Write-Output "$ExpectedSha"; Write-Output "CertUtil: -hashfile command completed successfully." }
  else { Write-Output "SHA256 hash of x:"; Write-Output "deadbeef"; Write-Output "CertUtil: -hashfile command completed successfully." }
}

function Run-Script($path) {
  $script:out = ""
  try { . $path *>&1 | ForEach-Object { $script:out += "$_`n" } | Out-Null; return "OK" }
  catch { $script:out += "$($_.Exception.Message)`n"; return $_.Exception.Message }
}

$env:PIXOUS_MODEL_CACHE = "/tmp/pstest/cache"
Remove-Item -Recurse -Force /tmp/pstest/cache -ErrorAction SilentlyContinue

# T1 happy path
$env:PROCESSOR_ARCHITECTURE = "AMD64"
$r = Run-Script $Setup
Check "T1 happy-path completes" ($r -eq "OK" -and $script:out -match "Download verified" -and $script:out -match "Model ready") $script:out
Check "T1 happy-path used curl" $script:MockCurlCalled ""

# T2 idempotent re-run with good existing file (curl must NOT be called)
$script:MockCurlCalled = $false
$r = Run-Script $Setup
Check "T2 rerun skips download, hash verified" ($r -eq "OK" -and $script:out -match "already present, hash verified" -and -not $script:MockCurlCalled) $script:out

# T3 hash mismatch on existing file -> redownload; then also bad download hash -> refuse
$script:MockHashGood = $false
Remove-Item -Recurse -Force /tmp/pstest/cache -ErrorAction SilentlyContinue
$r = Run-Script $Setup
Check "T3 bad hash fails closed, file removed" ($r -match "hash mismatch" -and -not (Test-Path "/tmp/pstest/cache/chat/Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf") -and -not (Test-Path "/tmp/pstest/cache/chat/Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf.part")) $script:out
$script:MockHashGood = $true

# T4 incomplete download (size mismatch) -> refuse + removed
$script:MockCurlMakesFullSize = $false
Remove-Item -Recurse -Force /tmp/pstest/cache -ErrorAction SilentlyContinue
$r = Run-Script $Setup
Check "T4 short download fails closed, partial removed" ($r -match "size mismatch" -and -not (Test-Path "/tmp/pstest/cache/chat/Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf.part")) $script:out
$script:MockCurlMakesFullSize = $true

# T5 ARM64 -> unsupported
$env:PROCESSOR_ARCHITECTURE = "ARM64"
$r = Run-Script $Setup
Check "T5 ARM64 refused" ($r -match "ARM64") $script:out
$env:PROCESSOR_ARCHITECTURE = "AMD64"

# T6 python missing
$script:MockPyPresent = $false
$r = Run-Script $Setup
Check "T6 missing python refused" ($r -match "Python not found") $script:out
$script:MockPyPresent = $true

# T7 python 3.9 out of wheel range
$script:MockPyVersion = "3.9"
$r = Run-Script $Setup
Check "T7 python 3.9 refused" ($r -match "3.10-3.13") $script:out
$script:MockPyVersion = "3.12"

# T8 python 3.14 out of wheel range
$script:MockPyVersion = "3.14"
$r = Run-Script $Setup
Check "T8 python 3.14 refused" ($r -match "3.10-3.13") $script:out
$script:MockPyVersion = "3.12"

# T9 start-chat without model -> tells to run setup
Remove-Item -Recurse -Force /tmp/pstest/cache -ErrorAction SilentlyContinue
$r = Run-Script $Start
Check "T9 start without model refused" ($r -match "setup-chat") $script:out

# T10 start-chat with model -> correct server args, default ctx 4096
New-Item -ItemType Directory -Force /tmp/pstest/cache/chat | Out-Null
[System.IO.File]::WriteAllText("/tmp/pstest/cache/chat/Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf", "x")
$r = Run-Script $Start
Check "T10 server args host/port/ctx" ($script:MockServerArgs -match "--host 127\.0\.0\.1" -and $script:MockServerArgs -match "--port 8085" -and $script:MockServerArgs -match "--n_ctx 4096" -and $script:MockServerArgs -match "--model .*Qwen_Qwen3-4B-Instruct-2507-Q4_K_M\.gguf") $script:MockServerArgs

# T11 PIXOUS_CHAT_CTX override honored
$env:PIXOUS_CHAT_CTX = "2048"
$r = Run-Script $Start
Check "T11 ctx override honored" ($script:MockServerArgs -match "--n_ctx 2048") $script:MockServerArgs
Remove-Item Env:PIXOUS_CHAT_CTX

Write-Host ""
$script:results | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "TOTAL pass=$($script:pass) fail=$($script:fail)"
