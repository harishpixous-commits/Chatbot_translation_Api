#!/usr/bin/env python3
"""Negative controls for request-scoped cancel, on OWN sidecars (port 8781): client disconnect of a running request aborts the
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

def main():
    global fails
    # 4) negative controls (request-scoped cancel, repeated disconnects, lock not leaked)
    from pixous_engine.worker import Worker, WorkerFailed
    wk = Worker()
    try:
        t1 = wk.ticket(); r = wk.call("en", "ta", ["Hi"], t1)
        wk.cancel(t1)                                    # late cancel of a FINISHED request
        t2 = wk.ticket(); out = []
        th = threading.Thread(target=lambda: out.append(wk.call("en", "ta", ["Good morning"], t2))); th.start()
        time.sleep(0.2); wk.cancel(t1); th.join(60)      # old ticket cancelled again while t2 runs
        chk("late cancel of an old ticket never kills the worker serving the next request", len(out) == 1 and bool(out[0]["translations"][0]), out and out[0]["translations"])
        t3 = wk.ticket(); wk.cancel(t3)
        try: wk.call("en", "ta", ["x"], t3); ok = False
        except WorkerFailed as e: ok = "cancelled" in str(e)
        chk("cancel before start: call refused, no engine work", ok)
        t4 = wk.ticket(); res = []
        def run4():
            try: wk.call("en", "ta", [DENSE], t4)
            except WorkerFailed as e: res.append(str(e))
        th = threading.Thread(target=run4); th.start(); time.sleep(3); wk.cancel(t4); wk.cancel(t4); th.join(30)
        chk("repeated cancel of a running ticket: ends as cancelled, once", len(res) == 1 and "cancelled" in res[0], res)
        t5 = wk.ticket(); r5 = wk.call("en", "ta", ["Hello"], t5)
        chk("worker serves the next request after a cancel", bool(r5["translations"][0]))
    finally:
        wk.shutdown()
    p = start(PIXOUS_MAX_QUEUE=20, PIXOUS_HARD_SECONDS=100)
    try:
        tr("warm")
        holder = threading.Thread(target=lambda: tr("x" * 300)); holder.start(); time.sleep(1)
        def leave(i):
            try: tr("w%d" % i, to=1 + (i % 3) * 0.4)
            except requests.exceptions.ReadTimeout: pass
        ls = [threading.Thread(target=leave, args=(i,)) for i in range(10)]
        [t.start() for t in ls]; [t.join() for t in ls]; holder.join()
        t = time.time(); r = tr("Good morning", to=60)
        chk("10 waiters leaving at staggered times: lock not leaked, next request answers", r.status_code == 200, "%s %.1fs" % (r.status_code, time.time() - t))
    finally:
        p.kill(); p.wait()
    print("negative-control failures:", fails); sys.exit(1 if fails else 0)


if __name__ == '__main__':
    main()
