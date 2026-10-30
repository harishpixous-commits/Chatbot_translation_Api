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
import re
import sys
import threading
import time
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


_LINE_PREFIX = re.compile(r"^(\s*(?:[-*\u2022]|\d{1,3}[.)]|#{1,6})\s+)?(.*?)(\s*)$")


def _split_lines(item: str) -> list[tuple[str, str, str]]:
    """Split one item into (prefix, text, trailing-space) per line.

    Sentence models flatten newlines and drop list markers, which destroys
    the Markdown lists chat replies use. Translating each line's text on
    its own and re-attaching the original prefix keeps structure intact.
    """
    out = []
    for line in item.split("\n"):
        m = _LINE_PREFIX.match(line)
        out.append((m.group(1) or "", m.group(2), m.group(3)))
    return out


# Spans the sentence models corrupt: pictographs/emoji (become stray
# symbols), `inline code` and **bold** markers. They are masked with "[n]"
# placeholders INSIDE the line so the model still sees the whole sentence
# (verb/object context is kept), then restored. Restoration is checked: each
# placeholder must come back exactly once, else that line falls back to
# translating the text between the spans separately (still lossless).
_SPAN = re.compile(
    "[\U0001F000-\U0001FAFF\u2600-\u27BF\u2B00-\u2BFF\uFE0F\u200D]+"
    "|`[^`\n]+`"
    "|\\*\\*[^*\n]+\\*\\*"
    "|(?<![*\\w])\\*[^*\\s][^*\n]*\\*(?![*\\w])")   # *italic*
_PH = re.compile(r"\[\d{1,3}\]")


def _spans(text: str, rules):
    """Yield (start, end, kind, restore) for protected spans, incl. opt-in
    glossary hits. A glossary rule only fires when its `when` cue regex also
    matches the same line (sense scoping); no rules = no behaviour change."""
    found = [(m.start(), m.end(), "span", m.group()) for m in _SPAN.finditer(text)]
    for pat, repl, when in rules or ():
        if when is not None and not when.search(text):
            continue
        for m in pat.finditer(text):
            if not any(m.start() < e and s_ < m.end() for s_, e, _, _ in found):
                found.append((m.start(), m.end(), "gloss", repl))
    return sorted(found)


# ---- bounded translation -------------------------------------------------
# The ct2 engine silently TRUNCATES in two places: the bundled preprocessing
# cuts at 256 pieces and ct2 max_input_length cuts at 160 (language tags and
# </s> included). A 30-sentence item came back with ~15 sentences and a
# success status. Every model call therefore goes through _bounded_translate:
# text is cut into chunks whose REAL model input (normalised, spm-encoded, +2
# language tags +</s>) is within _TOKEN_BUDGET, all chunks are translated and
# the output is rejoined in order. Nothing is dropped, duplicated or passed
# through untranslated: an over-long unbroken run is cut at the longest
# prefix that fits (joiner "" so no space is invented). Work that cannot
# finish is REFUSED (chunk cap -> ValueError, time budget -> TimeoutError)
# rather than answered partially.
_TOKEN_BUDGET = int(os.environ.get("PIXOUS_TOKEN_BUDGET", "80"))   # < ct2 160
_MAX_CHUNKS = int(os.environ.get("PIXOUS_MAX_CHUNKS", "1500"))
_MAX_SECONDS = float(os.environ.get("PIXOUS_MAX_REQUEST_SECONDS", "90"))
_GROUP = int(os.environ.get("PIXOUS_CHUNK_GROUP", "4"))
_SENT_SPLIT = re.compile(r"(?<=[.!?\u0964\u0965])\s+")


def _ntokens(model, text: str, src: str) -> int:
    """Exact count of what ct2 will receive for this text (no truncation)."""
    pre, _ = model.preprocess([text], lang=src)
    return len(model.apply_spm(pre)[0].split()) + 3


