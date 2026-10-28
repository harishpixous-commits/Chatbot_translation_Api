import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, InjectionToken, Optional, Inject, inject } from '@angular/core';
import { Observable, Subject, catchError, map, throwError } from 'rxjs';
import { AI_SUITE_CONFIG } from './ai-chat.service';
import {
  AiSuiteConfig,
  ApiEnvelope,
  ChangePasswordResult,
  LlmKeyInfo,
  LlmKeyInput,
  LlmKeyTestResult,
  LlmKeyUpdate,
  KnowledgeUploadResult,
  SettingsSession,
  SettingsSnapshot,
  VoiceSettingsWrite,
  ServiceKeyCreated,
  ServiceKeyInfo,
} from './models';

/** Session config for the password-gated ⚙ settings API. */
export interface AiSettingsConfig extends AiSuiteConfig {
  /** sessionStorage key for the settings token (default: aiSuiteSettingsToken). */
  tokenStorageKey?: string;
}

export const AI_SETTINGS_CONFIG = new InjectionToken<AiSettingsConfig>('AI_SETTINGS_CONFIG');

const DEFAULT_TOKEN_KEY = 'aiSuiteSettingsToken';

/**
 * Client for the password-gated settings API (`/api/ai/settings`).
 *
 * Secrets contract: raw API keys go IN on write (HTTPS + authenticated) and
 * only ever come back masked (e.g. `sk-a…1234`). The token lives in
 * sessionStorage and is attached as `X-Settings-Token`. On any 401 the token
 * is cleared and `unauthorized$` fires so your UI can re-open the gate.
 *
 *   login(password) → snapshot() → addKey({ vendor: 'OpenAI', apiKey: '…' })
 */
@Injectable({ providedIn: 'root' })
export class AiSettingsService {
  private http = inject(HttpClient);

  /** Override via AI_SETTINGS_CONFIG provider; falls back to AI_SUITE_CONFIG. */
  config: AiSettingsConfig;

  private readonly tokenKey: string;

  /** Emits when the backend rejects the session (expired/revoked) — reopen the gate. */
  readonly unauthorized$ = new Subject<void>();

  constructor(
    @Optional() @Inject(AI_SETTINGS_CONFIG) config: AiSettingsConfig | null,
    @Optional() @Inject(AI_SUITE_CONFIG) fallback: AiSuiteConfig | null,
  ) {
    this.config = config ?? fallback ?? { apiBaseUrl: '' };
    this.tokenKey = this.config.tokenStorageKey ?? DEFAULT_TOKEN_KEY;
  }

  get token(): string {
    return sessionStorage.getItem(this.tokenKey) ?? '';
  }

  get isAuthenticated(): boolean {
    return this.token.length > 0;
  }

  // ── auth ───────────────────────────────────────────────────────────────

  /** Exchange the settings password for a session token (stored in sessionStorage). */
  login(password: string): Observable<SettingsSession> {
    return this.http
      .post<ApiEnvelope<SettingsSession>>(`${this.base()}/settings/login`, { password })
      .pipe(
        map((env) => this.unwrap(env, 'Login failed.')),
        map((session) => {
          if (session?.token) sessionStorage.setItem(this.tokenKey, session.token);
          return session;
        }),
      );
  }

  /** Revoke the session server-side and drop the local token. */
  logout(): Observable<unknown> {
    const req = this.http
      .post<ApiEnvelope<boolean>>(`${this.base()}/settings/logout`, {}, { headers: this.authHeaders() })
      .pipe(map((env) => this.unwrap(env, 'Logout failed.')));
    return new Observable((sub) => {
      req.subscribe({
        next: (v) => { this.clearToken(); sub.next(v); sub.complete(); },
        error: (e) => { this.clearToken(); sub.error(e); },
      });
    });
  }

  // ── snapshot ───────────────────────────────────────────────────────────

  /** Full settings snapshot — keys masked, never raw. */
  snapshot(): Observable<SettingsSnapshot> {
    return this.guard(
      this.http.get<ApiEnvelope<SettingsSnapshot>>(`${this.base()}/settings`, { headers: this.authHeaders() }),
      'Could not load settings.',
    );
  }

  // ── password ───────────────────────────────────────────────────────────

  changePassword(currentPassword: string, newPassword: string): Observable<ChangePasswordResult> {
    return this.guard(
      this.http.put<ApiEnvelope<ChangePasswordResult>>(`${this.base()}/settings/password`,
        { currentPassword, newPassword }, { headers: this.authHeaders() }),
      'Could not change the password.',
    );
  }

  // ── LLM API keys (multi-vendor, auto-fallback) ─────────────────────────

  /** Save a key. Response carries the masked entry only. */
  addKey(input: LlmKeyInput): Observable<LlmKeyInfo> {
    return this.guard(
      this.http.post<ApiEnvelope<LlmKeyInfo>>(`${this.base()}/settings/llm-keys`, input, { headers: this.authHeaders() }),
      'Could not save the key.',
    );
  }

