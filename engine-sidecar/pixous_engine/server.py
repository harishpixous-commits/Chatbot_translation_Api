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

from .engine_manager import EngineManager

MAX_ITEMS = int(os.environ.get("PIXOUS_MAX_ITEMS", "256"))
MAX_ITEM_CHARS = int(os.environ.get("PIXOUS_MAX_ITEM_CHARS", "2000"))
MAX_TOTAL_CHARS = int(os.environ.get("PIXOUS_MAX_TOTAL_CHARS", "200000"))
TOKEN = os.environ.get("PIXOUS_ENGINE_TOKEN", "")

app = FastAPI(title="Pixous Translation Engine", version="1.2.0")
manager = EngineManager()
_translate_lock = asyncio.Lock()


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
        "engine": "indictrans2-ct2",
        "device": "cpu",
        "compute_type": "int8",
        "resident_models": manager.resident_models(),
        "max_resident_models": manager.max_resident,
        "auth": "token" if TOKEN else "none (localhost default)",
    }


@app.get("/languages")
async def languages() -> dict:
    return {"languages": manager.supported_languages()}


@app.post("/translate", dependencies=[Depends(require_token)])
async def translate(req: TranslateRequest) -> dict:
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
    async with _translate_lock:
        try:
            result = await asyncio.to_thread(
                manager.translate, req.source, req.target, req.items)
        except ValueError as exc:
            raise HTTPException(status.HTTP_400_BAD_REQUEST, str(exc)) from exc
        except FileNotFoundError as exc:
            raise HTTPException(status.HTTP_503_SERVICE_UNAVAILABLE,
                                str(exc)) from exc
    result["provider"] = "PixousSelfHosted"
    return result