def _fit_prefix(model, text: str, src: str) -> int:
    """Longest prefix length of `text` whose token count fits the budget."""
    lo, hi = 1, len(text)
    while lo < hi:
        mid = (lo + hi + 1) // 2
        if _ntokens(model, text[:mid], src) <= _TOKEN_BUDGET:
            lo = mid
        else:
            hi = mid - 1
    return lo


def _chunk_text(model, text: str, src: str) -> list[tuple[str, str]]:
    """-> [(chunk, joiner-before)]; ''.join(joiner+chunk) rebuilds the text
    up to whitespace normalisation."""
    out: list[tuple[str, str]] = []
    cur = ""

    def flush():
        nonlocal cur
        if cur:
            out.append((cur, " " if out else ""))
            cur = ""

    for sent in _SENT_SPLIT.split(text.strip()):
        if not sent:
            continue
        if cur and _ntokens(model, cur + " " + sent, src) <= _TOKEN_BUDGET:
            cur += " " + sent
            continue
        flush()
        if _ntokens(model, sent, src) <= _TOKEN_BUDGET:
            cur = sent
            continue
        for word in sent.split(" "):          # over-long sentence: pack words
            if _ntokens(model, word, src) > _TOKEN_BUDGET:   # unbroken run
                flush()
                rest, first = word, True
                while rest:
                    k = (_fit_prefix(model, rest, src)
                         if _ntokens(model, rest, src) > _TOKEN_BUDGET else len(rest))
                    out.append((rest[:k], (" " if out else "") if first else ""))
                    first, rest = False, rest[k:]
                continue
            if cur and _ntokens(model, cur + " " + word, src) > _TOKEN_BUDGET:
                flush()
            cur = (cur + " " + word) if cur else word
    flush()
    return out


def _bounded_translate(model, texts: list[str], src: str, tgt: str,
                       stats: dict | None = None) -> list[str]:
    plans = [_chunk_text(model, t, src) if t.strip() else [] for t in texts]
    jobs = [c for plan in plans for c, _ in plan]
    if len(jobs) > _MAX_CHUNKS:
        raise ValueError(
            f"request needs {len(jobs)} translation chunks (limit {_MAX_CHUNKS})")
    if stats is not None:
        stats["chunks"] = stats.get("chunks", 0) + len(jobs)
        stats["items_split"] = stats.get("items_split", 0) + sum(
            1 for p in plans if len(p) > 1)
    # groups: stop BETWEEN groups when out of time (cooperative budget; the
    # server also enforces a hard limit from outside the process)
    deadline = time.monotonic() + _MAX_SECONDS
    results: list[str] = []
    for i in range(0, len(jobs), _GROUP):
        if time.monotonic() > deadline:
            raise TimeoutError(
                f"translation exceeded its {_MAX_SECONDS:g}s time budget")
        results.extend(model.batch_translate(jobs[i:i + _GROUP], src, tgt))
    done = iter(results)
    out = []
    for t, plan in zip(texts, plans):
        out.append(t if not plan else "".join(jn + next(done) for _, jn in plan))
    return out


_GLOSS_MAX_RULES = 100
_GLOSS_MAX_PATTERN = 200
# a quantified group that itself contains a quantifier, e.g. (a+)+ or (.*)*
_NESTED_QUANT = re.compile(r"\((?:[^()\\]|\\.)*[+*](?:[^()\\]|\\.)*\)\s*[+*{]")


