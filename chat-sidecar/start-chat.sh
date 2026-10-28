#!/usr/bin/env bash
# Starts the local OpenAI-compatible chat server on 127.0.0.1:8085.
# n_ctx 4096 default; lower to 2048 if RAM is tight (measured: 0.6B needed
# ~1.1-1.2 GB runtime on top of weights at n_ctx 4096 in a 2 GB sandbox).
set -euo pipefail
CACHE="${PIXOUS_MODEL_CACHE:-$HOME/.cache/pixous-models}/chat"
MODEL="$CACHE/Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf"
CTX="${PIXOUS_CHAT_CTX:-4096}"
exec python3 -m llama_cpp.server --model "$MODEL" --host 127.0.0.1 --port 8085 --n_ctx "$CTX"
