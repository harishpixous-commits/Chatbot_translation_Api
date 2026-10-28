# Running the self-hosted chatbot on the owner's existing laptop

Status 2026-10-27: owner confirmed the target machine is his existing laptop,
16 GB RAM, WINDOWS (WhatsApp). No purchase, no new host, no paid inference:
everything below runs locally. Windows is the primary path; Linux notes kept.
RAM alone does not establish CPU/GPU speed or answer quality - the on-host
validation battery in section 4 is REQUIRED before any real use.

## 0. What runs where (all on the laptop, nothing external)

- Translation engine sidecar (already shipped): ~1.4 GB resident
- Chat model server (this doc): ~3-4 GB resident at n_ctx 4096
- SampleApi (.NET): ~0.3 GB
- OS + browser: ~6-8 GB
=> Fits a 16 GB machine with headroom. All totals are ESTIMATES derived from
measured sandbox overhead + observed file sizes, not measured on this laptop.

## 1. Get the model (public, no login)

Prerequisites on the Windows laptop:
- Windows 10/11, **64-bit x64 (AMD64)**. ARM64 Windows (Snapdragon laptops) is
  **NOT supported**: prebuilt Python wheels are x64-only (verified 2026-10-27:
  zero win_arm64 wheels on the index).
- Python **3.10 - 3.13** on PATH (`python --version`). Prebuilt Windows CPU
  wheels for the local server exist ONLY for this range. The pinned wheel is
  llama-cpp-python **0.3.19** - the newest with a prebuilt Windows x64 CPU
  binary - from the abetlen wheel index
  (https://abetlen.github.io/llama-cpp-python/whl/cpu/). NOTE: PyPI itself
  ships llama-cpp-python as SOURCE ONLY (verified 2026-10-27: 0.3.36 has zero
  wheels); an earlier version of this doc wrongly said PyPI ships wheels.
- PowerShell and curl.exe (built into Windows 10+).
- .NET 10 SDK (free, dotnet.microsoft.com) to run the sample app.
- If pip ever falls back to a source build it needs "Desktop development with
  C++" from Visual Studio Build Tools - avoid that by staying in the
  Python 3.10-3.13 range.

**Validation status, stated honestly:** the scripts in `chat-sidecar/` have
**never been executed on Windows** (no Windows machine was available during
this work). What HAS been verified (2026-10-27, in a Linux sandbox):

1. **PowerShell parser + logic paths**: both .ps1 files parse cleanly under
   the real PowerShell 7.4.6 parser, and a mocked-command harness ran the
   scripts end-to-end: happy path, idempotent re-run (skips download when the
   hash verifies), fail-closed on download hash mismatch (file deleted),
   fail-closed on short download (partial deleted), ARM64 refused, missing
   Python refused, Python 3.9 and 3.14 refused (wheel range 3.10-3.13),
   start script refuses without model, correct server args (--host 127.0.0.1
   --port 8085 --n_ctx 4096) and PIXOUS_CHAT_CTX override. 14/14 checks pass.
   This validates script LOGIC, not Windows-specific behavior (certutil
   output format, curl.exe flags, Windows paths were mocked to spec).
2. **Dependency resolution**: the exact pinned install command resolves for
   Windows x64 / Python 3.12: pip pulled
   llama_cpp_python-0.3.19-cp312-cp312-win_amd64.whl plus all dependencies
   from the abetlen index. No compiler needed in the normal case.
3. **Pinned server version API**: llama-cpp-python 0.3.19 (the pinned wheel
   version) was installed and RUN here: same CLI flags the scripts use boot
   the server; GET /v1/models returns the OpenAI shape with the model id
   equal to the model file path; POST /v1/chat/completions works in both
   non-streaming and SSE streaming modes - the exact calls our app makes.
   (Run on Linux with a Qwen3-0.6B test model; the Windows-compiled binary
   itself remains unexecuted.)
4. **Download integrity inputs**: URL, pinned revision, expected size
   (2,497,280,736 bytes) and SHA256
   (2fde00ce69dd4899c70d020845e2638353015bba0fdf161b3eb965f2bca4464e)
   verified live against huggingface.co today; scripts verify size + SHA256
   after download and refuse a mismatched file.

**Remaining gap, kept open honestly:** first real execution on the owner's
Windows laptop (Windows-specific commands, antivirus/SmartScreen behavior,
actual performance on his CPU). The scripts are not an owner decision
blocker - they are a convenience; the manual commands below use the same
verified inputs. This delivery is a **setup candidate, not a proven-ready
claim.**

Downloads Qwen3-4B-Instruct-2507 Q4_K_M from
bartowski/Qwen_Qwen3-4B-Instruct-2507-GGUF, pinned rev ae44f08e (verified
2026-10-27: gated:false, upstream apache-2.0; third-party conversion -
provenance caveat in CHAT-MODEL-RECOMMENDATION.md applies).

### Manual commands (use these if you want to see each step, or if a script fails)

PowerShell, from any folder:

```
mkdir "$env:LOCALAPPDATA\pixous-models\chat" -Force
curl.exe -fL --retry 3 -o "$env:LOCALAPPDATA\pixous-models\chat\Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf" "https://huggingface.co/bartowski/Qwen_Qwen3-4B-Instruct-2507-GGUF/resolve/ae44f08e1392f39c0e474af10c3ff8355c8b6688/Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf"

$file = "$env:LOCALAPPDATA\pixous-models\chat\Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf"
$expected = "2fde00ce69dd4899c70d020845e2638353015bba0fdf161b3eb965f2bca4464e"
$actual = (certutil -hashfile $file SHA256 | Select-Object -Index 1).Replace(" ","").ToLower()
if ((Get-Item $file).Length -ne 2497280736 -or $actual -ne $expected) {
  Remove-Item $file
  throw "Fingerprint or size mismatch - file deleted. Run the download again."
}
Write-Host "Download verified."

python -m pip install --prefer-binary --extra-index-url https://abetlen.github.io/llama-cpp-python/whl/cpu "llama-cpp-python[server]==0.3.19"
```

Or the convenience scripts, which do the same with the checks built in:
- Windows (PowerShell): `chat-sidecar/setup-chat.ps1`
- Linux/macOS (bash):   `chat-sidecar/setup-chat.sh`

## 2. Start the chat server

Manual command (Windows PowerShell):

```
python -m llama_cpp.server --model "$env:LOCALAPPDATA\pixous-models\chat\Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf" --host 127.0.0.1 --port 8085 --n_ctx 4096
```

Or the scripts: Windows `chat-sidecar/start-chat.ps1`, Linux/macOS `chat-sidecar/start-chat.sh`.

Serves an OpenAI-compatible endpoint on http://127.0.0.1:8085 (llama-cpp-python).
The model id to put in config is whatever `GET /v1/models` reports (it is the
model file path). Cross-version note: our testing ran llama-cpp-python 0.3.36
on Linux; the pinned Windows wheel is 0.3.19. Cross-version behavior
differences are possible and UNVERIFIED - one more reason section 4 is required.
If RAM pressure appears: set PIXOUS_CHAT_CTX=2048 (measured here: ctx is the
main runtime-memory lever; 0.6B OOM'd at 4096, fit at 2048 in 2 GB).

Performance expectation (ESTIMATE, not measured): 4B Q4_K_M on a laptop CPU
is interactive but not instant - order 5-15 tokens/second depending on CPU.
A discrete GPU with 4+ GB VRAM offloads and is much faster; unknown whether
the laptop has one (pending owner answer). If too slow: fall back to
Qwen3-1.7B Q4_K_M (bartowski/Qwen_Qwen3-1.7B-GGUF, not pinned/verified here).

## 3. Point the app at it (config only, no code change)

Prerequisites: .NET 10 SDK on PATH (`dotnet --version`). In a SECOND
PowerShell window (keep the model server window open), from the extracted
package folder:

```
cd "<the folder where you extracted the package>"
$env:AiSuite__ChatProvider = "OpenAiCompatible"
$env:AiSuite__OpenAiCompatible__Endpoint = "http://127.0.0.1:8085/v1"
$env:AiSuite__OpenAiCompatible__Model = "<model id reported by GET /v1/models>"
$env:AiSuite__OpenAiCompatible__RequireApiKey = "false"
$env:AiSuite__OpenAiCompatible__TimeoutSeconds = "300"
dotnet run --project dotnet/samples/SampleApi --urls http://localhost:5199
```

Then open http://localhost:5199/demo.html - that is the chatbot.
$env: settings live only in that PowerShell window; paste them again before
starting the app from a new window.

TimeoutSeconds is set on the local-model provider only (300s headroom): the
60s global default is a billing guard for hosted vendors and is left
untouched. MEASURED: one short Qwen3-4B Q4_K_M reply took 85.5s on a 2-vCPU
shared CI runner (server warm) - a 60s cap turns a slow-but-working local
reply into a 502. A laptop CPU is typically faster than that runner, but
measure on your own machine (the validation battery below times it) and
adjust; the override is not infinite patience - a hung server still fails
closed at 300s.

Think-tag stripping ships ON by default (StripThinkTags) - verified at 0.6B.

## 4. REQUIRED on-host validation battery (before any real use)

Run each through POST /api/ai/chat and judge the reply:
1. EN instruction-following: "List exactly 3 colors as a numbered list."
   -> exactly 3 numbered items.
2. EN grounding: "What services does Pixous offer?" -> answers from the
   configured system prompt knowledge, no invented contact details.
3. TA factual probe: "தமிழில் பதில் சொல்: இந்தியாவின் தலைநகர் எது?"
   -> must answer புது தில்லி (New Delhi). (0.6B failed this - measured.)
4. HI factual probe: "हिंदी में उत्तर दें: भारत की राजधानी क्या है?"
   -> must answer नई दिल्ली. (0.6B failed this - measured.)
5. Format check: no <think> tags visible in any reply.
6. Language consistency: reply language follows the user message language.

If 3/4/6 fail: the 4B path is not proven on this machine - try the 8B option
(4.68 GiB weights, ~16 GB total, tight but plausible) or keep KnowledgeBase
provider. Vendor benchmarks are NOT a substitute for this battery.

IMPORTANT - what this battery does and does not prove: passing all six checks
means the 4B setup is worth piloting on this laptop; it does NOT establish
production readiness. Six prompts cannot measure general answer quality.
Before real users touch it, run a broader review with native speakers of
Tamil/Hindi (and any other target languages) against realistic questions from
your actual use case. That native-speaker review stays open regardless of
battery results.
