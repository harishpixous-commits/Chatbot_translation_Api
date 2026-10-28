import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, InjectionToken, Optional, Inject, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { AI_SUITE_CONFIG } from './ai-chat.service';
import { AiSuiteConfig, ApiEnvelope, SttResult } from './models';

/**
 * Runtime voice client — speech-to-text and text-to-speech.
 *
 * Both routes are live only when enabled in ⚙ Settings → Voice
 * (`POST {apiBaseUrl}/api/ai/voice/stt|tts`); otherwise the backend answers
 * with VOICE_UNAVAILABLE. Audio and keys never touch third parties beyond
 * the provider you configured server-side.
 *
 *   stt(blob) → 'hello world'          // multipart upload, envelope { text, provider }
 *   tts('hi') → Blob (audio/wav …)     // raw bytes — feed into <audio> / Audio()
 */
@Injectable({ providedIn: 'root' })
export class AiVoiceService {
  private http = inject(HttpClient);

  config: AiSuiteConfig;

  constructor(
    @Optional() @Inject(AI_SUITE_CONFIG) config: AiSuiteConfig | null,
  ) {
    this.config = config ?? { apiBaseUrl: '' };
  }

  /**
   * Transcribe an audio recording (e.g. a MediaRecorder Blob from the mic).
   * @param language optional BCP-47 hint, e.g. 'en'.
   */
  stt(audio: Blob, fileName = 'recording.webm', language?: string): Observable<SttResult> {
    const form = new FormData();
    form.append('file', audio, fileName);
    if (language) form.append('language', language);
    return this.http
      .post<ApiEnvelope<SttResult>>(`${this.base()}/voice/stt`, form)
      .pipe(map((env) => (env?.data ? env.data : (() => { throw new Error(env?.error?.message ?? 'Speech-to-text failed.'); })())));
  }

  /**
   * Synthesize speech. Returns raw audio bytes (content-type from the provider,
   * e.g. audio/wav) — NOT the standard envelope, so use responseType 'blob'.
   * @param voice optional provider voice name (e.g. 'alloy').
   */
  tts(text: string, voice?: string, language?: string): Observable<Blob> {
    return this.http.post(`${this.base()}/voice/tts`, { text, voice, language }, {
      responseType: 'blob',
      headers: new HttpHeaders({ 'Content-Type': 'application/json' }),
    });
  }

  private base(): string {
    return `${this.config.apiBaseUrl}/api/ai`;
  }
}
