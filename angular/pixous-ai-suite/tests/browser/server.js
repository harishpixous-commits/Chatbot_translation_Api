// Widget test host: static ESM (importmap + extensionless resolution), zone.js,
// compiled app, and Pixous-shaped API stubs (envelope + SSE + error frames).
const http = require("http");
const fs = require("fs");
const path = require("path");
const NG = require("path").join(__dirname, "..", "..", "node_modules");
const OUT = require("path").join(__dirname, ".build", "out");

const PAGE = `<!DOCTYPE html><html><head><meta charset="utf-8">
<script src="/ng/zone.js/bundles/zone.umd.js"></script>
<script type="importmap">${JSON.stringify({ imports: {
  "@angular/core": "/ng/@angular/core/fesm2022/core.mjs",
  "@angular/compiler": "/ng/@angular/compiler/fesm2022/compiler.mjs",
  "@angular/core/primitives/signals": "/ng/@angular/core/fesm2022/primitives/signals.mjs",
  "@angular/core/primitives/event-dispatch": "/ng/@angular/core/fesm2022/primitives/event-dispatch.mjs",
  "@angular/core/rxjs-interop": "/ng/@angular/core/fesm2022/rxjs-interop.mjs",
  "@angular/common": "/ng/@angular/common/fesm2022/common.mjs",
  "@angular/common/http": "/ng/@angular/common/fesm2022/http.mjs",
  "@angular/forms": "/ng/@angular/forms/fesm2022/forms.mjs",
  "@angular/platform-browser": "/ng/@angular/platform-browser/fesm2022/platform-browser.mjs",
  "rxjs": "/ng/rxjs/dist/esm/index.js",
  "rxjs/operators": "/ng/rxjs/dist/esm/operators/index.js",
  "tslib": "/ng/tslib/tslib.es6.mjs",
} })}</script></head>
<body><app-root></app-root><script type="module" src="/app/main.js"></script></body></html>`;

function sendSSE(res, frames) {
  res.writeHead(200, { "content-type": "text/event-stream" });
  res.end(frames.map((f) => "data: " + f + "\n\n").join(""));
}

const server = http.createServer((req, res) => {
  const u = new URL(req.url, "http://x");
  if (u.pathname === "/api/ai/chat" && req.method === "POST") {
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify({ success: true, data: { reply: "STUB-REPLY vanakkam-ok" }, error: null, traceId: "t" }));
    return;
  }
  if (u.pathname === "/api/ai/chat/stream" && req.method === "POST") {
    let body = "";
    req.on("data", (c) => (body += c));
    req.on("end", () => {
    let fail = false;
    try { fail = /will break/.test(JSON.parse(body).message || ""); } catch { /* malformed test body: not our scenario */ }
    if (fail) {
      sendSSE(res, ["partial-text", JSON.stringify({ error: "acct-0000 quxberry private sentence sk-liveSENTINELkey9999", code: "UPSTREAM_ABORTED" })]);
    } else {
      sendSSE(res, ["Vann", "akkam", "[DONE]"]);
    }
    });
    return;
  }
  if (u.pathname === "/widget.html" || u.pathname === "/") {
    res.writeHead(200, { "content-type": "text/html" });
    res.end(PAGE);
    return;
  }
  let base = null, p = u.pathname;
  if (p.startsWith("/ng/")) { base = NG; p = p.slice(3); }
  else if (p.startsWith("/app/")) { base = OUT; p = p.slice(4); }
  if (base) {
    for (const cand of [p, p + ".js", p + ".mjs", p + "/index.js"]) {
      const f = path.join(base, cand);
      try {
        const content = fs.readFileSync(f);
        res.writeHead(200, { "content-type": f.endsWith(".map") ? "application/json" : "text/javascript" });
        res.end(content);
        return;
      } catch { /* try next */ }
    }
  }
  res.writeHead(404); res.end("nf " + u.pathname);
});
server.listen(8903, "127.0.0.1", () => console.log("widget host up"));
