#!/usr/bin/env python3
"""Engine-sidecar behavioral tests. Plain stdlib+requests runner (no pytest
dependency) so it runs identically on a dev box and CI.

Targets a RUNNING sidecar (default http://127.0.0.1:8765, override with
SIDECAR_URL). Validation/bounds/auth tests never load a model. Translation
tests use the REAL IndicTrans2 models (no stubs): the smoke test is always on,
the indic->indic pivot test runs with --slow (loads both models).

Auth tests need a second instance on :8766 started with PIXOUS_ENGINE_TOKEN
set (see tests/run.sh); they are skipped with a note when absent.
"""
import os
import sys

import requests

# Windows consoles default to cp1252 and crash on Indic output; force UTF-8.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

BASE = os.environ.get("SIDECAR_URL", "http://127.0.0.1:8765")
AUTH_BASE = os.environ.get("SIDECAR_AUTH_URL", "http://127.0.0.1:8766")
TOKEN = "test-secret-token"

failures = []


def check(name, ok, detail=""):
    print(("PASS  " if ok else "FAIL  ") + name + ("  | " + str(detail) if detail else ""))
    if not ok:
        failures.append(name)


def main():
    slow = "--slow" in sys.argv

    # --- health / discovery (no model load) ---
    r = requests.get(BASE + "/health", timeout=10)
    h = r.json()
    check("health 200 + engine identity", r.status_code == 200 and h.get("engine") == "indictrans2-ct2", h.get("engine"))
    check("health reports auth mode", h.get("auth") in ("token", "none (localhost default)"), h.get("auth"))

    r = requests.get(BASE + "/languages", timeout=10)
    langs = {l["code"]: l for l in r.json()["languages"]}
    check("languages endpoint lists 22 Indic + en", len(langs) == 23, len(langs))
    check("priority six present with flores codes",
          all(langs.get(c, {}).get("flores") for c in ("en", "ta", "hi", "te", "ml", "kn")))
    check("ta flores is tam_Taml", langs.get("ta", {}).get("flores") == "tam_Taml")

    # --- request-shape validation (422, no model load) ---
    r = requests.post(BASE + "/translate", json={"source": "e", "target": "ta", "items": ["x"]}, timeout=10)
    check("source shorter than 2 chars rejected 422", r.status_code == 422, r.status_code)
    r = requests.post(BASE + "/translate", json={"source": "en", "target": "ta", "items": []}, timeout=10)
    check("empty items list rejected 422", r.status_code == 422, r.status_code)
    r = requests.post(BASE + "/translate", json={"source": "en", "target": "ta"}, timeout=10)
    check("missing items field rejected 422", r.status_code == 422, r.status_code)

    # --- hard bounds (413, no model load) ---
    r = requests.post(BASE + "/translate", json={"source": "en", "target": "ta", "items": ["x"] * 257}, timeout=10)
    check("items > 256 rejected 413", r.status_code == 413, r.status_code)
    r = requests.post(BASE + "/translate", json={"source": "en", "target": "ta", "items": ["x" * 2001]}, timeout=10)
    check("item > 2000 chars rejected 413", r.status_code == 413, r.status_code)
    r = requests.post(BASE + "/translate", json={"source": "en", "target": "ta", "items": ["x" * 1990] * 101}, timeout=10)
    check("total > 200000 chars rejected 413", r.status_code == 413, r.status_code)

    # --- language-pair handling ---
    r = requests.post(BASE + "/translate", json={"source": "zz", "target": "ta", "items": ["hello"]}, timeout=10)
    check("unknown language rejected 400", r.status_code == 400, r.status_code)
    r = requests.post(BASE + "/translate", json={"source": "ta", "target": "ta", "items": ["same", "language"]}, timeout=10)
    ok = r.status_code == 200 and r.json()["translations"] == ["same", "language"] and r.json()["models"] == []
    check("same-language short-circuit echoes without a model", ok, r.status_code)
    r = requests.post(BASE + "/translate", json={"source": "eng_Latn", "target": "eng_Latn", "items": ["flores"]}, timeout=10)
    check("raw flores codes accepted", r.status_code == 200, r.status_code)

    # --- real-engine smoke (loads en-indic model) ---
    r = requests.post(BASE + "/translate", json={"source": "en", "target": "ta", "items": ["Hello", "Good morning"]}, timeout=300)
    if r.status_code != 200:
        check("real en->ta translation", False, "status " + str(r.status_code))
    else:
        d = r.json()
        check("real en->ta translation returns 2 items", len(d["translations"]) == 2, d["translations"])
        check("real en->ta output is non-ASCII Tamil (not echo)",
              all(any(ord(c) > 127 for c in t) for t in d["translations"]), d["translations"])
        check("provider stamped PixousSelfHosted", d.get("provider") == "PixousSelfHosted")
        check("direct pair not marked pivot", d.get("pivot") is False)
        check("resident model reported", "en-indic" in requests.get(BASE + "/health", timeout=10).json()["resident_models"])

    # --- auth instance (optional, needs token-configured server on :8766) ---
    try:
        requests.get(AUTH_BASE + "/health", timeout=2)
        auth_up = True
    except requests.RequestException:
        auth_up = False
    if not auth_up:
        print("SKIP  auth tests (no token-configured instance on " + AUTH_BASE + ")")
    else:
        r = requests.post(AUTH_BASE + "/translate", json={"source": "zz", "target": "ta", "items": ["x"]}, timeout=10)
        check("no token rejected 401", r.status_code == 401, r.status_code)
        r = requests.post(AUTH_BASE + "/translate", headers={"authorization": "Bearer wrong"}, json={"source": "zz", "target": "ta", "items": ["x"]}, timeout=10)
        check("wrong token rejected 401", r.status_code == 401, r.status_code)
        r = requests.post(AUTH_BASE + "/translate", headers={"authorization": "Bearer " + TOKEN}, json={"source": "zz", "target": "ta", "items": ["x"]}, timeout=10)
        check("correct token passes auth (400 = reached validation)", r.status_code == 400, r.status_code)
        r = requests.get(AUTH_BASE + "/health", timeout=10)
        check("health stays unauthenticated by design", r.status_code == 200 and r.json().get("auth") == "token")

    # --- slow: indic->indic pivot through English (loads both models) ---
    if slow:
        r = requests.post(BASE + "/translate", json={"source": "ta", "target": "hi", "items": ["வணக்கம்"]}, timeout=600)
        if r.status_code != 200:
            check("pivot ta->hi", False, "status " + str(r.status_code))
        else:
            d = r.json()
            check("pivot ta->hi flagged pivot=true with bridge eng_Latn",
                  d.get("pivot") is True and d.get("bridge") == "eng_Latn" and len(d["translations"]) == 1,
                  {k: d.get(k) for k in ("pivot", "bridge", "models")})
            check("pivot output is Devanagari (not echo)",
                  any("\u0900" <= c <= "\u097f" for c in d["translations"][0]), d["translations"])

    print()
    if failures:
        print(str(len(failures)) + " FAILURES: " + ", ".join(failures))
        sys.exit(1)
    print("ALL SIDECAR TESTS PASSED")


if __name__ == "__main__":
    main()
