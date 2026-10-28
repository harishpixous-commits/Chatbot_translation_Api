# Migrating v1.1.0 -> v1.2.0

## .NET (all four packages 1.1.0 -> 1.2.0)
- No breaking API changes. Existing providers (Echo/AzureTranslator/LibreTranslate)
  and chat providers behave exactly as in v1.1.0.
- NEW: `AiSuite:PixousEngine` section + `TranslationProvider: "PixousSelfHosted"`.
  Opt-in only; existing configs work unchanged.
- New dependency for self-hosted translation: the engine sidecar
  (engine-sidecar/, Python 3.10+, ~900 MB model cache, CPU). Without it, keep
  your existing provider and nothing changes.

## Angular (1.1.0 -> 1.2.0)
- No component/API changes; version alignment only. Reinstall the tgz and rebuild.

## Website SDK (new in 1.2.0)
- Add sdk/pixous-i18n.js to pages; init with your source language; wire your
  language selector to PixousI18n.setLanguage(code).
- Point `endpoint` at your host's /api/ai/translation/batch (default).
- Persistence is OFF by default (memory-only). To survive reloads set
  `persistCache: true` AND call `PixousI18n.clearCache()` on logout/account
  switch — the SDK cannot know your session lifecycle.
- Mark anything private with data-no-translate / translate="no" / .no-translate.
  Form values, textareas, contenteditable and password fields are never sent.

## Engine sidecar (new in 1.2.0)
- `./setup.sh` downloads pinned model revisions; `./run.sh` serves 127.0.0.1:8765.
- Before network exposure: set PIXOUS_ENGINE_TOKEN and put TLS in front.
