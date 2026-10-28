import '@angular/compiler';
import { Component } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { provideZoneChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { ChatWidgetComponent } from './.build/pixous/chat-widget.component';
import { AI_SUITE_CONFIG } from './.build/pixous/ai-chat.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [ChatWidgetComponent],
  template: '<pix-chat-widget [streaming]="stream"></pix-chat-widget>',
})
class AppRoot {
  stream = new URLSearchParams(location.search).has('stream');
}

bootstrapApplication(AppRoot, {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideHttpClient(),
    { provide: AI_SUITE_CONFIG, useValue: { apiBaseUrl: '' } },
  ],
}).then(() => { (window as any).__booted = true; })
 .catch((e) => { (window as any).__bootError = String(e && (e.stack || e)); console.error(e); });
