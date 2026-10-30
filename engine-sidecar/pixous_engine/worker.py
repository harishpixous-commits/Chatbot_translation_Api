"""Owned engine worker process: hard time limit and memory guard.

The translation model runs in a child process the server OWNS. If a request
exceeds PIXOUS_HARD_SECONDS the child is killed (a real deadline, unlike the
cooperative between-groups check); if the child's RSS passes
PIXOUS_MAX_RSS_MB a watchdog inside it exits the process. Either way the
server answers 503 and the next request starts a fresh child (model reload).
This is process-level containment, NOT sandboxing: same user, no cgroup.
Set PIXOUS_WORKER=0 to run in-process (no deadline/guard) for debugging.
"""
from __future__ import annotations

import multiprocessing as mp
import os
import threading
import time

HARD_SECONDS = float(os.environ.get("PIXOUS_HARD_SECONDS", "120"))
MAX_RSS_MB = int(os.environ.get("PIXOUS_MAX_RSS_MB", "1400"))


class WorkerFailed(RuntimeError):
    """Child was killed (deadline) or died (memory guard/crash)."""


def _rss_mb() -> int:
    with open("/proc/self/statm") as f:
        return int(f.read().split()[1]) * 4096 // 2**20


def _child(conn, max_rss_mb: int) -> None:
    def guard():
        while True:
            try:
                if _rss_mb() > max_rss_mb:
                    os._exit(75)   # parent reports "memory guard"
            except OSError:        # no /proc (non-Linux): guard unavailable
                return
            time.sleep(0.05)
    threading.Thread(target=guard, daemon=True).start()
    from .engine_manager import EngineManager
    manager = EngineManager()
    while True:
        try:
            msg = conn.recv()
        except EOFError:
            return
        try:
            conn.send(("ok", manager.translate(*msg), manager.resident_models()))
        except (ValueError, FileNotFoundError, TimeoutError) as exc:
            conn.send(("err", type(exc).__name__, str(exc)))
        except Exception as exc:    # unexpected: do not leak input text
            conn.send(("err", "RuntimeError", f"engine error ({type(exc).__name__})"))


class Ticket:
    """Per-request cancellation handle."""
    def __init__(self) -> None:
        self.cancelled = False


class Worker:
    def __init__(self) -> None:
        self._ctx = mp.get_context("spawn")
        self._proc = None
        self._conn = None
        self.restarts = 0
        self.resident: list[str] = []
        self._call_lock = threading.Lock()   # the pipe is single-user: never two calls at once
        self._state_lock = threading.Lock()
        self._active = None

    def _ensure(self) -> None:
        if self._proc is not None and self._proc.is_alive():
            return
        self._kill()
        parent, child = self._ctx.Pipe()
        self._proc = self._ctx.Process(target=_child, args=(child, MAX_RSS_MB), daemon=True)
        self._proc.start()
        child.close()
        self._conn = parent

    def _kill(self) -> None:
        if self._proc is not None:
            if self._proc.is_alive():
                self._proc.kill()
            self._proc.join(5)
            self.restarts += 1
        self._proc = self._conn = None
        self.resident = []

    def ticket(self) -> "Ticket":
        return Ticket()

    def cancel(self, ticket: "Ticket") -> None:
        """Abort ONE request. It only kills the child if this ticket is the call running right
        now; a late cancel of a finished/other request is a no-op and can never kill the worker
        that is serving the next request."""
        with self._state_lock:
            ticket.cancelled = True
            if self._active is ticket:
                proc = self._proc
                if proc is not None and proc.is_alive():
                    proc.kill()

    def call(self, source: str, target: str, items: list[str], ticket: "Ticket | None" = None) -> dict:
        ticket = ticket or Ticket()
        with self._call_lock:
            with self._state_lock:
                if ticket.cancelled:
                    raise WorkerFailed("engine call cancelled before start")
                self._active = ticket
            try:
                return self._call(source, target, items, ticket)
            finally:
                with self._state_lock:
                    self._active = None

    def _call(self, source: str, target: str, items: list[str], ticket: "Ticket") -> dict:
        self._ensure()
        self._conn.send((source, target, items))
        # first call also pays model load: allow it inside the same hard limit
        if not self._conn.poll(HARD_SECONDS):
            self._kill()
            raise WorkerFailed(
                f"translation exceeded the {HARD_SECONDS:g}s hard limit; engine restarted")
        try:
            msg = self._conn.recv()
        except (EOFError, OSError):
            code = self._proc.exitcode if self._proc else None
            self._kill()
            why = "cancelled" if ticket.cancelled else "memory guard" if code == 75 else "crash"
            raise WorkerFailed(f"engine worker stopped ({why}); restarted on next request") from None
        if msg[0] == "ok":
            self.resident = msg[2]
            return msg[1]
        exc = {"ValueError": ValueError, "FileNotFoundError": FileNotFoundError,
               "TimeoutError": TimeoutError}.get(msg[1], RuntimeError)
        raise exc(msg[2])

    def alive(self) -> bool:
        return self._proc is not None and self._proc.is_alive()

    def shutdown(self) -> None:
        self._kill()
