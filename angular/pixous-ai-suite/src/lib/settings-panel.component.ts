import { CommonModule } from '@angular/common';
import { Component, OnInit, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AiSettingsService } from './ai-settings.service';
import {
  KnowledgeDocumentInfo,
  LlmKeyInfo,
  LlmKeyInput,
  LlmVendorPreset,
  ServiceKeyInfo,
  SettingsSnapshot,
  VoiceSettingsWrite,
} from './models';

/**
 * Drop-in admin panel for the ⚙ Settings surface: password login gate,
 * LLM API keys (add / edit / enable / disable / delete / test, with honest
 * states: ok / invalid / denied / cooling / locked), project service keys
 * for the shared /api/ai/v1 surface (create-once-shown / revoke / rotate),
 * voice (STT/TTS), knowledge documents, and the settings password.
 *
 *   <pixous-settings-panel></pixous-settings-panel>
 *
 * Talks only to your own backend through AiSettingsService — raw provider
 * keys go IN (authenticated) and are only ever shown masked.
 */
@Component({
  selector: 'pixous-settings-panel',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
  <div class="pix-admin">
    <!-- ── login gate ─────────────────────────────────────────────── -->
    <section *ngIf="!settings.isAuthenticated" class="pix-card">
      <h2>Settings</h2>
      <p class="pix-muted">Enter the settings password to manage providers, keys and voice.</p>
      <form (ngSubmit)="doLogin()">
        <input type="password" [(ngModel)]="password" name="password" placeholder="Settings password"
               autocomplete="current-password" [disabled]="busy" />
        <button type="submit" [disabled]="busy || !password">Log in</button>
      </form>
      <p class="pix-error" *ngIf="error">{{ error }}</p>
    </section>

    <ng-container *ngIf="settings.isAuthenticated">
      <nav class="pix-tabs">
        <button *ngFor="let t of tabs" [class.active]="tab === t.id" (click)="switchTab(t.id)">{{ t.label }}</button>
        <button class="pix-logout" (click)="doLogout()">Log out</button>
      </nav>
      <p class="pix-error" *ngIf="error">{{ error }}</p>
      <p class="pix-ok" *ngIf="notice">{{ notice }}</p>

      <!-- ── LLM keys ─────────────────────────────────────────────── -->
      <section *ngIf="tab === 'llm'" class="pix-card">
        <h3>LLM API keys</h3>
        <p class="pix-muted">Priority order = fallback order. A 401 marks a key
          <em>invalid</em>, a 403 <em>denied</em> (not the same as a wrong key), a 429
          <em>cooling</em> until its Retry-After passes — quota answers are honored, not evaded.
          A new key is <em>untested</em> until the vendor accepts it (via Test or real traffic) —
          <em>ok</em> means verified, nothing less.
          A <em>locked</em> key's stored secret could not be decrypted (different data-protection
          key ring): its ciphertext is preserved — re-enter the key to unlock it.</p>
        <table *ngIf="snapshot?.llmKeys?.length" class="pix-table">
          <tr><th>Key</th><th>Vendor / model</th><th>State</th><th></th></tr>
          <tr *ngFor="let k of snapshot!.llmKeys">
            <td><strong>{{ k.name }}</strong><br><small class="pix-muted">{{ k.apiKeyMasked || '—' }}</small></td>
            <td>{{ k.vendor }}<br><small class="pix-muted">{{ k.model || k.endpoint || '' }}</small></td>
            <td><span class="pix-state" [attr.data-state]="k.status?.state || (k.enabled ? 'untested' : 'disabled')">
                  {{ k.status?.state || (k.enabled ? 'untested' : 'disabled') }}</span>
                <small class="pix-muted" *ngIf="k.status?.lastError"><br>{{ k.status!.lastError }}</small></td>
            <td class="pix-actions">
              <button (click)="testKey(k)" [disabled]="busy">Test</button>
              <button (click)="toggleKey(k)" [disabled]="busy">{{ k.enabled ? 'Disable' : 'Enable' }}</button>
              <button class="pix-danger" (click)="deleteKey(k)" [disabled]="busy">Delete</button>
            </td>
          </tr>
        </table>
        <p *ngIf="!snapshot?.llmKeys?.length" class="pix-muted">No keys yet — the KnowledgeBase / Echo
          engines work without one.</p>

        <h4>Add a key</h4>
        <form (ngSubmit)="addKey()" class="pix-form">
          <label>Vendor
            <select [(ngModel)]="newKey.vendor" name="vendor" (ngModelChange)="applyPreset()">
              <option *ngFor="let p of snapshot?.vendorPresets || []" [ngValue]="p.vendor">{{ p.label }}</option>
            </select></label>
          <label>Name<input [(ngModel)]="newKey.name" name="name" placeholder="e.g. primary" /></label>
          <label>Endpoint<input [(ngModel)]="newKey.endpoint" name="endpoint" placeholder="Blank = vendor default" /></label>
          <label>Model<input [(ngModel)]="newKey.model" name="model" placeholder="e.g. gpt-4o-mini" /></label>
          <label>API key (stored encrypted, never shown again)
            <input type="password" [(ngModel)]="newKey.apiKey" name="apiKey" autocomplete="off" /></label>
          <button type="submit" [disabled]="busy || !newKey.vendor">Add key</button>
        </form>
      </section>

      <!-- ── service keys ─────────────────────────────────────────── -->
      <section *ngIf="tab === 'service'" class="pix-card">
        <h3>Project service keys</h3>
        <p class="pix-muted">Keys for the shared <code>/api/ai/v1</code> surface — one per consumer
          project (any language). Each key has its own rate limit and session namespace.
          Server-side use only: never ship a key to a browser or mobile app.</p>
        <div *ngIf="createdKey" class="pix-once">
          <strong>Store this key now — it is never shown again:</strong>
          <code>{{ createdKey }}</code>
          <button (click)="createdKey = null">I stored it</button>
        </div>
        <table *ngIf="serviceKeys.length" class="pix-table">
          <tr><th>Project</th><th>Prefix</th><th>Requests</th><th>State</th><th></th></tr>
          <tr *ngFor="let k of serviceKeys">
            <td>{{ k.name }}</td>
            <td><code>{{ k.prefix }}…</code></td>
            <td>{{ k.totalRequests }}</td>
            <td><span class="pix-state" [attr.data-state]="k.revokedUtc ? 'revoked' : 'ok'">
              {{ k.revokedUtc ? 'revoked' : 'active' }}</span></td>
            <td class="pix-actions">
              <button (click)="rotateServiceKey(k)" [disabled]="busy || !!k.revokedUtc">Rotate</button>
              <button class="pix-danger" (click)="revokeServiceKey(k)" [disabled]="busy || !!k.revokedUtc">Revoke</button>
            </td>
          </tr>
        </table>
        <form (ngSubmit)="createServiceKey()" class="pix-form">
          <label>Consumer project name
            <input [(ngModel)]="newServiceKeyName" name="skname" placeholder="e.g. billing-api" /></label>
          <button type="submit" [disabled]="busy || !newServiceKeyName.trim()">Issue key</button>
        </form>
      </section>

      <!-- ── voice ────────────────────────────────────────────────── -->
      <section *ngIf="tab === 'voice'" class="pix-card">
        <h3>Voice</h3>
        <form *ngIf="voice" (ngSubmit)="saveVoice()" class="pix-form">
          <label><input type="checkbox" [(ngModel)]="voice.sttEnabled" name="sttEnabled" /> Speech-to-text (mic button)</label>
          <label>STT model<input [(ngModel)]="voice.sttModel" name="sttModel" placeholder="e.g. whisper-1" /></label>
          <label>STT API key (blank = keep stored)
            <input type="password" [(ngModel)]="voice.sttApiKey" name="sttApiKey" autocomplete="off" /></label>
          <label><input type="checkbox" [(ngModel)]="voice.ttsEnabled" name="ttsEnabled" /> Text-to-speech (speak replies)</label>
          <label>TTS model<input [(ngModel)]="voice.ttsModel" name="ttsModel" placeholder="e.g. tts-1" /></label>
          <label>TTS voice<input [(ngModel)]="voice.ttsVoice" name="ttsVoice" placeholder="e.g. alloy" /></label>
          <label>TTS API key (blank = keep stored)
            <input type="password" [(ngModel)]="voice.ttsApiKey" name="ttsApiKey" autocomplete="off" /></label>
          <button type="submit" [disabled]="busy">Save voice</button>
        </form>
      </section>

      <!-- ── knowledge ────────────────────────────────────────────── -->
      <section *ngIf="tab === 'knowledge'" class="pix-card">
        <h3>Knowledge base</h3>
        <p class="pix-muted">Uploaded documents ground the KnowledgeBase engine's answers.</p>
        <table *ngIf="snapshot?.documents?.length" class="pix-table">
          <tr><th>Document</th><th>Chunks</th><th></th></tr>
          <tr *ngFor="let d of snapshot!.documents">
            <td>{{ d.fileName }} <small class="pix-muted">({{ d.sizeBytes }} B)</small></td>
            <td>{{ d.chunkCount }}</td>
            <td class="pix-actions"><button class="pix-danger" (click)="deleteDocument(d)" [disabled]="busy">Delete</button></td>
          </tr>
        </table>
        <input type="file" (change)="uploadDocument($event)" [disabled]="busy" />
      </section>

      <!-- ── password ─────────────────────────────────────────────── -->
      <section *ngIf="tab === 'password'" class="pix-card">
        <h3>Settings password</h3>
        <form (ngSubmit)="changePassword()" class="pix-form">
          <label>Current password
            <input type="password" [(ngModel)]="pwCurrent" name="pwCurrent" autocomplete="current-password" /></label>
          <label>New password (min 8 chars)
            <input type="password" [(ngModel)]="pwNew" name="pwNew" autocomplete="new-password" /></label>
          <button type="submit" [disabled]="busy || !pwCurrent || pwNew.length < 8">Change password</button>
        </form>
      </section>
    </ng-container>
  </div>
  `,
  styles: [`
    .pix-admin { font: 14px/1.45 system-ui, sans-serif; color: #1f2937; max-width: 760px; }
    .pix-card { border: 1px solid #e5e7eb; border-radius: 10px; padding: 16px; margin: 12px 0; background: #fff; }
    .pix-tabs { display: flex; gap: 6px; margin: 12px 0; flex-wrap: wrap; }
    .pix-tabs button { border: 1px solid #d1d5db; background: #f9fafb; border-radius: 8px; padding: 6px 12px; cursor: pointer; }
    .pix-tabs button.active { background: #2563eb; border-color: #2563eb; color: #fff; }
    .pix-tabs .pix-logout { margin-left: auto; }
    .pix-form { display: flex; flex-direction: column; gap: 8px; margin-top: 8px; }
    .pix-form input, .pix-form select { padding: 8px; border: 1px solid #d1d5db; border-radius: 8px; }
    .pix-form label { display: flex; flex-direction: column; gap: 4px; font-weight: 500; color: #374151; }
    .pix-form label:has(input[type='checkbox']) { flex-direction: row; align-items: center; font-weight: 400; }
    .pix-form button, .pix-actions button { padding: 6px 12px; border: 1px solid #2563eb; background: #2563eb;
      color: #fff; border-radius: 8px; cursor: pointer; }
    .pix-actions button { background: #fff; color: #2563eb; margin-right: 4px; }
    .pix-actions .pix-danger { border-color: #dc2626; color: #dc2626; background: #fff; }
    .pix-table { width: 100%; border-collapse: collapse; margin: 8px 0; }
    .pix-table th, .pix-table td { text-align: left; padding: 6px 8px; border-bottom: 1px solid #f3f4f6; vertical-align: top; }
    .pix-muted { color: #6b7280; }
    .pix-error { color: #b91c1c; }
    .pix-ok { color: #15803d; }
    .pix-state { padding: 2px 8px; border-radius: 999px; font-size: 12px; background: #e5e7eb; }
    .pix-state[data-state='ok'], .pix-state[data-state='active'] { background: #dcfce7; color: #166534; }
    .pix-state[data-state='invalid'], .pix-state[data-state='revoked'] { background: #fee2e2; color: #991b1b; }
    .pix-state[data-state='denied'] { background: #ffedd5; color: #9a3412; }
    .pix-state[data-state='cooling'], .pix-state[data-state='locked'] { background: #fef9c3; color: #854d0e; }
    .pix-state[data-state='untested'] { background: #e5e7eb; color: #4b5563; }
    .pix-once { border: 1px solid #f59e0b; background: #fffbeb; border-radius: 8px; padding: 10px;
      display: flex; gap: 10px; align-items: center; flex-wrap: wrap; margin: 8px 0; }
    .pix-once code { user-select: all; word-break: break-all; }
  `],
})
export class AiSettingsPanelComponent implements OnInit {
  protected settings = inject(AiSettingsService);

  protected readonly tabs = [
    { id: 'llm', label: 'LLM keys' },
    { id: 'service', label: 'Service keys' },
    { id: 'voice', label: 'Voice' },
    { id: 'knowledge', label: 'Knowledge' },
    { id: 'password', label: 'Password' },
  ] as const;
  protected tab: string = 'llm';

  protected snapshot: SettingsSnapshot | null = null;
  protected serviceKeys: ServiceKeyInfo[] = [];
  protected voice: (VoiceSettingsWrite & { sttApiKey?: string | null; ttsApiKey?: string | null }) | null = null;

  protected password = '';
  protected busy = false;
  protected error = '';
  protected notice = '';

  protected newKey: LlmKeyInput = { vendor: 'OpenAiCompatible' };
  protected newServiceKeyName = '';
  protected createdKey: string | null = null;
  protected pwCurrent = '';
  protected pwNew = '';

  ngOnInit(): void {
    if (this.settings.isAuthenticated) this.reload();
  }

  protected switchTab(id: string): void {
    this.tab = id;
    this.notice = ''; // success/error messages belong to the tab that produced them
    this.error = '';
  }

  protected doLogin(): void {
    this.run('Logged in.', this.settings.login(this.password), () => {
      this.password = '';
      this.reload();
    });
  }

  protected doLogout(): void {
    this.settings.logout().subscribe({ next: () => undefined, error: () => undefined });
  }

  protected reload(): void {
    this.settings.snapshot().subscribe({
      next: (snap) => {
        this.snapshot = snap;
        this.voice = {
          sttEnabled: snap.voice.sttEnabled,
          sttProvider: snap.voice.sttProvider,
          sttEndpoint: snap.voice.sttEndpoint ?? undefined,
          sttModel: snap.voice.sttModel ?? undefined,
          ttsEnabled: snap.voice.ttsEnabled,
          ttsProvider: snap.voice.ttsProvider,
          ttsEndpoint: snap.voice.ttsEndpoint ?? undefined,
          ttsModel: snap.voice.ttsModel ?? undefined,
          ttsVoice: snap.voice.ttsVoice ?? undefined,
        };
      },
      error: (e) => (this.error = e?.message ?? 'Could not load settings.'),
    });
    this.settings.listServiceKeys().subscribe({
      next: (keys) => (this.serviceKeys = keys),
      error: () => undefined,
    });
  }

  // ── LLM keys ──
  protected applyPreset(): void {
    const p: LlmVendorPreset | undefined = this.snapshot?.vendorPresets.find(
      (x) => x.vendor === this.newKey.vendor);
    if (p) {
      this.newKey.endpoint = p.endpoint ?? this.newKey.endpoint;
      this.newKey.model = p.model ?? this.newKey.model;
    }
  }

  protected addKey(): void {
    this.run('Key added.', this.settings.addKey(this.newKey), () => {
      this.newKey = { vendor: this.newKey.vendor };
      this.reload();
    });
  }

  protected toggleKey(k: LlmKeyInfo): void {
    this.run(k.enabled ? 'Key disabled.' : 'Key enabled.',
      this.settings.updateKey(k.id, { enabled: !k.enabled }), () => this.reload());
  }

  protected deleteKey(k: LlmKeyInfo): void {
    this.run('Key deleted.', this.settings.deleteKey(k.id), () => this.reload());
  }

  protected testKey(k: LlmKeyInfo): void {
    this.busy = true;
    this.error = this.notice = '';
    this.settings.testKey(k.id).subscribe({
      next: (r) => {
        this.busy = false;
        if (r.ok) this.notice = `${k.name}: ${r.message || 'key works'}`;
        else this.error = `${k.name}: ${r.message || 'test failed'}`;
        this.reload();
      },
      error: (e) => {
        this.busy = false;
        this.error = e?.message ?? 'Test failed.';
      },
    });
  }

  // ── service keys ──
  protected createServiceKey(): void {
    this.run('Key issued.', this.settings.createServiceKey(this.newServiceKeyName.trim()), (r) => {
      this.createdKey = r.apiKey; // shown ONCE
      this.newServiceKeyName = '';
      this.reload();
    });
  }

  protected revokeServiceKey(k: ServiceKeyInfo): void {
    this.run('Key revoked.', this.settings.revokeServiceKey(k.id), () => this.reload());
  }

  protected rotateServiceKey(k: ServiceKeyInfo): void {
    this.run('Key rotated.', this.settings.rotateServiceKey(k.id), (r) => {
      this.createdKey = r.apiKey; // shown ONCE
      this.reload();
    });
  }

  // ── voice / knowledge / password ──
  protected saveVoice(): void {
    if (!this.voice) return;
    this.run('Voice settings saved.', this.settings.saveVoice(this.voice), () => this.reload());
  }

  protected uploadDocument(ev: Event): void {
    const file = (ev.target as HTMLInputElement).files?.[0];
    if (!file) return;
    this.run('Document uploaded.', this.settings.uploadDocument(file), () => this.reload());
  }

  protected deleteDocument(d: KnowledgeDocumentInfo): void {
    this.run('Document deleted.', this.settings.deleteDocument(d.id), () => this.reload());
  }

  protected changePassword(): void {
    this.run('Password changed. Other sessions were signed out.',
      this.settings.changePassword(this.pwCurrent, this.pwNew), () => {
        this.pwCurrent = this.pwNew = '';
      });
  }

  private run<T>(okMessage: string, req: import('rxjs').Observable<T>, after: (r: T) => void): void {
    this.busy = true;
    this.error = this.notice = '';
    req.subscribe({
      next: (r) => {
        this.busy = false;
        this.notice = okMessage;
        after(r);
      },
      error: (e) => {
        this.busy = false;
        this.error = e?.message ?? 'Request failed.';
      },
    });
  }
}
