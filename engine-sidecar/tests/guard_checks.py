#!/usr/bin/env python3
"""Owned-process guard checks. Starts its OWN sidecars (ports 8770-8772) with
small limits and kills only those PIDs. Verifies: hard time limit kills the
worker and the server recovers; memory guard kills the worker and the server
recovers; queue limit answers 429; a normal request still succeeds afterwards."""
import os, subprocess, sys, threading, time
import requests
HERE = os.path.dirname(os.path.abspath(__file__)); ROOT = os.path.dirname(HERE)
fails = 0
def chk(name, ok, d=""):
    global fails; fails += (not ok); print(("PASS  " if ok else "FAIL  ") + name + (("  | " + str(d)) if d else ""))
def start(port, **env):
    e = dict(os.environ, PYTHONPATH=ROOT, PIXOUS_MODEL_CACHE=os.environ.get("PIXOUS_MODEL_CACHE", "/tmp/hfcache"), **{k: str(v) for k, v in env.items()})
    p = subprocess.Popen([sys.executable, "-m", "uvicorn", "pixous_engine.server:app", "--host", "127.0.0.1", "--port", str(port)],
                         cwd=ROOT, env=e, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for _ in range(60):
        try:
            if requests.get(f"http://127.0.0.1:{port}/health", timeout=1).ok: return p
        except Exception: time.sleep(1)
    p.kill(); raise SystemExit("sidecar did not start")
def tr(port, items, tgt="ta", to=150):
    return requests.post(f"http://127.0.0.1:{port}/translate", json={"source": "en", "target": tgt, "items": items}, timeout=to)
procs = []
try:
    # 1) hard time limit (cooperative budget disabled by huge value so only the hard limit can fire)
    p = start(8770, PIXOUS_HARD_SECONDS=12, PIXOUS_MAX_REQUEST_SECONDS=1000); procs.append(p)
    tr(8770, ["warm"])   # load model
    t = time.time(); r = tr(8770, ["x" * 1999]); dt = time.time() - t
    chk("hard limit: pathological 1999 'x' -> 503 within ~hard limit (not 72-95 s)", r.status_code == 503 and dt < 25, "%s %.1fs %s" % (r.status_code, dt, r.text[:90]))
    h = requests.get("http://127.0.0.1:8770/health").json()
    chk("hard limit: worker restart counted in /health", h["worker"]["restarts"] >= 1, h["worker"])
    t = time.time(); r = tr(8770, ["Good morning"]); 
    chk("hard limit: next normal request succeeds after restart", r.status_code == 200 and any(ord(c) > 127 for c in r.json()["translations"][0]), "%s %.1fs" % (r.status_code, time.time() - t))
    p.kill(); p.wait()
    # 2) memory guard (limit below model residency)
    p = start(8771, PIXOUS_MAX_RSS_MB=400); procs.append(p)
    t = time.time(); r = tr(8771, ["Good morning"])
    chk("memory guard: worker over RSS limit -> 503 'memory guard'", r.status_code == 503 and "memory guard" in r.text, "%s %.1fs %s" % (r.status_code, time.time() - t, r.text[:100]))
    h = requests.get("http://127.0.0.1:8771/health"); chk("memory guard: server still healthy", h.ok)
    p.kill(); p.wait()
    # 3) queue limit
    p = start(8772, PIXOUS_MAX_QUEUE=1, PIXOUS_HARD_SECONDS=40, PIXOUS_MAX_REQUEST_SECONDS=1000); procs.append(p)
    tr(8772, ["warm"])
    codes = []
    def slow(): codes.append(tr(8772, ["x" * 600], to=80).status_code)
    ts = [threading.Thread(target=slow) for _ in range(4)]
    for th in ts: th.start(); time.sleep(0.5)
    for th in ts: th.join()
    chk("queue: with MAX_QUEUE=1, 4 concurrent slow requests include 429", 429 in codes, sorted(codes))
    r = tr(8772, ["Good morning"])
    chk("queue: normal request ok afterwards", r.status_code == 200, r.status_code)
finally:
    for p in procs:
        if p.poll() is None: p.kill()
print("guard checks done, failures:", fails); sys.exit(1 if fails else 0)
