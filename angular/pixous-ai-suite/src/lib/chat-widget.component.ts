import { CommonModule } from '@angular/common';
import { AfterViewChecked, Component, ElementRef, Input, ViewChild, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { merge } from 'rxjs';
import { AiChatService } from './ai-chat.service';

/**
 * Drop-in chat widget (standalone — no NgModule needed).
 *
 * Popup-style assistant branded "Pixous Assistant": gradient header with
 * online status, language badge, quick-reply chips, typing indicator.
 *
 * Usage:
 *   <pix-chat-widget title="Pixous Assistant" responseLanguage="ta" />
 *
 * Inputs are plain data (no secrets): the service reads apiBaseUrl
 * from AI_SUITE_CONFIG. Style via ::ng-deep or the documented CSS vars.
 */
/**
 * User-facing stream failure text, keyed by the server's error code.
 * Distinct, actionable categories - never one collapsed "is it running?" for everything.
 */
function streamErrorText(err: unknown): string {
  const e = err as { code?: string; message?: string } | undefined;
  switch (e?.code) {
    case 'VALIDATION':
      return e.message ?? 'That message was not accepted.';
    case 'UPSTREAM_ABORTED':
      return 'The reply was interrupted - the model server dropped the stream mid-answer. Please try again; if it keeps happening, start a fresh chat.';
    case 'UPSTREAM_UNREACHABLE':
      return 'The model server is not responding. Check that the local model server is running, then try again.';
    case 'SERVER_ERROR':
      return 'Something went wrong on the server. Check the API logs for details.';
    default:
      return 'Sorry - the assistant is unavailable right now.';
  }
}

@Component({
  selector: 'pix-chat-widget',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="pix-chat">
      <header class="pix-chat-head">
        <span class="pix-avatar">P</span>
        <span class="pix-who">
          <b>{{ title }}</b>
          <small><i class="pix-dot"></i>Online · Pixous Assistant</small>
        </span>
        <span class="pix-lang" *ngIf="responseLanguage">{{ responseLanguage | uppercase }}</span>
      </header>

      <div class="pix-chat-body" #body>
        <div
          *ngFor="let m of chat.messages$ | async"
          class="pix-msg"
          [class.pix-user]="m.role === 'user'"
          [class.pix-assistant]="m.role === 'assistant'"
        >
          <span class="pix-md" [innerHTML]="renderRich(m.content)"></span>
        </div>
        <div *ngIf="chat.typing$ | async" class="pix-typing"><span></span><span></span><span></span></div>
        <div class="pix-chips" *ngIf="quickReplies.length">
          <button *ngFor="let q of quickReplies" (click)="onQuick(q)">{{ q }}</button>
        </div>
      </div>

      <form class="pix-chat-input" (ngSubmit)="onSend()">
        <input
          [(ngModel)]="draft"
          name="draft"
          [placeholder]="placeholder"
          autocomplete="off"
        />
        <button type="submit" [disabled]="!draft.trim() || sending" aria-label="Send">
          <svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor" aria-hidden="true"><path d="M3 11l18-8-8 18-2-8-8-2z"/></svg>
        </button>
      </form>
    </div>
  `,
  styles: [
    `
      .pix-chat { display: flex; flex-direction: column; height: 600px; max-width: 384px; border-radius: 22px; overflow: hidden; font-family: inherit; background: #f3f5f9; box-shadow: 0 24px 60px rgba(20,30,60,.28); }
      .pix-chat-head { padding: 14px; background: linear-gradient(135deg, #34d399, #10b981); color: #fff; display: flex; align-items: center; gap: 10px; }
      .pix-avatar { width: 44px; height: 44px; border-radius: 50%; background: rgba(255,255,255,.28); display: flex; align-items: center; justify-content: center; font-size: 22px; font-weight: 700; }
      .pix-who { flex: 1; } .pix-who b { font-size: 16px; } .pix-who small { display: block; font-size: 12px; opacity: .92; }
      .pix-dot { display: inline-block; width: 7px; height: 7px; border-radius: 50%; background: #eafff3; margin-right: 4px; }
      .pix-lang { background: rgba(255,255,255,.22); border-radius: 10px; padding: 4px 10px; font-size: 13px; font-weight: 700; }
      .pix-chat-body { flex: 1; overflow-y: auto; padding: 14px 12px; display: flex; flex-direction: column; gap: 10px; }
      .pix-msg { max-width: 88%; padding: 10px 14px; border-radius: 16px; white-space: pre-wrap; font-size: 14px; line-height: 1.45; }
      .pix-user { align-self: flex-end; background: #1a1a2e; color: #fff; border-top-right-radius: 4px; }
      .pix-assistant { align-self: flex-start; background: #fff; border: 1px solid #e6e8ef; border-top-left-radius: 4px; }
      .pix-md pre.pix-code { background: #1f2430; color: #e8ecf4; padding: 10px 12px; border-radius: 10px; overflow-x: auto; margin: 6px 0; font-size: 12px; }
      .pix-md code.pix-inline { background: rgba(0,0,0,.08); padding: 1px 5px; border-radius: 5px; font-size: 12px; }
      .pix-msg.pix-user .pix-md code.pix-inline { background: rgba(255,255,255,.25); }
      .pix-md a { color: inherit; text-decoration: underline; }
      .pix-typing { display: flex; gap: 5px; padding: 12px 14px; background: #fff; border: 1px solid #e6e8ef; border-radius: 16px; width: fit-content; font-style: normal; }
      .pix-typing span { width: 7px; height: 7px; border-radius: 50%; background: #9aa0ae; animation: pixb 1s infinite; }
      .pix-typing span:nth-child(2) { animation-delay: .15s; } .pix-typing span:nth-child(3) { animation-delay: .3s; }
      @keyframes pixb { 0%,100% { opacity: .3; } 50% { opacity: 1; } }
      .pix-chips { display: flex; flex-wrap: wrap; gap: 8px; }
      .pix-chips button { border: 1px solid #d8dce6; background: #fff; border-radius: 999px; padding: 7px 14px; font-size: 13px; cursor: pointer; }
      .pix-chat-input { display: flex; gap: 8px; padding: 12px; border-top: 1px solid #e6e8ef; background: #fff; align-items: center; }
      .pix-chat-input input { flex: 1; padding: 11px 16px; border: 1.5px solid #10b981; border-radius: 999px; font-size: 14px; outline: none; }
      .pix-chat-input button { width: 44px; height: 44px; border: 0; border-radius: 50%; background: #10b981; color: #fff; cursor: pointer; font-size: 17px; }
      .pix-chat-input button:disabled { opacity: 0.5; cursor: default; }
    `,
  ],
})
export class ChatWidgetComponent implements AfterViewChecked {
  @Input() title = 'Pixous Assistant';
  @Input() placeholder = 'Type your message…';
  /** BCP-47 code the backend should reply in (must be in the backend allow-list). */
  @Input() responseLanguage?: string;
  /** Incremental SSE replies (default true). Set false to force plain POST /chat. */
  @Input() streaming = true;

  /**
   * Opt-in: stream even when `responseLanguage` is set. Streamed replies skip
   * the backend post-translation step (they stay in the model's own language),
   * so by default an explicit `responseLanguage` routes to the POST path,
   * which applies the configured translation provider to the reply.
   */
  @Input() allowUntranslatedStream = false;
  /** Quick-reply chips under the messages. Set to [] to hide. */
  @Input() quickReplies: string[] = ['Our Services', 'Products', 'Contact', 'Careers'];

  @ViewChild('body') private body?: ElementRef<HTMLDivElement>;

  protected chat = inject(AiChatService);
  protected draft = '';
  protected sending = false;
  private pendingScroll = false;

  constructor() {
    // Replies can arrive while the body is scrolled up — snap back to the
    // latest message after the next change-detection pass updates the DOM.
    merge(this.chat.messages$, this.chat.typing$)
      .pipe(takeUntilDestroyed())
      .subscribe(() => (this.pendingScroll = true));
  }

  ngAfterViewChecked(): void {
    if (this.pendingScroll && this.body) {
      this.body.nativeElement.scrollTop = this.body.nativeElement.scrollHeight;
      this.pendingScroll = false;
    }
  }

  onQuick(q: string): void {
    this.draft = q;
    this.onSend();
  }

  onSend(): void {
    const text = this.draft.trim();
    if (!text || this.sending) return;
    this.draft = '';
    this.sending = true;
    // Streaming skips the configured post-translation step. When the host
    // explicitly requests a response language, honor it via the POST path
    // unless the host opted into untranslated (model-language) streaming.
    const useStream = this.streaming && (!this.responseLanguage?.trim() || this.allowUntranslatedStream);
    if (!useStream) {
      this.chat.send(text, { responseLanguage: this.responseLanguage }).subscribe({
        next: () => (this.sending = false),
        error: () => (this.sending = false),
      });
      return;
    }
    // Incremental SSE: the pending bubble fills token by token. If the stream
    // cannot start (e.g. an old backend without /chat/stream), fall back to
    // the non-streaming POST so the reply still arrives.
    (async () => {
      let streamed = false;
      try {
        for await (const delta of this.chat.stream(text, { responseLanguage: this.responseLanguage })) {
          if (delta) streamed = true;
        }
      } catch (err) {
        // Fallback to plain POST ONLY when the server definitively says the
        // stream endpoint does not exist (404/405 = old backend). Any other
        // failure (timeout, network drop, 401/403/429, 5xx, a reply that
        // already started) may mean the request was processed and billed —
        // re-sending it could double-charge, so the error surfaces instead.
        const status = (err as { status?: number })?.status;
        if (!streamed && (status === 404 || status === 405)) {
          this.chat.retractPending();
          await new Promise<void>((resolve) => {
            this.chat.send(text, { responseLanguage: this.responseLanguage }, { echoUser: false }).subscribe({
              next: () => resolve(),
              error: () => resolve(),
            });
          });
        } else if (!streamed) {
          this.chat.retractPending();
          this.chat.pushError(streamErrorText(err));
        } else {
          // The reply started streaming and then broke. Keep the partial text
          // (it may have been billed) and say plainly it was cut off - never
          // silently re-send the request.
          this.chat.pushError('That reply was cut off before it finished - please try again. If it keeps happening, start a fresh chat.');
        }
      } finally {
        this.sending = false;
      }
    })();
  }

  /**
   * Safe rich-text rendering for UNTRUSTED model output. Every character of
   * the source is HTML-escaped FIRST, then a small markdown subset is applied
   * to the escaped text: fenced code blocks, inline code, **bold**, *italic*,
   * and [label](url) links whose href scheme is http(s) or mailto only.
   * No raw HTML from the model ever reaches the DOM — `<script>`, `onerror=`,
   * `javascript:` URLs etc. are inert escaped text. The result additionally
   * passes through Angular's DomSanitizer at the [innerHTML] binding.
   */
  protected renderRich(text: string | null | undefined): string {
    if (!text) return '…';
    const esc = (t: string) =>
      t.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
       .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    let out = esc(text);
    // fenced code blocks ```...``` (language tag ignored)
    out = out.replace(/```[a-z]*\n([\s\S]*?)```/g, (_m, code) =>
      `<pre class="pix-code"><code>${code.replace(/\n$/, '')}</code></pre>`);
    // inline code `...`
    out = out.replace(/`([^`\n]+)`/g, '<code class="pix-inline">$1</code>');
    // bold then italic (escaped text, so no tags can be injected via content)
    out = out.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    out = out.replace(/(^|[^*])\*([^*\n]+)\*/g, '$1<em>$2</em>');
    // links — scheme whitelist; label already escaped
    out = out.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (_m, label, url) => {
      const u = String(url).replace(/&quot;/g, '"');
      return /^(https?:|mailto:)/i.test(u)
        ? `<a href="${url}" target="_blank" rel="noopener noreferrer">${label}</a>`
        : label;
    });
    return out.replace(/\n/g, '<br>');
  }
}
