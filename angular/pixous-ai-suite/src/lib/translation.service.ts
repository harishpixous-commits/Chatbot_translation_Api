import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';
import { BehaviorSubject, Observable, firstValueFrom, map } from 'rxjs';
import {
  ApiEnvelope,
  SupportedLanguage,
  TranslateRequest,
  TranslateResponse,
} from './models';
import { AI_SUITE_CONFIG, AiChatService } from './ai-chat.service';

/**
 * Runtime translation service with two layers:
 *  1. UI strings → @ngx-translate (local /assets/i18n/*.json, instant).
 *  2. Dynamic/user content → backend POST {apiBaseUrl}/api/ai/translation/*.
 *
 * Static compile-time i18n ($localize) is intentionally NOT used:
 * it needs a rebuild per locale, which is wrong for a chat widget
 * whose language switches at runtime.
 */
@Injectable({ providedIn: 'root' })
export class AiTranslationService {
  private http = inject(HttpClient);
  private ngx = inject(TranslateService);
  private chat = inject(AiChatService);

  private readonly _lang = new BehaviorSubject<string>('en');
  /** Emits on every language change — subscribe to re-render dynamic content. */
  readonly languageChanged$ = this._lang.asObservable();

  /** Call once at app startup (see integration docs). */
  init(defaultLang = 'en', supported: string[] = ['en']): void {
    this.ngx.addLangs(supported);
    this.ngx.setDefaultLang(defaultLang);
    const browser = this.ngx.getBrowserLang();
    const initial =
      browser && supported.includes(browser) ? browser : defaultLang;
    this.use(initial);
  }

  /** Switch UI language at runtime (ngx-translate + observable). */
  use(lang: string): void {
    this.ngx.use(lang);
    this._lang.next(lang);
  }

  current(): string {
    return this._lang.value;
  }

  /** Instant (synchronous) lookup for UI strings. */
  instant(key: string, params?: Record<string, unknown>): string {
    return this.ngx.instant(key, params);
  }

  /** Translate dynamic content via the backend (keys never leave the server). */
  translate(text: string, targetLanguage: string, sourceLanguage?: string): Observable<TranslateResponse> {
    const req: TranslateRequest = { text, targetLanguage, sourceLanguage };
    return this.http
      .post<ApiEnvelope<TranslateResponse>>(
        `${this.chat.config.apiBaseUrl}/api/ai/translation/translate`,
        req,
      )
      .pipe(map((env) => this.requireData(env)));
  }

  /** Translate 1–100 texts in one call. */
  translateBatch(texts: string[], targetLanguage: string, sourceLanguage?: string): Observable<string[]> {
    return this.http
      .post<ApiEnvelope<{ translatedTexts: string[] }>>(
        `${this.chat.config.apiBaseUrl}/api/ai/translation/batch`,
        { texts, targetLanguage, sourceLanguage },
      )
      .pipe(map((env) => this.requireData(env).translatedTexts));
  }

  /** Detect the language of a text. */
  detect(text: string): Observable<{ language: string; confidence: number }> {
    return this.http
      .post<ApiEnvelope<{ language: string; confidence: number }>>(
        `${this.chat.config.apiBaseUrl}/api/ai/translation/detect-language`,
        { text },
      )
      .pipe(map((env) => this.requireData(env)));
  }

  /** Languages supported by the active backend provider. */
  supportedLanguages(): Observable<SupportedLanguage[]> {
    return this.http
      .get<ApiEnvelope<SupportedLanguage[]>>(`${this.chat.config.apiBaseUrl}/api/ai/translation/languages`)
      .pipe(
        map((env) => {
          const langs = this.requireData(env);
          return Array.isArray(langs) ? langs : [];
        }),
      );
  }

  /** Convenience: translate then switch UI language. */
  async translateAndUse(text: string, target: string): Promise<string> {
    const res = await firstValueFrom(this.translate(text, target));
    return res.translatedText;
  }

  private requireData<T>(env: ApiEnvelope<T>): T {
    if (!env || !env.success || env.data === null || env.data === undefined) {
      throw new Error(env?.error?.message ?? 'Translation request failed.');
    }
    return env.data;
  }
}

// Re-export so consumers import config token from one place.
export { AI_SUITE_CONFIG };
