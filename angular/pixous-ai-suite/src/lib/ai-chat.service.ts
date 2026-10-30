import { HttpClient } from '@angular/common/http';
import { Injectable, InjectionToken, Optional, Inject, inject } from '@angular/core';
import { BehaviorSubject, Observable, catchError, map, throwError } from 'rxjs';
import {
  AiSuiteConfig,
  ApiEnvelope,
  ChatMessage,
  ChatRequest,
  ChatResponse,
  ProviderSelection,
} from './models';

export interface ChatUiMessage extends ChatMessage {
  id: string;
  pending?: boolean;
  /** Reply is shown in the model's original language because its translation was withheld. */
  translationWithheld?: boolean;
}

/** Observed availability of the assistant, proven by real interactions only. */
export type ChatAvailability = 'unknown' | 'online' | 'unavailable';

interface ErrorParts { code?: string; message?: string; status?: number; }

/**
 * Pulls the stable category out of whatever a failure surfaced as: our own
 * Error with .code (SSE frame parsed by stream(), or envelope failure in
 * send()), an HttpErrorResponse whose body is the ApiResponse envelope
 * (error.error.{code,message}) or ASP.NET ProblemDetails (error.code
 * extension + error.title), or a raw network failure (status 0).
 */
function errorParts(err: unknown): ErrorParts {
  const e = err as {
    code?: string; message?: string; status?: number;
    error?: { code?: string; title?: string; error?: { code?: string; message?: string } };
  } | undefined;
  return {
    code: e?.error?.error?.code ?? e?.error?.code ?? e?.code,
    message: e?.error?.error?.message ?? e?.error?.title ?? e?.message,
    status: e?.status,
  };
}

/**
 * User-facing failure text, keyed by the server's stable error code.
 * One taxonomy for both paths - SSE error frames (VALIDATION,
 * UPSTREAM_ABORTED, UPSTREAM_UNREACHABLE, SERVER_ERROR) and non-streaming
 * HTTP failures (VALIDATION_ERROR, PROVIDER_NOT_CONFIGURED,
 * AI_PROVIDER_UNAVAILABLE, INTERNAL_ERROR, network status 0). Distinct,
 * actionable categories: never one collapsed "is it running?" for
 * everything, and never provider-authored text beyond the server's own
 * client-safe message.
 */
export function chatErrorText(err: unknown): string {
  const e = errorParts(err);
  switch (e.code) {
    case 'VALIDATION':
    case 'VALIDATION_ERROR':
      return e.message ?? 'That message was not accepted.';
    case 'UPSTREAM_ABORTED':
      return 'The reply was interrupted - the model server dropped the stream mid-answer. Please try again; if it keeps happening, start a fresh chat.';
    case 'UPSTREAM_UNREACHABLE':
    case 'AI_PROVIDER_UNAVAILABLE':
      return 'The model server is not responding. Check that the local model server is running, then try again.';
    case 'PROVIDER_NOT_CONFIGURED':
      return 'The assistant is not configured on the server yet. Ask the site owner to finish the AI setup.';
    case 'SERVER_ERROR':
    case 'INTERNAL_ERROR':
      return 'Something went wrong on the server. Check the API logs for details.';
    default:
      if (e.status === 0) {
        return 'The assistant cannot be reached. Check your connection and that the API server is running, then try again.';
      }
      return 'Sorry - the assistant is unavailable right now.';
  }
}

/** Availability a failure proves: validation answers mean the API itself responded. */
function availabilityAfterError(err: unknown): ChatAvailability {
  const code = errorParts(err).code;
  return code === 'VALIDATION' || code === 'VALIDATION_ERROR' ? 'online' : 'unavailable';
}

/**
 * Reusable chat client. Talks ONLY to your own backend
 * (`POST {apiBaseUrl}/api/ai/chat`) — API keys never reach the browser.
 *
 * Provide config once in your app:
 *   { provide: AI_SUITE_CONFIG, useValue: { apiBaseUrl: 'https://...' } }
 */
export const AI_SUITE_CONFIG = new InjectionToken<AiSuiteConfig>('AI_SUITE_CONFIG');

@Injectable({ providedIn: 'root' })
export class AiChatService {
  private http = inject(HttpClient);

  /** Override in your app module via AI_SUITE_CONFIG provider. */
  config: AiSuiteConfig;

  constructor(@Optional() @Inject(AI_SUITE_CONFIG) config: AiSuiteConfig | null) {
    this.config = config ?? { apiBaseUrl: '' };
  }

  private readonly _messages = new BehaviorSubject<ChatUiMessage[]>([]);
  readonly messages$ = this._messages.asObservable();

  private readonly _typing = new BehaviorSubject<boolean>(false);
  readonly typing$ = this._typing.asObservable();

  private readonly _availability = new BehaviorSubject<ChatAvailability>('unknown');
  /**
   * Observed availability ONLY: starts 'unknown' (no claim is made), turns
   * 'online' after a completed reply or a validation answer (the API clearly
   * responded), 'unavailable' after a real failure. It reflects what
   * interactions proved - it is not a health probe and never guessed.
   */
  readonly availability$ = this._availability.asObservable();

