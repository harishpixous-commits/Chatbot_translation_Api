# Starts the local OpenAI-compatible chat server on 127.0.0.1:8085 (Windows x64).
# STATUS: authored, not executed on Windows. If it fails, run the same command
# manually from docs/v2/RUN-CHAT-ON-LAPTOP.md.
$Cache = if ($env:PIXOUS_MODEL_CACHE) { "$env:PIXOUS_MODEL_CACHE\chat" } else { "$env:LOCALAPPDATA\pixous-models\chat" }
$Model = Join-Path $Cache "Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf"
if (-not (Test-Path $Model)) { throw "Model not found at $Model - run .\setup-chat.ps1 first." }
$Ctx = if ($env:PIXOUS_CHAT_CTX) { $env:PIXOUS_CHAT_CTX } else { 4096 }
python -m llama_cpp.server --model $Model --host 127.0.0.1 --port 8085 --n_ctx $Ctx
