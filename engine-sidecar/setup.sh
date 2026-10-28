#!/usr/bin/env bash
# Downloads the pinned CT2 model revisions the sidecar serves.
# DEFAULT ROUTE NEEDS NO HUGGING FACE ACCOUNT OR TOKEN: the pinned adalat-ai
# repos are public (gated:false, license:mit, re-verified 2026-10-27).
# The official ai4bharat weights repos are gated (login + access request) and
# are NOT required for this setup; see README.md "Model provenance" for the
# official-route status and the optional provenance upgrade path.
# Weights are fetched from the publishers' Hugging Face repos; review each
# repo's license before redistribution (see README.md "Model provenance").
set -euo pipefail
# Engine resolves models at $PIXOUS_MODEL_CACHE/models--<repo>/snapshots/<rev>,
# so point HF_HUB_CACHE (not just HF_HOME, which would append /hub) at the
# same directory. Keep both for any huggingface_hub code path.
export PIXOUS_MODEL_CACHE="${PIXOUS_MODEL_CACHE:-/tmp/hfcache}"
export HF_HUB_CACHE="$PIXOUS_MODEL_CACHE"
export HF_HOME="$PIXOUS_MODEL_CACHE"
pip3 install -r requirements.txt
python3 - <<'PY'
import nltk
for pkg in ("punkt", "punkt_tab"):
    nltk.download(pkg, quiet=True)
from huggingface_hub import snapshot_download
for repo, rev in [
    ("adalat-ai/ct2-rotary-indictrans2-en-indic-dist-200M",
     "ee04de9a86e42920ac310a0fe5a1505c85671b68"),
    ("adalat-ai/ct2-rotary-indictrans2-indic-en-dist-200M",
     "524aade4f724dccbc6cd5ffb5e3c5c832a2baee4"),
]:
    path = snapshot_download(repo, revision=rev)
    print("downloaded", repo, "->", path)
PY
echo "setup complete"
