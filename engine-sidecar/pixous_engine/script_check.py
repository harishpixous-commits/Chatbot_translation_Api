"""Objective wrong-script check on translation OUTPUT. Never edits output.

Reports only that letters from a script that is neither the target's own script,
Latin, nor a script already present in the SOURCE item were produced. It does
not measure translation accuracy. Digits, punctuation, symbols and joiners are
not letters and are ignored. Languages without a known script are not checked.
"""
import unicodedata

TARGET_SCRIPT = {
    "en": "LATIN", "ta": "TAMIL", "hi": "DEVANAGARI", "mr": "DEVANAGARI", "ne": "DEVANAGARI",
    "sa": "DEVANAGARI", "kok": "DEVANAGARI", "mai": "DEVANAGARI", "doi": "DEVANAGARI",
    "te": "TELUGU", "ml": "MALAYALAM", "kn": "KANNADA", "bn": "BENGALI", "as": "BENGALI",
    "gu": "GUJARATI", "pa": "GURMUKHI", "or": "ORIYA",
}


# Only letters whose Unicode name starts with a known SCRIPT word are judged. Everything else (combining
# marks, modifier letters such as U+02BC, ordinal indicators, unnamed code points) is treated as common and
# never flagged: a false positive on legitimate text is worse here than a missed rare script.
KNOWN_SCRIPTS = {
    "LATIN", "TAMIL", "DEVANAGARI", "TELUGU", "MALAYALAM", "KANNADA", "BENGALI", "GUJARATI", "GURMUKHI",
    "ORIYA", "SINHALA", "ARABIC", "HEBREW", "CYRILLIC", "GREEK", "ARMENIAN", "GEORGIAN", "THAI", "LAO",
    "TIBETAN", "MYANMAR", "KHMER", "ETHIOPIC", "SYRIAC", "THAANA", "MEETEI", "OL", "CJK", "HIRAGANA",
    "KATAKANA", "HANGUL", "LEPCHA", "LIMBU", "SAURASHTRA", "BRAHMI", "GRANTHA", "SHARADA", "NKO",
}


def _script(ch: str) -> str | None:
    if unicodedata.category(ch)[0] not in ("L", "M"):
        return None
    try:
        first = unicodedata.name(ch).split()[0]
    except ValueError:
        return None
    return first if first in KNOWN_SCRIPTS else None


def check_items(target: str, sources: list[str], outputs: list[str]) -> dict:
    """Return {"checked": bool, "script_anomaly_items": [indices]}."""
    want = TARGET_SCRIPT.get(target.split("-")[0].lower())
    if want is None:
        return {"checked": False, "script_anomaly_items": []}
    bad = []
    for i, (src, out) in enumerate(zip(sources, outputs)):
        allowed = {want, "LATIN"} | {s for s in map(_script, src) if s}
        if any((s := _script(c)) is not None and s not in allowed for c in out):
            bad.append(i)
    return {"checked": True, "script_anomaly_items": bad}
