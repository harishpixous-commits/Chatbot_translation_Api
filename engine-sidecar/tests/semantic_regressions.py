#!/usr/bin/env python3
"""Semantic regressions against a RUNNING sidecar. Expected outputs come from
a non-native reviewer (ta only; labelled), so these are regression assertions
for KNOWN WRONG senses, not native certification. A failing assertion stays a
FAIL and the script exits non-zero; nothing here is weakened or xfail-ed."""
import os, sys, requests
BASE = os.environ.get("SIDECAR_URL", "http://127.0.0.1:8765")
if hasattr(sys.stdout, "reconfigure"): sys.stdout.reconfigure(encoding="utf-8", errors="replace")
def tr(items, tgt="ta"):
    r = requests.post(BASE + "/translate", json={"source": "en", "target": tgt, "items": items}, timeout=300)
    return r.json()["translations"]
h = requests.get(BASE + '/health', timeout=10).json()
if h.get('glossary_rules', 0) != 0: sys.exit('server has a glossary on; this script measures default mode (health.glossary_rules=%s)' % h.get('glossary_rules'))
fails, total = [], 0
GLOSS = os.environ.get('GLOSSARY_ON') == '1'  # sidecar started with PIXOUS_GLOSSARY=tests/glossary.example.json
def check(name, ok, detail):
    global total; total += 1
    print(("PASS  " if ok else "FAIL  ") + name + "  | " + detail)
    if not ok: fails.append(name)
# (name, input, forbidden wrong-sense substrings, required substrings)
CASES = [
 ("ta role->vessel/character (job posting)", "Send your resume with the role in the subject line.", ["பாத்திர", "பாட வரிசை"], []),
 ("ta role->vessel/character (applying)", "Mention the role you are applying for in the subject line.", ["பாத்திர", "பாட வரிசை"], []),
 ("ta role (job, positive control)", "What is the role of the manager in this project?", ["பாத்திர"], ["பங்கு"]),
 ("ta 'most welcome' as greeting (thanks reply)", "You are most welcome!", ["வரவேற்"], []),
 ("ta 'welcome' after thanks", "Thanks for the help. You're welcome!", ["வரவேற்"], []),
 ("ta 'Press' as newspaper in a UI step line", "Steps:\n- Open Settings\n- Press Test", ["பத்திரிகை"], []),
 ("ta literal [1] not corrupted", "Use [1] carefully `x` now.", ["பாபே"], ["[1]", "`x`"]),
 ("ta literal [1] + code + bold fallback path", "Use [1] carefully `x` now **bold**", ["பாபே", "[1]பா"], ["[1]", "`x`", "**"]),
 ("ta bold inner phrase translated (not left English)", "Please read the **terms of service** first.", [], []),
 ("ta 'subject line' as lesson order", "Write the topic in the subject line.", ["பாட வரிசை"], []),
]
for name, src, bad, need in CASES:
    out = tr([src])[0]
    if "inner phrase" in name: bad = ["terms of service"]
    hit = [b for b in bad if b in out]; miss = [n for n in need if n not in out]
    check(name, not hit and not miss, "%r -> %r bad=%s missing=%s" % (src, out, hit, miss))
print("semantic regressions: %d/%d passed" % (total - len(fails), total))
sys.exit(1 if fails else 0)
