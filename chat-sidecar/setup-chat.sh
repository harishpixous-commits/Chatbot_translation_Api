#!/usr/bin/env bash
# Pixous Assistant - self-hosted chat model setup (Linux/macOS).
# Downloads the pinned public GGUF and verifies size + SHA256.
# No account, no login, no paid service.
set -euo pipefail
CACHE="${PIXOUS_MODEL_CACHE:-$HOME/.cache/pixous-models}/chat"
REPO="bartowski/Qwen_Qwen3-4B-Instruct-2507-GGUF"
REV="ae44f08e1392f39c0e474af10c3ff8355c8b6688"
FILE="Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf"
URL="https://huggingface.co/${REPO}/resolve/${REV}/${FILE}"
EXPECTED_SIZE=2497280736
EXPECTED_SHA="2fde00ce69dd4899c70d020845e2638353015bba0fdf161b3eb965f2bca4464e"

mkdir -p "$CACHE"
verify() {
  [ "$(stat -c%s "$1" 2>/dev/null || stat -f%z "$1")" = "$EXPECTED_SIZE" ] || return 1
  [ "$(sha256sum "$1" | cut -d' ' -f1)" = "$EXPECTED_SHA" ]
}
if [ -s "$CACHE/$FILE" ] && verify "$CACHE/$FILE"; then
  echo "Model already present, hash verified."
else
  rm -f "$CACHE/$FILE"
  echo "Downloading $FILE (2.33 GiB, pinned rev) ..."
  curl -fL --retry 3 -o "$CACHE/$FILE.part" "$URL"
  if ! verify "$CACHE/$FILE.part"; then
    rm -f "$CACHE/$FILE.part"
    echo "Download failed size/hash verification - file removed. Re-run the script." >&2
    exit 1
  fi
  mv "$CACHE/$FILE.part" "$CACHE/$FILE"
  echo "Download verified (size + SHA256)."
fi

echo "Installing the local server (llama-cpp-python) ..."
python3 -m pip install "llama-cpp-python[server]"

cat <<MSG

Model ready: $CACHE/$FILE
Next: run  ./start-chat.sh   then follow docs/v2/RUN-CHAT-ON-LAPTOP.md section 3-4.
MSG