def load_glossary(path: str | None):
    """JSON list of {source,target,phrase,replacement,when?,note?}; opt-in."""
    if not path:
        return {}
    import json  # noqa: PLC0415
    data = json.load(open(path, encoding="utf-8"))
    if not isinstance(data, list) or len(data) > _GLOSS_MAX_RULES:
        raise ValueError(f"glossary must be a list of at most {_GLOSS_MAX_RULES} rules")
    rules: dict = {}
    for i, r in enumerate(data):
        for k in ("source", "target", "phrase", "replacement"):
            if not isinstance(r.get(k), str) or not r[k]:
                raise ValueError(f"glossary rule {i}: '{k}' must be a non-empty string")
        pats = [r["phrase"]] + ([r["when"]] if r.get("when") else [])
        for pat in pats:
            if len(pat) > _GLOSS_MAX_PATTERN or _NESTED_QUANT.search(pat):
                raise ValueError(
                    f"glossary rule {i}: pattern too long or has nested quantifiers "
                    "(possible catastrophic backtracking)")
        try:
            phrase = re.compile(r["phrase"], re.I)
            when = re.compile(r["when"], re.I) if r.get("when") else None
        except re.error as e:
            raise ValueError(f"glossary rule {i}: invalid regex ({e})") from None
        if phrase.search(""):
            raise ValueError(f"glossary rule {i}: phrase must not match the empty string")
        if _PH.search(r["replacement"]):
            raise ValueError(f"glossary rule {i}: replacement must not look like a [n] placeholder")
        rules.setdefault((r["source"], r["target"]), []).append((phrase, r["replacement"], when))
    return rules


def _mask(text: str, rules=None):
    """-> (masked text, [(placeholder, restore-string)]) or None if unsafe."""
    if _PH.search(text):
        return None  # literal "[1]" already in the source: cannot mask safely
    subs, n, out, pos = [], 0, [], 0
    for st, en, kind, g in _spans(text, rules):
        out.append(text[pos:st])
        if kind == "gloss":
            n += 1
            out.append("[%d]" % n)
            subs.append(("[%d]" % n, g))
        elif g.startswith("*"):
            mk = "**" if g.startswith("**") else "*"
            o, c = "[%d]" % (n + 1), "[%d]" % (n + 2)
            n += 2
            out.append(o + g[len(mk):-len(mk)] + c)
            subs += [(o, mk), (c, mk)]
        else:
            n += 1
            out.append("[%d]" % n)
            subs.append(("[%d]" % n, g))
        pos = en
    out.append(text[pos:])
    return "".join(out), subs


def _unmask(translated: str, subs):
    """Restore placeholders in ONE pass (a restored span such as `a[2]` or a
    glossary replacement containing "[2]" must never be re-substituted)."""
    for ph, _ in subs:
        if translated.count(ph) != 1:
            return None
    rep = dict(subs)
    # emphasis markers must hug their text to render: open eats following
    # space, close eats preceding space (odd/even position within the pair)
    for i, (ph, r) in enumerate(subs):
        if r.startswith("*") and i % 2 == 0:
            translated = re.sub(re.escape(ph) + r"\s+", ph, translated)
        elif r.startswith("*"):
            translated = re.sub(r"\s+" + re.escape(ph), ph, translated)
    return _PH.sub(lambda m: rep.get(m.group(), m.group()), translated)


def _split_spans(text: str, rules=None):
    segs, pos = [], 0
    for st, en, kind, g in _spans(text, rules):
        segs.append(("t", text[pos:st]))
        segs.append(("e", g if kind == "gloss" else text[st:en]))
        pos = en
    segs.append(("t", text[pos:]))
    return segs