  /** Non-streaming chat. Appends user + assistant messages to messages$. */
  send(
    message: string,
    opts?: Partial<ChatRequest>,
    behavior?: { echoUser?: boolean },
  ): Observable<ChatResponse> {
    const trimmed = message.trim();
    if (!trimmed) return throwError(() => new Error('Message is empty.'));

    const echoUser = behavior?.echoUser ?? true;
    if (echoUser) this.push({ role: 'user', content: trimmed });
    this._typing.next(true);

    const visible = this._messages.value
      .filter((m) => !m.pending)
      .map((m): ChatMessage => ({ role: m.role, content: m.content }));

    const req: ChatRequest = {
      message: trimmed,
      // Exclude the message just echoed; when echoUser is false the user
      // message is already in the list (streaming path pushed it), so keep all.
      history: echoUser ? visible.slice(0, -1) : visible,
      ...opts,
    };

    return this.http
      .post<ApiEnvelope<ChatResponse>>(`${this.config.apiBaseUrl}/api/ai/chat`, req)
      .pipe(
        map((env) => {
          if (!env.success || !env.data) {
            const e = new Error(env.error?.message ?? 'Chat failed.') as Error & { code?: string };
            if (env.error?.code) e.code = env.error.code;
            throw e;
          }
          const res = env.data;
          this.push({ role: 'assistant', content: res.reply, translationWithheld: res.replyScriptAnomaly === true });
          this._availability.next('online');
          return res;
        }),
        catchError((err) => {
          this._availability.next(availabilityAfterError(err));
          this.push({ role: 'assistant', content: chatErrorText(err) });
          return throwError(() => err);
        }),
      );
  }

  /** Current effective provider selection (admin/diagnostics UI). */
  getSelection(): Observable<ProviderSelection> {
    return this.http
      .get<ApiEnvelope<ProviderSelection>>(`${this.config.apiBaseUrl}/api/ai/selection`)
      .pipe(map((env) => env.data));
  }

  /** Switch providers live. Null = fall back to configured default. */
  setSelection(chatProvider: string | null, translationProvider: string | null): Observable<unknown> {
    return this.http.post(`${this.config.apiBaseUrl}/api/ai/selection`, {
      chatProvider,
      translationProvider,
    });
  }

  /** Clear the local message list (server sessions are separate — see sessionId). */
  clear(): void {
    this._messages.next([]);
  }

  /**
   * Streaming chat over Server-Sent Events (`POST {apiBaseUrl}/api/ai/chat/stream`).
   * Emits each text delta; appends the final assistant message automatically.
   * Uses native fetch (no extra dependency) so it works in any Angular app.
   */
  async *stream(message: string, opts?: Partial<ChatRequest>, signal?: AbortSignal): AsyncGenerator<string> {
    const trimmed = message.trim();
    if (!trimmed) throw new Error('Message is empty.');
    this.push({ role: 'user', content: trimmed });
    this._typing.next(true);

    const pendingId = this.push({ role: 'assistant', content: '', pending: true });
    let full = '';

    try {
      const res = await fetch(`${this.config.apiBaseUrl}/api/ai/chat/stream`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ message: trimmed, ...opts } satisfies ChatRequest),
        signal,
      });
      if (!res.ok || !res.body) {
        const e = new Error(`Stream failed with status ${res.status}.`) as Error & { status?: number; code?: string };
        e.status = res.status;
        // The server writes an explicit {"error",code} SSE frame even on non-2xx -
        // read it so the failure keeps its real category.
        if (res.body) {
          try {
            const text = await res.text();
            const m = text.match(/data:\s*(\{"error".*)/);
            if (m) {
              const p = JSON.parse(m[1]) as { error?: string; code?: string };
              if (p.error) e.message = p.error;
              if (p.code) e.code = p.code;
            }
          } catch { /* best effort - status alone still categorizes */ }
        }
        throw e;
      }

      const reader = res.body.getReader();
      const decoder = new TextDecoder();
      let buf = '';
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        buf += decoder.decode(value, { stream: true });
        const events = buf.split('\n\n');
        buf = events.pop() ?? '';
        for (const ev of events) {
          const line = ev.split('\n').find((l) => l.startsWith('data:'));
          if (!line) continue;
          // SSE payload = everything after "data:" plus exactly one space.
          // Do NOT trim the payload itself — word deltas end with a real space.
          const data = line.slice(5).replace(/^ /, '').replace(/\\n/g, '\n');
          if (data === '[DONE]' || !data) continue;
          if (data.startsWith('{"error"')) {
            const e = new Error('stream error') as Error & { code?: string };
            try {
              const p = JSON.parse(data) as { error?: string; code?: string };
              if (p.error) e.message = p.error;
              if (p.code) e.code = p.code;
            } catch { /* keep raw */ }
            throw e;
          }
          full += data;
          this.patch(pendingId, full);
          yield data;
        }
      }
      this._availability.next('online');
    } catch (err) {
      this._availability.next(availabilityAfterError(err));
      throw err;
    } finally {
      this.patch(pendingId, full, false);
      this._typing.next(false);
    }
  }

  /** Appends a visible assistant error bubble (stream failures that must not silently retry). */
  pushError(content: string): void {
    this.push({ role: 'assistant', content });
  }

  /** Removes any empty assistant bubble (streaming placeholder). The stream's
   *  finally-block clears `pending` before callers retract, so match on empty
   *  content, not on the pending flag. */
  retractPending(): void {
    this._messages.next(this._messages.value.filter((m) => !(m.role === 'assistant' && !m.content)));
  }

  // ── internals ──
  private push(m: ChatMessage & { pending?: boolean; translationWithheld?: boolean }): string {
    const id = Math.random().toString(36).slice(2);
    this._messages.next([...this._messages.value, { ...m, id }]);
    this._typing.next(false);
    return id;
  }

  private patch(id: string, content: string, pending = true): void {
    this._messages.next(
      this._messages.value.map((m) => (m.id === id ? { ...m, content, pending } : m)),
    );
  }
}
