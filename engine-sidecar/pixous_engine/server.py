"""FastAPI sidecar exposing the self-hosted IndicTrans2 engine.

Security defaults (read before deploying):
- Binds 127.0.0.1 only. Localhost binding is NOT authentication: if you
  expose this service on a network, set PIXOUS_ENGINE_TOKEN and require TLS
  at your reverse proxy. Treat it as unauthenticated otherwise.
- Request bounds are hard limits (items, per-item chars, total chars) so one
  caller cannot exhaust host memory; all are env-tunable downward.
- Translation calls are serialized through one lock: this host profile
  cannot load two models or run two batches concurrently.
"""
from __future__ import annotations

import asyncio
import hmac
import os

from fastapi import Depends, FastAPI, HTTPException, Request, status
from pydantic import BaseModel, Field

from . import script_check
from .engine_manager import EngineManager
from .worker import Worker, WorkerFailed

MAX_ITEMS = int(os.environ.get("PIXOUS_MAX_ITEMS", "256"))
MAX_ITEM_CHARS = int(os.environ.get("PIXOUS_MAX_ITEM_CHARS", "2000"))
MAX_TOTAL_CHARS = int(os.environ.get("PIXOUS_MAX_TOTAL_CHARS", "200000"))
TOKEN = os.environ.get("PIXOUS_ENGINE_TOKEN", "")

app = FastAPI(title="Pixous Translation Engine", version="1.2.0")
manager = EngineManager()
_translate_lock = asyncio.Lock()
USE_WORKER = os.environ.get("PIXOUS_WORKER", "1") != "0"
worker = Worker() if USE_WORKER else None
MAX_QUEUE = int(os.environ.get("PIXOUS_MAX_QUEUE", "8"))   # requests waiting for the engine
_waiting = 0


async def require_token(request: Request) -> None:
    if not TOKEN:
        return  # localhost-only default; see module docstring
    auth = request.headers.get("authorization", "")
    # Constant-time compare; the 401 body is fixed text and never echoes the
    # presented header or hints at length/prefix mismatches.
    if not hmac.compare_digest(auth.encode(), f"Bearer {TOKEN}".encode()):
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "invalid token")


class TranslateRequest(BaseModel):
    source: str = Field(min_length=2, max_length=12)
    target: str = Field(min_length=2, max_length=12)
    items: list[str] = Field(min_length=1)


@app.get("/health")
async def health() -> dict:
    return {
        "status": "ok",
        "glossary_rules": manager.glossary_rule_count(),
        "engine": "indictrans2-ct2",
        "device": "cpu",
        "compute_type": "int8",
        "resident_models": worker.resident if worker else manager.resident_models(),
        "worker": ({"enabled": True, "alive": worker.alive(), "restarts": worker.restarts} if worker else {"enabled": False}),
        "max_resident_models": manager.max_resident,
        "limits": {"max_items": MAX_ITEMS, "max_item_chars": MAX_ITEM_CHARS, "max_total_chars": MAX_TOTAL_CHARS},
        "auth": "token" if TOKEN else "none (localhost default)",
    }


@app.on_event("shutdown")
def _shutdown() -> None:
    if worker is not None:
        worker.shutdown()


@app.get("/languages")
async def languages() -> dict:
    return {"languages": manager.supported_languages()}


async def _abandon(acq: "asyncio.Future") -> None:
    """Give up a lock acquisition. If it completed in the race, release it (we own it)."""
    if not acq.done():
        acq.cancel()
    await asyncio.sleep(0)
    if acq.done() and not acq.cancelled() and acq.exception() is None:
        _translate_lock.release()


def ticket_cancelled(loc: dict) -> bool:
    t = loc.get("ticket")
    return bool(t is not None and t.cancelled)


@app.post("/translate", dependencies=[Depends(require_token)])
async def translate(req: TranslateRequest, request: Request) -> dict:
    if len(req.items) > MAX_ITEMS:
        raise HTTPException(status.HTTP_413_REQUEST_ENTITY_TOO_LARGE,
                            f"items > {MAX_ITEMS}")
    total = 0
    for item in req.items:
        if len(item) > MAX_ITEM_CHARS:
            raise HTTPException(status.HTTP_413_REQUEST_ENTITY_TOO_LARGE,
                                f"item exceeds {MAX_ITEM_CHARS} chars")
        total += len(item)
    if total > MAX_TOTAL_CHARS:
        raise HTTPException(status.HTTP_413_REQUEST_ENTITY_TOO_LARGE,
                            f"total chars > {MAX_TOTAL_CHARS}")
    global _waiting
    if _translate_lock.locked() and _waiting >= MAX_QUEUE:
        raise HTTPException(status.HTTP_429_TOO_MANY_REQUESTS,
                            "translation engine is busy, retry shortly",
                            headers={"Retry-After": "5"})
    _waiting += 1
    acq = asyncio.ensure_future(_translate_lock.acquire())
    try:
        while not acq.done():                     # a waiting client that left gives up its place
            await asyncio.wait({acq}, timeout=0.5)
            if not acq.done() and await request.is_disconnected():
                break
    except BaseException:                         # handler itself cancelled (shutdown etc.)
        await _abandon(acq)
        raise
    finally:
        _waiting -= 1
    if not acq.done() or acq.cancelled():
        await _abandon(acq)
        raise HTTPException(499, "client closed request")
    try:
        try:
            if worker is not None:
                ticket = worker.ticket()
                job = asyncio.ensure_future(asyncio.to_thread(
                    worker.call, req.source, req.target, req.items, ticket))
                try:
                    while not job.done():         # a running job whose client left is aborted
                        await asyncio.wait({job}, timeout=0.5)
                        if not job.done() and await request.is_disconnected():
                            worker.cancel(ticket)
                            break
                except BaseException:
                    worker.cancel(ticket)         # handler cancelled: still abort OUR job only
                    raise
                result = await job
            else:
                result = await asyncio.to_thread(
                    manager.translate, req.source, req.target, req.items)
        except WorkerFailed as exc:
            if ticket_cancelled(locals()):
                raise HTTPException(499, "client closed request") from exc
            raise HTTPException(status.HTTP_503_SERVICE_UNAVAILABLE,
                                {"code": "resource_limit", "message": str(exc)}) from exc
        except ValueError as exc:
            raise HTTPException(status.HTTP_400_BAD_REQUEST, str(exc)) from exc
        except TimeoutError as exc:
            raise HTTPException(status.HTTP_503_SERVICE_UNAVAILABLE,
                                {"code": "resource_limit", "message": str(exc)}) from exc
        except FileNotFoundError as exc:
            raise HTTPException(status.HTTP_503_SERVICE_UNAVAILABLE,
                                str(exc)) from exc
        except RuntimeError as exc:   # message is type-only, never input text
            raise HTTPException(status.HTTP_500_INTERNAL_SERVER_ERROR, str(exc)) from exc
    finally:
        _translate_lock.release()
    result["provider"] = "PixousSelfHosted"
    result["quality"] = script_check.check_items(req.target, req.items, result["translations"])
    return result