def _translate_structured(model, items: list[str], src: str, tgt: str, rules=None,
                          stats: dict | None = None) -> list[str]:
    """batch_translate that preserves line breaks, list/heading markers,
    fenced code blocks, inline code, **bold** and emoji."""
    if not any("\n" in i or _SPAN.search(i) or _spans(i, rules) for i in items):
        return _bounded_translate(model, items, src, tgt, stats)
    plan, texts = [], []   # texts: model inputs; plan refers to them by index
    for item in items:
        lines, fenced = [], False
        for prefix, text, tail in _split_lines(item):
            if text.lstrip().startswith("```") or fenced:
                if text.lstrip().startswith("```"):
                    fenced = not fenced
                lines.append((prefix, "verbatim", text, tail))   # code is never translated
                continue
            if not text.strip():
                lines.append((prefix, "verbatim", text, tail))
                continue
            masked = _mask(text, rules)
            if masked is None and _spans(text, rules):
                lines.append((prefix, "masked", (None, [], text), tail))  # split path
            elif masked is None or not masked[1]:
                texts.append(text)
                lines.append((prefix, "plain", len(texts) - 1, tail))
            else:
                texts.append(masked[0])
                lines.append((prefix, "masked", (len(texts) - 1, masked[1], text), tail))
        plan.append(lines)
    done = _bounded_translate(model, texts, src, tgt, stats) if texts else []
    # lines whose placeholders did not survive: translate between the spans
    redo, redo_idx = [], {}
    for lines in plan:
        for _, kind, v, _ in lines:
            if kind == "masked" and (v[0] is None or _unmask(done[v[0]], v[1]) is None):
                for k, seg in _split_spans(v[2], rules):
                    if k == "t" and seg.strip() and (v[2], seg) not in redo_idx:
                        redo_idx[(v[2], seg)] = len(redo)
                        redo.append(seg.strip())
    redone = _bounded_translate(model, redo, src, tgt, stats) if redo else []
    out = []
    for lines in plan:
        rendered = []
        for prefix, kind, v, tail in lines:
            if kind == "verbatim":
                body = v
            elif kind == "plain":
                body = done[v]
            else:
                body = None if v[0] is None else _unmask(done[v[0]], v[1])
                if body is None:
                    parts = []
                    for k, seg in _split_spans(v[2], rules):
                        if k == "t" and seg.strip():
                            lead = seg[:len(seg) - len(seg.lstrip())]
                            trail = seg[len(seg.rstrip()):]
                            parts.append(lead + redone[redo_idx[(v[2], seg)]] + trail)
                        else:
                            parts.append(seg)
                    body = "".join(parts)
            rendered.append(prefix + body + tail)
        out.append("\n".join(rendered))
    return out


def _release_heap() -> None:
    """Return freed model memory to the OS (glibc keeps it otherwise, which made
    pivot pairs look like a leak)."""
    try:
        import ctypes
        ctypes.CDLL("libc.so.6").malloc_trim(0)
    except Exception:
        pass


class EngineManager:
    """Bounded-residency model cache + pivot routing. Thread-safe."""

    def __init__(self) -> None:
        self._glossary = load_glossary(os.environ.get("PIXOUS_GLOSSARY"))
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
            _release_heap()
        model = _load_model(kind)
        self._models[kind] = model
        return model

    def glossary_rule_count(self) -> int:
        return sum(len(v) for v in self._glossary.values())

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
        stats: dict = {}
        with self._lock:
            if "eng_Latn" in (src, tgt):
                model = self._get(self._kind(src, tgt))
                out = _translate_structured(
                    model, items, src, tgt,
                    self._glossary.get((ISO_BY_FLORES[src], ISO_BY_FLORES[tgt])), stats)
                return {"translations": out, "pivot": False, "segmentation": stats,
                        "models": [self._kind(src, tgt)],
                        "source": src, "target": tgt}
            # Indic<->Indic: pivot through English (see module docstring).
            to_en = self._get("indic-en")
            bridge = _translate_structured(to_en, items, src, "eng_Latn", None, stats)
            del to_en   # drop our reference, else the evicted model stays resident (2 models = ~1.3 GB)
            self._models.pop("indic-en", None)   # evict explicitly while we hold no reference
            gc.collect(); _release_heap()
            from_en = self._get("en-indic")
            out = _translate_structured(from_en, bridge, "eng_Latn", tgt, None, stats)
            return {"translations": out, "pivot": True, "segmentation": stats,
                    "models": ["indic-en", "en-indic"],
                    "source": src, "target": tgt, "bridge": "eng_Latn"}
