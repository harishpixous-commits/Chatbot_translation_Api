#!/usr/bin/env python3
"""Real-model completeness across directions: a 24-sentence numbered item (Sentence N markers with
distinct numbers) must return every number once, in order. Objective completeness only, NOT meaning."""
import os, re, subprocess, sys, time, requests
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
pairs = [p.split(">") for p in (sys.argv[1] if len(sys.argv) > 1 else "en>ta,en>hi,en>te,en>ml,en>kn").split(",")]
e = dict(os.environ, PYTHONPATH=ROOT, PIXOUS_MODEL_CACHE="/tmp/hfcache")
p = subprocess.Popen([sys.executable, "-m", "uvicorn", "pixous_engine.server:app", "--port", "8775"], cwd=ROOT, env=e, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
fails = 0
try:
    for _ in range(60):
        try: requests.get("http://127.0.0.1:8775/health", timeout=1); break
        except Exception: time.sleep(1)
    en = " ".join("Order %d ships on day %d. Please check item %d." % (100 + i, i, 200 + i) for i in range(1, 13))
    for s, t in pairs:
        if s == "en": text = en
        else:
            text = requests.post("http://127.0.0.1:8775/translate", json={"source": "en", "target": s, "items": [en]}, timeout=100).json()["translations"][0]
        t0 = time.time()
        r = requests.post("http://127.0.0.1:8775/translate", json={"source": s, "target": t, "items": [text]}, timeout=100)
        if r.status_code != 200: print("FAIL", s, t, r.status_code, r.text[:80]); fails += 1; continue
        j = r.json(); out = j["translations"][0]
        want = [str(100 + i) for i in range(1, 13)] + [str(200 + i) for i in range(1, 13)]
        order = [str(x) for i in range(1, 13) for x in (100 + i, 200 + i)]
        got = re.findall(r"\d+", out)
        missing = [w for w in want if w not in got]
        ok = not missing
        ordok = [g for g in got if g in want] == order
        ok = ok and ordok
        fails += (not ok)
        if not ok: print("   OUTPUT:", out[:260].replace("\n"," "))
        print(("PASS" if ok else "FAIL"), s, ">", t, "numbers present %d/24" % (24 - len(missing)), "order_ok=%s" % ([g for g in got if g in want] == order), "seg=%s" % j.get("segmentation"), "%.1fs" % (time.time() - t0))
finally:
    p.kill()
sys.exit(1 if fails else 0)
