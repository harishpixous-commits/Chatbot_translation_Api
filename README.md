# Pixous Assistant - Chatbot + Translation API

Reusable, self-hosted AI module: chatbot (.NET 8/10 library + llama.cpp sidecar,
Qwen3-4B) and whole-site translation (self-hosted IndicTrans2 engine,
en/ta/hi/te/ml/kn), with an Angular widget library and a plain-JS SDK.
No external AI provider calls.

- dotnet/ - the .NET module (src + tests + SampleApi demo host)
- engine-sidecar/ - FastAPI IndicTrans2 translation engine (CPU, int8)
- chat-sidecar/ - llama.cpp chat sidecar notes/config
- angular/ - pixous-ai-suite Angular library (+ real-runtime browser tests)
- sdk/ - pixous-i18n.js drop-in translation SDK (+ browser test battery)
- docs/ - setup, integration, security, quality notes

CI: .github/workflows/windows-verify.yml runs the full verification stack on
windows-latest (manual "Run workflow" trigger): .NET unit+integration suites,
engine sidecar tests with pinned model revisions, SDK browser battery and
Angular widget tests on system Chrome, llama.cpp smoke test - bounded by a
45-minute timeout.
