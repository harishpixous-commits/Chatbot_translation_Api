# Angular Integration (existing app, 4 steps)

1. `npm i @pixous/ai-suite @ngx-translate/core @ngx-translate/http-loader`
   (or copy `angular/pixous-ai-suite/src/lib` + `assets/i18n` into `shared/`).
2. Provide backend URL + TranslateModule once (`app.config.ts`):
   ```typescript
   import { AI_SUITE_CONFIG } from '@pixous/ai-suite';
   // + TranslateModule.forRoot({ defaultLanguage: 'en',
   //     loader: { provide: TranslateLoader, useFactory: httpLoaderFactory, deps: [HttpClient] } })
   { provide: AI_SUITE_CONFIG, useValue: { apiBaseUrl: 'https://api.myapp.com' } }
   ```
3. Drop widgets where needed (all inputs plain data - no secrets):
   ```html
   <pix-chat-widget title="Pixous Assistant" responseLanguage="ta"
     [quickReplies]="['Our Services','Products','Contact','Careers']" />
   <pix-language-switcher />
   <pixous-settings-panel />   <!-- admin page only, behind your own route guard -->
   ```
   Component API: `title`, `placeholder`, `responseLanguage` (backend
   allow-listed), `quickReplies` (set `[]` to hide), `streaming` (SSE, default
   ON), `allowUntranslatedStream` (default off - see below). Theme via `.pix-*` CSS.
4. Copy `assets/i18n/*.json` to `src/assets/i18n/`, add your keys, init once:
   `i18n.init('en', ['en','ta','hi','es','fr'])`.

## Streaming chat (`streaming` defaults to true)

The widget streams the reply token by token over SSE (`POST /api/ai/chat/stream`):

- **Translation routing:** streaming skips the backend post-translation step -
  a streamed reply stays in the model's own language. So when
  `responseLanguage` is set, the widget uses the POST path by default: the
  backend then applies the configured translation provider to the reply
  (with the keyless Echo provider that is a "[ta]"-style tag, not a
  translation; real translation quality depends on the configured provider
  and is unverified here). Set `[allowUntranslatedStream]="true"` to opt into
  streaming in the model's language anyway.
- The pending bubble fills incrementally; `AbortSignal` cancels the stream
  (cancellation is a deliberate stop, never retried).
- **Fallback to plain POST happens only when the server says the stream
  endpoint does not exist (HTTP 404/405 - an old backend).** The reply then
  arrives as one bubble, sent exactly once.
- Any other failure (401/403/429, 5xx, timeout, network drop, or a reply that
  had already started) does NOT re-send the request: the request may have been
  processed and billed, and re-sending could double-charge. The widget shows a
  message chosen by the failure (one shared `chatErrorText` table): a rejected
  message shows the server's own validation text (header stays Online, since
  the API answered); a stream the model server dropped mid-answer shows "cut
  off" (partial text stays visible); an unreachable API, an unreachable model
  server, a missing provider setup and a server error each get their own plain
  sentence; only an unclassified failure falls back to "the assistant is
  unavailable right now". The header shows nothing until an interaction proves
  the state, then "Online" or "Unavailable" (always a text label).
- A 4xx is the vendor's answer about the request, not proof of billing state;
  the provider dashboard is the final word on what was charged.
- `responseLanguage` is passed to the chat provider in both modes, so a real
  LLM is also instructed to answer in that language; the POST path additionally
  runs the reply through the configured translation provider. Neither is a
  guarantee of output language quality - that depends on the model/provider.

## Settings panel (`<pixous-settings-panel />`)

A drop-in admin console for the settings endpoints. Put it on an admin route
behind your own auth guard; the panel itself gates on the settings password
(`AiSuite:Settings:AdminPassword`) and stores the returned session token in
`sessionStorage` (tab-scoped, cleared on tab close; any 401 reopens the gate).

Tabs:

| Tab | What it manages |
|---|---|
| LLM keys | Provider keys CRUD. Priority order = fallback order. State chips: `untested` (added but never accepted by the vendor), `ok` (vendor accepted it - via Test or real traffic), `disabled`, `invalid` (401), `denied` (403), `cooling` (429 until Retry-After), `locked` (stored secret undecryptable under the current data-protection key ring - ciphertext preserved; delete + re-add to unlock). "Test" makes one live vendor call. |
| Service keys | Project keys for the shared `/api/ai/v1` surface. The raw key is shown ONCE in a dismissable banner (store it immediately); the table shows only the prefix, request count and state. Rotate / revoke take effect instantly. |
| Voice | STT/TTS enablement, models, voices; API keys stored encrypted server-side. |
| Knowledge | Upload `.pdf/.docx/.md/.txt` documents (chunked server-side), delete them. Other types are rejected with a clear error. |
| Password | Change the settings password (min 8 chars). |

No raw secret is ever rendered in the panel: stored keys come back masked
from the server, inputs are `type="password"`, and the one-time service-key
banner is the only exception by design.

## Services cheat sheet

| Member | Does |
|---|---|
| `AiChatService.send(msg, {responseLanguage?, provider?, sessionId?}, {echoUser?})` | POST `/api/ai/chat`, updates `messages$`/`typing$`. `echoUser:false` skips the local user bubble (used by the stream fallback, which already echoed it) |
| `AiChatService.stream(msg, opts?, signal?)` | SSE `/api/ai/chat/stream` as `AsyncGenerator<string>`; stream errors carry `.status` |
| `AiChatService.pushError(msg)` / `retractPending()` | Append a visible error bubble / drop the empty streaming placeholder |
| `AiChatService.getSelection()/setSelection()` | Live provider switching (admin UI) |
| `AiSettingsService` | Login + snapshot, LLM keys, service keys, voice, knowledge, password for the panel |
| `AiTranslationService.use()/instant()` | Runtime UI strings via ngx-translate |
| `AiTranslationService.translate()/translateBatch()/detect()` | Backend translation (envelope-unwrapped) |
| `AiTranslationService.supportedLanguages()` | Backend provider's languages |
| `aiSuiteAuthInterceptor(getToken)` | Attaches YOUR app token to `/api/ai/*` (never an AI key) |
