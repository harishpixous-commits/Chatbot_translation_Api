// ── Public contracts mirroring the .NET DTOs ─────────────────────────────
// Keep these in sync with Pixous.AiSuite.Services DTOs.

export interface ChatMessage {
  role: 'system' | 'user' | 'assistant';
  content: string;
}

export interface ChatRequest {
  message: string;
  history?: ChatMessage[];
  responseLanguage?: string;
  provider?: string;
  /** Optional server-side session (IConversationStore). Omit for stateless mode. */
  sessionId?: string;
}

export interface ChatResponse {
  reply: string;
  provider: string;
  model?: string;
  responseLanguage?: string;
  sessionId?: string;
}

export interface TranslateRequest {
  text: string;
  targetLanguage: string;
  sourceLanguage?: string;
  provider?: string;
}

export interface TranslateResponse {
  translatedText: string;
  targetLanguage: string;
  detectedSourceLanguage?: string;
  provider: string;
}

export interface SupportedLanguage {
  code: string;
  name: string;
}

/** Standard backend envelope { success, data, error{ code, message }, traceId }. */
export interface ApiEnvelope<T> {
  success: boolean;
  data: T;
  error: { code: string; message: string } | null;
  traceId: string;
}

export interface ProviderSelection {
  chatProvider: string;
  translationProvider: string;
  runtimeOverride: { chatProvider: string | null; translationProvider: string | null };
  availableChatProviders: string[];
  availableTranslationProviders: string[];
}

export interface ServiceInfo {
  name: string;
  provider: string;
  apiKeyConfigKey: string | null;
  apiKeyConfigured: boolean;
}

/** Drop-in configuration for the library. No secrets here — the browser
 *  only ever talks to YOUR backend (BFF proxy), which holds the API keys. */
export interface AiSuiteConfig {
  /** Base URL of your backend, e.g. 'https://api.myapp.com' (no trailing slash). */
  apiBaseUrl: string;
  defaultLanguage?: string;
  supportedLanguages?: string[];
}

// ── ⚙ Settings surface (password-gated admin API) ───────────────────────
// Mirrors AiSuiteSettingsEndpoints DTOs. Raw API keys are write-only:
// the backend only ever returns masked values (e.g. "sk-a…1234").

export interface SettingsSecurityInfo {
  passwordSource: 'config' | 'stored';
  persistKeys: boolean;
  storeDirectory: string;
  minPasswordLength: number;
  tokenHours: number;
}

/** Auto-fallback health of one saved key. */
export interface LlmKeyStatus {
  state?: string | null; // 'ok' | 'invalid' | 'denied' | 'cooling' | 'locked' | null
  untilUtc?: string | null;
  lastStatus?: number | null;
  lastError?: string | null;
}

/** Key as returned by the API — masked, never raw. */
export interface LlmKeyInfo {
  id: string;
  name: string;
  vendor: string;
  endpoint: string | null;
  model: string | null;
  apiKeyMasked: string | null;
  hasKey: boolean;
  maxTokens: number | null;
  temperature: number | null;
  enabled: boolean;
  priority: number;
  status: LlmKeyStatus | null;
}

/** Write shape for add/update. apiKey is optional on update = keep stored key. */
export interface LlmKeyInput {
  name?: string | null;
  vendor: string;
  endpoint?: string | null;
  model?: string | null;
  apiKey?: string | null;
  maxTokens?: number | null;
  temperature?: number | null;
  enabled?: boolean;
}

export type LlmKeyMove = 'up' | 'down';

export interface LlmKeyUpdate extends Partial<LlmKeyInput> {
  move?: LlmKeyMove;
}

/** Voice config as returned by the API — keys masked, hasKey flags only. */
export interface VoiceSettingsInfo {
  sttEnabled: boolean;
  sttProvider: string;
  sttEndpoint: string | null;
  sttModel: string | null;
  sttKeyMasked: string;
  sttHasKey: boolean;
  ttsEnabled: boolean;
  ttsProvider: string;
  ttsEndpoint: string | null;
  ttsModel: string | null;
  ttsVoice: string | null;
  ttsKeyMasked: string;
  ttsHasKey: boolean;
}

/** PUT /voice body. Send the full object; omit/blank apiKey = keep stored. */
export interface VoiceSettingsWrite {
  sttEnabled: boolean;
  sttProvider: string;
  sttEndpoint?: string;
  sttModel?: string;
  /** Blank or omitted = keep the stored key. Never echoed back. */
  sttApiKey?: string | null;
  ttsEnabled: boolean;
  ttsProvider: string;
  ttsEndpoint?: string;
  ttsModel?: string;
  ttsVoice?: string;
  /** Blank or omitted = keep the stored key. Never echoed back. */
  ttsApiKey?: string | null;
}

export interface KnowledgeDocumentInfo {
  id: string;
  fileName: string;
  sizeBytes: number;
  chunkCount: number;
  uploadedAtUtc: string;
}

export interface KnowledgeUploadResult {
  document: KnowledgeDocumentInfo;
  characters: number;
  warnings: string[];
}

/** Built-in vendor presets for the key form (any vendor works — no lock-in). */
export interface LlmVendorPreset {
  vendor: string;
  label: string;
  endpoint: string | null;
  model: string | null;
  requiresKey: boolean;
}

/** Project API key metadata for the shared /api/ai/v1 surface (raw key is never returned). */
export interface ServiceKeyInfo {
  id: string;
  name: string;
  prefix: string;
  createdUtc: string;
  revokedUtc: string | null;
  totalRequests: number;
  lastUsedUtc: string | null;
}

/** Returned ONCE at creation/rotation: the only time the raw key is visible. */
export interface ServiceKeyCreated {
  apiKey: string;
  key: ServiceKeyInfo;
  message: string;
}

export interface SettingsSnapshot {
  security: SettingsSecurityInfo;
  chatProvider: string;
  llmKeys: LlmKeyInfo[];
  voice: VoiceSettingsInfo;
  documents: KnowledgeDocumentInfo[];
  vendorPresets: LlmVendorPreset[];
}

export interface SettingsSession {
  token: string;
  expiresAtUtc?: string | null;
}

/** Result of POST /settings/llm-keys/{id}/test (secret-free by design). */
export interface LlmKeyTestResult {
  ok: boolean;
  latencyMs: number;
  statusCode: number | null;
  model: string | null;
  message: string | null;
}

export interface ChangePasswordResult {
  changed: boolean;
  passwordSource: 'config' | 'stored';
}

// ── Runtime voice (public when enabled in settings) ──────────────────────

export interface SttResult {
  text: string;
  provider: string;
}
