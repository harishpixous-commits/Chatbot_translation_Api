"""Model lifecycle manager for the Pixous translation engine sidecar.

Loads IndicTrans2 CT2 models on demand, keeps a bounded number resident
(default 1: a low-memory host cannot hold two), and translates through
English as a pivot for Indic-to-Indic pairs.

Honest limits (do not remove from docs):
- Indic<->Indic pairs are translated via an English pivot. Pivoting is the
  chosen implementation because the CT2 ports we ship instructions for cover
  en-indic and indic-en only; pivoting compounds error and is measurably
  worse than a direct pair on hard sentences. Callers see pivot=true.
- CPU int8 inference. Observed on a 1 vCPU / 1 GB sandbox: process high-water
  mark ~1.03-1.06 GB while a model was resident. That is one observation on
  one box, not a supported floor: production sizing is unverified.
"""
from __future__ import annotations

import gc
import os
import sys
import threading
from collections import OrderedDict
from pathlib import Path

_HERE = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(_HERE))

FLORES_BY_ISO = {
    "en": "eng_Latn", "ta": "tam_Taml", "hi": "hin_Deva", "te": "tel_Telu",
    "ml": "mal_Mlym", "kn": "kan_Knda", "as": "asm_Beng", "bn": "ben_Beng",
    "brx": "brx_Deva", "doi": "doi_Deva", "gu": "guj_Gujr", "gom": "gom_Deva",
    "ks": "kas_Arab", "mai": "mai_Deva", "mr": "mar_Deva", "mni": "mni_Beng",
    "ne": "npi_Deva", "or": "ory_Orya", "pa": "pan_Guru", "sa": "san_Deva",
    "sat": "sat_Olck", "sd": "snd_Arab", "ur": "urd_Arab",
}
ISO_BY_FLORES = {v: k for k, v in FLORES_BY_ISO.items()}

# Friendly names for the six priority languages; others get their flores code.
PRIORITY_NAMES = {
    "en": "English", "ta": "Tamil", "hi": "Hindi",
    "te": "Telugu", "ml": "Malayalam", "kn": "Kannada",
}

_EN_INDIC_REPO = "models--adalat-ai--ct2-rotary-indictrans2-en-indic-dist-200M"
_EN_INDIC_REV = "ee04de9a86e42920ac310a0fe5a1505c85671b68"
_EN_INDIC_SUB = "en-indic-200m-ct2/ctranslate2_model"
_INDIC_EN_REPO = "models--adalat-ai--ct2-rotary-indictrans2-indic-en-dist-200M"
_INDIC_EN_REV = "524aade4f724dccbc6cd5ffb5e3c5c832a2baee4"
_INDIC_EN_SUB = "indic-en-200m-ct2/ctranslate2_model"


def _model_dir(repo: str, rev: str, sub: str) -> str:
    explicit = os.environ.get(
        "PIXOUS_EN_INDIC_DIR" if "en-indic" in sub else "PIXOUS_INDIC_EN_DIR")
    if explicit:
        return explicit
    cache = os.environ.get("PIXOUS_MODEL_CACHE", "/tmp/hfcache")
    return str(Path(cache) / repo / "snapshots" / rev / sub)


def _load_model(kind: str):
    import ctranslate2  # noqa: PLC0415

    original = ctranslate2.Translator

    def patched(path, device="cpu", **kw):
        return original(path, device="cpu", compute_type="int8",
                        inter_threads=1, intra_threads=1, **kw)

    ctranslate2.Translator = patched
    try:
        from indictrans2_inference.engine import Model  # noqa: PLC0415

        if kind == "en-indic":
            ckpt = _model_dir(_EN_INDIC_REPO, _EN_INDIC_REV, _EN_INDIC_SUB)
        else:
            ckpt = _model_dir(_INDIC_EN_REPO, _INDIC_EN_REV, _INDIC_EN_SUB)
        if not Path(ckpt).is_dir():
            raise FileNotFoundError(
                f"model directory missing: {ckpt} (run setup.sh)")
        return Model(ckpt, device="cpu")
    finally:
        ctranslate2.Translator = original


class EngineManager:
    """Bounded-residency model cache + pivot routing. Thread-safe."""

    def __init__(self) -> None:
        self.max_resident = int(os.environ.get("PIXOUS_MAX_RESIDENT_MODELS", "1"))
        self._lock = threading.Lock()
        self._models: OrderedDict[str, object] = OrderedDict()

    @staticmethod
    def _kind(src_flores: str, tgt_flores: str) -> str:
        return "indic-en" if tgt_flores == "eng_Latn" else "en-indic"

    def _get(self, kind: str):
        if kind in self._models:
            self._models.move_to_end(kind)
            return self._models[kind]
        while len(self._models) >= self.max_resident:
            _, evicted = self._models.popitem(last=False)
            del evicted
            gc.collect()
        model = _load_model(kind)
        self._models[kind] = model
        return model

    def supported_languages(self) -> list[dict]:
        return [
            {"code": iso, "flores": flores,
             "name": PRIORITY_NAMES.get(iso, flores)}
            for iso, flores in sorted(FLORES_BY_ISO.items())
        ]

    def resident_models(self) -> list[str]:
        return list(self._models.keys())

    def translate(self, source: str, target: str, items: list[str]) -> dict:
        src = FLORES_BY_ISO.get(source, source)
        tgt = FLORES_BY_ISO.get(target, target)
        if src not in ISO_BY_FLORES or tgt not in ISO_BY_FLORES:
            raise ValueError(f"unsupported language pair: {source}->{target}")
        if src == tgt:
            return {"translations": list(items), "pivot": False,
                    "models": [], "source": src, "target": tgt}
        with self._lock:
            if "eng_Latn" in (src, tgt):
                model = self._get(self._kind(src, tgt))
                out = model.batch_translate(items, src, tgt)
                return {"translations": out, "pivot": False,
                        "models": [self._kind(src, tgt)],
                        "source": src, "target": tgt}
            # Indic<->Indic: pivot through English (see module docstring).
            to_en = self._get("indic-en")
            bridge = to_en.batch_translate(items, src, "eng_Latn")
            from_en = self._get("en-indic")
            out = from_en.batch_translate(bridge, "eng_Latn", tgt)
            return {"translations": out, "pivot": True,
                    "models": ["indic-en", "en-indic"],
                    "source": src, "target": tgt, "bridge": "eng_Latn"}
