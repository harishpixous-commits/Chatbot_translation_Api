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
            throw new Error(env.error?.message ?? 'Chat failed.');
          }
          const res = env.data;
          this.push({ role: 'assistant', content: res.reply });
          return res;
        }),
        catchError((err) => {
          this.push({ role: 'assistant', content: 'Sorry - the assistant is unavailable right now.' });
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
  private push(m: ChatMessage & { pending?: boolean }): string {
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
