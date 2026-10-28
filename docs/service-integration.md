# Shared Service API — use the chatbot from ANY project

The hosted suite exposes a tech-neutral HTTP surface at `/api/ai/v1/*`. Any
backend that can make an HTTPS call — .NET, Node, Python, Java, PHP, Go —
can use the chatbot and translator. You do **not** need the .NET or Angular
libraries; those are optional conveniences for their own stacks.

You need exactly two things:

1. **The base URL** of the running service, e.g. `https://ai.yourcompany.com`
2. **A project API key** (`pk_...`) issued from the settings panel

> The key belongs in YOUR backend's server-side configuration (environment
> variable / secret store). Never ship it to a browser, mobile app or public
> repo — anyone holding it can spend your project's request budget.

## 1. Run the service

```bash
cd dotnet/samples/SampleApi        # or your own host that calls AddAiSuite + MapAiSuiteEndpoints
dotnet run                          # listens on http://localhost:5000 by default
```

Production checklist (all documented in [deployment.md](deployment.md) and
[security.md](security.md)):

- `ASPNETCORE_ENVIRONMENT=Production`
- Set a real settings password: `AiSuite__Settings__Password` (env var / User
  Secrets / Key Vault). The shipped default is **refused** outside Development.
- Add your LLM vendor key in ⚙ Settings → LLM APIs (OpenAI-compatible,
  Anthropic, Gemini), and a translator (Azure Translator / LibreTranslate).
  These keys stay on this server; consumer projects never see them.
- Put the service behind HTTPS.

## 2. Issue a project key (once per consumer project)

Open the widget's ⚙ Settings panel, log in, and create a key under
**Service keys**, or call the endpoint directly:

```bash
TOKEN=$(curl -s -X POST $BASE/api/ai/settings/login \
  -H 'Content-Type: application/json' \
  -d '{"password":"<settings-password>"}' | jq -r .data.token)

curl -s -X POST $BASE/api/ai/settings/service-keys \
  -H "X-Settings-Token: $TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"billing-api"}'
# → data.apiKey = "pk_..."  SHOWN ONCE. Store it now.
```

List / revoke / rotate (rotate kills the old key instantly and shows the new
one once):

```bash
curl -s $BASE/api/ai/settings/service-keys -H "X-Settings-Token: $TOKEN"
curl -s -X POST $BASE/api/ai/settings/service-keys/<id>/revoke -H "X-Settings-Token: $TOKEN"
curl -s -X POST $BASE/api/ai/settings/service-keys/<id>/rotate  -H "X-Settings-Token: $TOKEN"
```

Only the SHA-256 hash of each key is stored (`service-keys.json`, mode 0600).
Each key gets its own rate limit (60 requests/minute), usage counters, and
session namespace — project A cannot read project B's conversations.

## 3. Call it — from any language

Every request sends the header `X-Api-Key: pk_...`. Responses use the standard
envelope `{ success, data, error: { code, message }, traceId }`.

### curl

```bash
curl -s -X POST $BASE/api/ai/v1/chat \
  -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"message":"Hello","sessionId":"user-123"}'
```

### JavaScript / TypeScript (Node 18+, any framework)

```js
const res = await fetch(`${BASE}/api/ai/v1/chat`, {
  method: "POST",
  headers: { "X-Api-Key": process.env.PIXOUS_KEY, "Content-Type": "application/json" },
  body: JSON.stringify({ message: "Hello", sessionId: "user-123" })
});
const { success, data, error } = await res.json();
if (!success) throw new Error(`${error.code}: ${error.message}`);
console.log(data.reply);
```

Streaming (SSE, incremental text):

```js
const res = await fetch(`${BASE}/api/ai/v1/chat/stream`, {
  method: "POST",
  headers: { "X-Api-Key": process.env.PIXOUS_KEY, "Content-Type": "application/json" },
  body: JSON.stringify({ message: "Hello", sessionId: "user-123" })
});
const reader = res.body.getReader();
const decoder = new TextDecoder();
let buf = "";
while (true) {
  const { done, value } = await reader.read();
  if (done) break;
  buf += decoder.decode(value, { stream: true });
  const events = buf.split("\n\n"); buf = events.pop();
  for (const ev of events) {
    const line = ev.split("\n").find(l => l.startsWith("data:"));
    if (!line) continue;
    const payload = line.slice(5).trimStart();
    if (payload === "[DONE]") break;
    process.stdout.write(payload.replace(/\\n/g, "\n")); // incremental token
  }
}
```