  /** Patch a key; omit apiKey (or send null) to keep the stored one. `move` reorders. */
  updateKey(id: string, patch: LlmKeyUpdate): Observable<LlmKeyInfo> {
    return this.guard(
      this.http.put<ApiEnvelope<LlmKeyInfo>>(`${this.base()}/settings/llm-keys/${encodeURIComponent(id)}`, patch,
        { headers: this.authHeaders() }),
      'Could not update the key.',
    );
  }

  deleteKey(id: string): Observable<{ deleted: boolean }> {
    return this.guard(
      this.http.delete<ApiEnvelope<{ deleted: boolean }>>(`${this.base()}/settings/llm-keys/${encodeURIComponent(id)}`,
        { headers: this.authHeaders() }),
      'Could not delete the key.',
    );
  }

  /** Live round-trip test of one saved key (secret-free result; sets fallback cooldown on failure). */
  testKey(id: string): Observable<LlmKeyTestResult> {
    return this.guard(
      this.http.post<ApiEnvelope<LlmKeyTestResult>>(`${this.base()}/settings/llm-keys/${encodeURIComponent(id)}/test`,
        {}, { headers: this.authHeaders() }),
      'Could not test the key.',
    );
  }

  // ── voice (STT/TTS config) ─────────────────────────────────────────────

  /** Save voice settings. Blank/omitted apiKey fields keep the stored keys. */
  saveVoice(voice: VoiceSettingsWrite): Observable<unknown> {
    return this.guard(
      this.http.put<ApiEnvelope<unknown>>(`${this.base()}/settings/voice`, voice, { headers: this.authHeaders() }),
      'Could not save voice settings.',
    );
  }

  // ── knowledge documents ────────────────────────────────────────────────

  /** Upload a .pdf/.docx/.md/.txt document — parsed and chunked server-side. */
  uploadDocument(file: File): Observable<KnowledgeUploadResult> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.guard(
      this.http.post<ApiEnvelope<KnowledgeUploadResult>>(`${this.base()}/settings/kb`, form,
        { headers: this.authHeaders() }), // Content-Type set by the browser for FormData
      'Could not upload the document.',
    );
  }

  deleteDocument(id: string): Observable<{ deleted: boolean }> {
    return this.guard(
      this.http.delete<ApiEnvelope<{ deleted: boolean }>>(`${this.base()}/settings/kb/${encodeURIComponent(id)}`,
        { headers: this.authHeaders() }),
      'Could not delete the document.',
    );
  }

  // ── internals ──────────────────────────────────────────────────────────

  // ── project service keys (shared /api/ai/v1 surface) ──

  listServiceKeys(): Observable<ServiceKeyInfo[]> {
    return this.guard(
      this.http.get<ApiEnvelope<ServiceKeyInfo[]>>(`${this.base()}/settings/service-keys`, { headers: this.authHeaders() }),
      'Could not load service keys.',
    );
  }

  /** Creates a project key. The raw key is in the response ONCE — show it to the operator immediately. */
  createServiceKey(name: string): Observable<ServiceKeyCreated> {
    return this.guard(
      this.http.post<ApiEnvelope<ServiceKeyCreated>>(`${this.base()}/settings/service-keys`, { name }, { headers: this.authHeaders() }),
      'Could not create the service key.',
    );
  }

  revokeServiceKey(id: string): Observable<{ revoked: boolean }> {
    return this.guard(
      this.http.post<ApiEnvelope<{ revoked: boolean }>>(`${this.base()}/settings/service-keys/${id}/revoke`, {}, { headers: this.authHeaders() }),
      'Could not revoke the service key.',
    );
  }

  /** Rotates: the old key dies instantly; the new raw key is in the response ONCE. */
  rotateServiceKey(id: string): Observable<ServiceKeyCreated> {
    return this.guard(
      this.http.post<ApiEnvelope<ServiceKeyCreated>>(`${this.base()}/settings/service-keys/${id}/rotate`, {}, { headers: this.authHeaders() }),
      'Could not rotate the service key.',
    );
  }

  private base(): string {
    return `${this.config.apiBaseUrl}/api/ai`;
  }

  private authHeaders(): HttpHeaders {
    return new HttpHeaders({ 'X-Settings-Token': this.token });
  }

  private unwrap<T>(env: ApiEnvelope<T>, fallback: string): T {
    if (!env || !env.success || env.data === undefined || env.data === null) {
      throw new Error(env?.error?.message ?? fallback);
    }
    return env.data;
  }

  /** Unwrap + map 401 (envelope UNAUTHORIZED) to unauthorized$ + a clean Error. */
  private guard<T>(req: Observable<ApiEnvelope<T>>, fallback: string): Observable<T> {
    return req.pipe(
      map((env) => this.unwrap(env, fallback)),
      catchError((err) => {
        const body = err?.error as ApiEnvelope<unknown> | undefined;
        const code = body?.error?.code;
        const status: number = err?.status ?? 0;
        if (status === 401 || code === 'UNAUTHORIZED') {
          this.clearToken();
          this.unauthorized$.next();
          return throwError(() => new Error(body?.error?.message ?? 'Settings session expired.'));
        }
        return throwError(() => new Error(body?.error?.message ?? err?.message ?? fallback));
      }),
    );
  }

  private clearToken(): void {
    sessionStorage.removeItem(this.tokenKey);
  }
}
