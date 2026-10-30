#!/usr/bin/env python3
"""Lifecycle checks on OWN sidecars (port 8781): client disconnect of a running request aborts the
engine (no 40-60 s wasted run), a waiting client that left frees its queue slot, waiters are served
in arrival order, next request works. Process/time observations on one host, not proofs."""
import os, subprocess, sys, threading, time
import requests
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
fails = 0
def chk(n, ok, d=""):
    global fails; fails += (not ok); print(("PASS  " if ok else "FAIL  ") + n + (("  | " + str(d)) if d else ""), flush=True)
def start(**env):
    e = dict(os.environ, PYTHONPATH=ROOT, **{k: str(v) for k, v in env.items()})
    p = subprocess.Popen([sys.executable, "-m", "uvicorn", "pixous_engine.server:app", "--port", "8781"], cwd=ROOT, env=e, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for _ in range(60):
        try: requests.get("http://127.0.0.1:8781/health", timeout=1); return p
        except Exception: time.sleep(1)
    p.kill(); raise SystemExit("no start")
def tr(text, to=100): return requests.post("http://127.0.0.1:8781/translate", json={"source": "en", "target": "ta", "items": [text]}, timeout=to)
DENSE = ("a b " * 500)[:2000]
p = start(PIXOUS_MAX_QUEUE=1, PIXOUS_HARD_SECONDS=100)
try:
    tr("warm")
    # 1) running request abandoned by its client
    try: tr(DENSE, to=3)
    except requests.exceptions.ReadTimeout: pass
    t = time.time(); r = tr("Good morning"); dt = time.time() - t
    chk("abandoned running request is aborted: next request answers fast (dense run alone takes 40-60 s)", r.status_code == 200 and dt < 20, "%s %.1fs" % (r.status_code, dt))
    w = requests.get("http://127.0.0.1:8781/health").json()["worker"]; chk("worker restarted once by cancel", w["restarts"] >= 1, w)
    # 2) waiter leaves, queue slot freed (MAX_QUEUE=1)
    holder = threading.Thread(target=lambda: tr("x" * 300)); holder.start(); time.sleep(1)
    try: tr("waiter", to=2)
    except requests.exceptions.ReadTimeout: pass
    time.sleep(1.5)
    r = tr("Hello", to=100); chk("after a waiter disconnected, a new request is queued (not 429)", r.status_code == 200, r.status_code)
    holder.join()
finally:
    p.kill(); p.wait()
# 3) FIFO order among waiters (fresh sidecar)
p = start(PIXOUS_MAX_QUEUE=8, PIXOUS_HARD_SECONDS=100)
try:
    tr("warm")
    order = []
    h = threading.Thread(target=lambda: tr("x" * 400)); h.start(); time.sleep(1)
    def w(name, txt): tr(txt); order.append(name)
    ts = []
    for n in ("B", "C", "D"):
        th = threading.Thread(target=w, args=(n, "Good morning %s" % n)); ts.append(th); th.start(); time.sleep(0.7)
    h.join(); [t.join() for t in ts]
    chk("waiters are served in arrival order", order == ["B", "C", "D"], order)
finally:
    p.kill(); p.wait()

print("lifecycle failures:", fails); sys.exit(1 if fails else 0)