### Python (stdlib only)

```python
import json, os, urllib.request

req = urllib.request.Request(
    f"{BASE}/api/ai/v1/chat",
    data=json.dumps({"message": "Hello", "sessionId": "user-123"}).encode(),
    headers={"X-Api-Key": os.environ["PIXOUS_KEY"], "Content-Type": "application/json"},
    method="POST")
with urllib.request.urlopen(req) as res:
    body = json.load(res)
print(body["data"]["reply"])
```

### Endpoints

| Method | Path                  | Purpose                                  |
|--------|-----------------------|------------------------------------------|
| POST   | `/api/ai/v1/chat`           | Chat (JSON reply)                    |
| POST   | `/api/ai/v1/chat/stream`    | Chat (SSE incremental stream)        |
| POST   | `/api/ai/v1/translate`      | Translate one text                   |
| POST   | `/api/ai/v1/translate/batch`| Translate 1–100 texts                |
| POST   | `/api/ai/v1/detect-language`| Detect a text's language             |
| GET    | `/api/ai/v1/languages`      | List supported languages             |

`sessionId` is optional; when given, conversation history is remembered per
session **within your project only** (the service prefixes it with your key's
id internally — you will see that prefix in the response).

### Errors

| HTTP | `error.code`              | Meaning                                        |
|------|---------------------------|------------------------------------------------|
| 401  | `SERVICE_KEY_INVALID`     | Missing / unknown / revoked key                |
| 429  | `SERVICE_KEY_RATE_LIMITED`| Key exceeded 60 req/min — see `Retry-After`    |
| 502  | `AI_PROVIDER_UNAVAILABLE` | The configured LLM vendor failed (see server logs) |
| 502  | `TRANSLATION_PROVIDER_UNAVAILABLE` | The configured translator failed       |

Rate limiting here is REQUEST-rate limiting per project key. It is not a
provider spend cap — configure budgets and alerts at your LLM vendor.

## Deployment limits and trust boundaries

Read these before exposing the service:

- **Two surfaces, two audiences.** `/api/ai/v1/*` is server-to-server: every
  call needs a project key. `/api/ai/*` (chat, translate, voice) is the PUBLIC
  widget surface — it exists so website visitors can chat, and it spends the
  same provider quota. It is guarded by the default-ON per-IP rate limit
  (20 req/min), NOT by keys. If this deployment is not a public website
  chatbot, set `AiSuite:Security:RequireAuthorization = true` so the widget
  surface requires your host's auth too. Either way, real spend control is
  provider-side budgets and alerts — the rate limiter slows abuse, it is not
  a spend cap.
- **Admin auth is header-token based** (`X-Settings-Token` from the password
  login), not cookies — there is no ambient browser credential for the
  settings endpoints, so cross-site request forgery does not apply to them.
  The login itself is rate-limited with lockout.
- **Sessions**: the default conversation store is in-memory. A restart clears
  history, and multiple instances do not share sessions (plug in a shared
  `IConversationStore` for multi-instance). Each project key only ever sees
  its own session namespace — replaying another project's session id (even
  its internally prefixed form) lands in YOUR namespace, never theirs
  (covered by an integration test).
- **Raw keys are shown once** at creation/rotation. If a key leaks, revoke it
  from the settings panel — revocation is instant.
- **HTTPS only** in production; project keys and provider calls must not
  cross the internet in plaintext.

## What the .NET / Angular libraries add (optional)

- **.NET** (`Pixous.AI.*` NuGet packages): host the service inside your own
  ASP.NET Core app, or consume it typed. See [dotnet-integration.md](dotnet-integration.md).
- **Angular** (`pixous-ai-suite` package): drop-in chat widget + settings
  panel that talks to the same service. See [angular-integration.md](angular-integration.md).

A project key works the same whether the consumer uses these libraries or raw
HTTP as above.
