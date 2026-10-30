#!/usr/bin/env python3
"""Opt-in glossary checks (sidecar started with PIXOUS_GLOSSARY=tests/glossary.example.json).
Replacement words are NOT native-certified; these verify scoping mechanics and negative controls."""
import os, sys, requests
BASE = os.environ.get("SIDECAR_URL", "http://127.0.0.1:8765")
if hasattr(sys.stdout, "reconfigure"): sys.stdout.reconfigure(encoding="utf-8", errors="replace")
def tr(t, tgt="ta"): return requests.post(BASE+"/translate", json={"source":"en","target":tgt,"items":[t]}, timeout=300).json()["translations"][0]
fails=0; n=0
h=requests.get(BASE+"/health",timeout=10).json()
if h.get("glossary_rules",0)<1: sys.exit("server is NOT running with a glossary (health.glossary_rules=%s): refusing to report"%h.get("glossary_rules"))
def chk(name, ok, d):
    global fails,n; n+=1; fails+= (not ok); print(("PASS  " if ok else "FAIL  ")+name+"  | "+d)
o=tr("Send your resume with the role in the subject line."); chk("role (job cue) -> glossary term, no vessel stem", "பதவி" in o and "பாத்திர" not in o, o)
o=tr("Mention the role you are applying for."); chk("role (applying cue) -> glossary term", "பதவி" in o and "பாத்திர" not in o, o)
o=tr("What is the role of the manager in this project?"); chk("NEG role of manager: glossary NOT applied", "பதவி" not in o, o)
o=tr("The actor played the role of a king."); chk("NEG actor role: glossary NOT applied", "பதவி" not in o, o)
o=tr("The ship's crew boarded the vessel."); chk("NEG vessel text untouched", "பதவி" not in o and "பரவாயில்லை" not in o, o)
o=tr("Thanks for the help. You're welcome!"); chk("welcome after thanks -> thanks-reply term, no greeting stem", "பரவாயில்லை" in o and "வரவேற்" not in o, o)
o=tr("You are most welcome to join us at the office."); chk("NEG welcome-to-join: glossary NOT applied (no thanks cue)", "பரவாயில்லை" not in o, o)
o=tr("Welcome to our website!"); chk("NEG greeting welcome untouched", "பரவாயில்லை" not in o and "வரவேற்" in o, o)
o=tr("Send your resume with the role.", "hi"); chk("NEG other language pair unaffected (hi)", "पदवी" not in o and "பதவி" not in o, o)
print("glossary checks: %d/%d passed"%(n-fails,n)); sys.exit(1 if fails else 0)
