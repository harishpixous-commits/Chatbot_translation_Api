// Demo server: static files + /api/ai/translation/batch proxy to the Pixous
// sidecar, reproducing the Pixous.AI.AspNetCore response envelope exactly.
const http = require("http");
const fs = require("fs");
const path = require("path");

const SIDECAR = process.env.SIDECAR_URL || "http://127.0.0.1:8765";
const FAIL_MODE = process.env.FAIL_MODE === "1";

const server = http.createServer(async (req, res) => {
  if (req.method === "POST" && req.url.split("?")[0] === "/api/ai/translation/batch") {
    let body = "";
    req.on("data", (c) => (body += c));
    req.on("end", async () => {
      if (FAIL_MODE) {
        res.writeHead(500, { "content-type": "application/json" });
        res.end(JSON.stringify({ success: false, data: null, error: { code: "X", message: "boom" }, traceId: "t" }));
        return;
      }
      try {
        const { texts, targetLanguage, sourceLanguage } = JSON.parse(body);
        const mangle = new URL(req.url, "http://x").searchParams.get("mangle");
        if (mangle === "flaky") {
          // drops the LAST marker of skeleton texts on their FIRST sight only;
          // a retry (fresh text, new marker) succeeds. Counts requests per text.
          global.__flakySeen = global.__flakySeen || new Set();
          // success path emits Indic-script text (like a real engine) so the
          // SDK echo-retention check can pass; the dropped-first-sight path
          // keeps Latin to simulate a raw corrupted engine response.
          const upperKeepMarkers = (t) => t.split(/(<\/?x[a-z0-9]+t\d+\/?>)/g).map((seg) =>
            /^<\/?x[a-z0-9]+t\d+\/?>$/.test(seg) ? seg : seg.replace(/[A-Za-z]{2,}/g, "\u0bae\u0bca\u0bb4\u0bbf\u0baa\u0bc6\u0baf\u0bb0\u0bcd\u0baa\u0bcd\u0baa\u0bc1")
          ).join("");
          const out = texts.map((t) => {
            const ms = t.match(/<\/?x[a-z0-9]+t\d+\/?>/g) || [];
            // deterministic per-prefix loss (~1/6 of prefixes), like the real engine:
            // a fresh retry prefix succeeds, re-sending the same text fails identically
            if (ms.length && /<xaaaat\d+>/.test(t)) return upperKeepMarkers(t).replace(ms[ms.length - 1], "");
            return upperKeepMarkers(t);
          });
          res.writeHead(200, { "content-type": "application/json" });
          res.end(JSON.stringify({ success: true, data: { translatedTexts: out, targetLanguage, provider: "FlakyFixture" }, error: null, traceId: "t" }));
          return;
        }
        if (mangle === "echobrand") {
          // markers kept verbatim; text = Indic + heavy legitimate Latin (brands + URL):
          // rejected without allowlist (>0.6 Latin), applied with echoAllowlist brands
          const brandSentence = "\u0baa\u0bbf\u0b95\u0bcd\u0b9a\u0bcd Pixous GitHub Docker Kubernetes \u0b86\u0ba9\u0bcd\u0bb2\u0bc8\u0ba9\u0bcd https://pixous.example/app \u0b9a\u0bc6\u0baf\u0bb2\u0bbf ";
          const out = texts.map((t) => t.split(/(<\/?x[a-z0-9]+t\d+\/?>)/g).map((seg) =>
            /^<\/?x[a-z0-9]+t\d+\/?>$/.test(seg) || !seg.trim() ? seg : brandSentence
          ).join(""));
          res.writeHead(200, { "content-type": "application/json" });
          res.end(JSON.stringify({ success: true, data: { translatedTexts: out, targetLanguage, provider: "EchoBrandFixture" }, error: null, traceId: "t" }));
          return;
        }
        if (mangle === "tokencorrupt") {
          // engine MUTATES interpolation-token markers (the observed te/kn
          // {{date}} -> {{target-script}} defect): words translated, skeleton
          // markers kept, token markers replaced by target-script braces
          const out = texts.map((t) => t.split(/(<\/?x[a-z0-9]+[tk]\d+\/?>)/g).map((seg) => {
            if (/^<\/?x[a-z0-9]+k\d+\/?>$/.test(seg)) return "{{\u0b89\u0bb0\u0bc8}}";
            if (/^<\/?x[a-z0-9]+t\d+\/?>$/.test(seg)) return seg;
            return seg.replace(/[A-Za-z]{2,}/g, "\u0bae\u0bca\u0bb4\u0bbf\u0baa\u0bc6\u0baf\u0bb0\u0bcd\u0baa\u0bcd\u0baa\u0bc1");
          }).join(""));
          res.writeHead(200, { "content-type": "application/json" });
          res.end(JSON.stringify({ success: true, data: { translatedTexts: out, targetLanguage, provider: "TokenCorruptFixture" }, error: null, traceId: "t" }));
          return;
        }
        if (mangle === "roundtrip-ambiguous") {
          // kn #3 real mechanism: "Sign out" -> forward wrong label, back -> "Sign it"
          // (stem kept, direction word lost). Guard must fail closed.
          // Positive control: "Log out" -> back "Logout" (accepted paraphrase).
          const BAD = "\u0CB8\u0CB9\u0CBF \u0CAE\u0CBE\u0CA1\u0CBF";   // the actual wrong kn #3 output ("sahi maadi")
          const GOOD = "\u0CB2\u0CBE\u0C97\u0CCC\u0C9F\u0CCD";           // kn-script transliteration, stands in for a good "Log out"
          const BADUP = "\u0CB8\u0CBF\u0C97\u0CCD\u0CA8\u0CCD \u0C87\u0CA8\u0CCD"; // kn-script "sig-n in": stands in for a register-vs-login flip
          const BW = {}; BW[BAD] = "Sign it"; BW[GOOD] = "Logout"; BW[BADUP] = "Sign in";
          const FW = { "Sign out": BAD, "Log out": GOOD, "Sign up": BADUP };
          let out;
          if (sourceLanguage === "kn" && targetLanguage === "en") {
            out = texts.map((t) => BW[t.trim()] !== undefined ? BW[t.trim()] : t.replace(/[A-Za-z]{2,}/g, (w) => "word" + w.length));
          } else {
            out = texts.map((t) => t.split(/(<\/?x[a-z0-9]+[tk]\d+\/?>)/g).map((seg) => {
              if (/^<\/?x[a-z0-9]+[tk]\d+\/?>$/.test(seg)) return seg;
              return FW[seg.trim()] !== undefined ? seg.replace(seg.trim(), FW[seg.trim()]) : seg.replace(/[A-Za-z]{2,}/g, "knword");
            }).join(""));
          }
          res.writeHead(200, { "content-type": "application/json" });
          res.end(JSON.stringify({ success: true, data: { translatedTexts: out, targetLanguage, provider: "AmbFixture" }, error: null, traceId: "t" }));
          return;
        }
        if (mangle === "xss") {
          // HOSTILE-OUTPUT fixture: the "engine" returns target-script words
          // plus executable-looking markup in every shape (svg onload, img
          // onerror, script tag). The SDK must land all of it as INERT text
          // (nodeValue / safe-attribute writes only): no new elements, no
          // inline handlers, nothing executed. Skeleton markers kept verbatim
          // so the skeleton path is exercised too. Back direction returns the
          // text unchanged (no polarity/direction words -> guard passes), so
          // the round-trip guard stays at its shipped default (ON).
          const TA = "\u0bae\u0bca\u0bb4\u0bbf\u0baa\u0bc6\u0baf\u0bb0\u0bcd\u0baa\u0bcd\u0baa\u0bc1 ".repeat(6);
          const PAYLOAD = "<svg onload=window.__xss1=1> <img src=x onerror=window.__xss2=1> <script>window.__xss3=1</" + "script>";
          let out;
          if (sourceLanguage === "ta" && targetLanguage === "en") {
            out = texts.map((t) => t); // unchanged: no polarity/direction words
          } else {
            out = texts.map((t) => t.split(/(<\/?x[a-z0-9]+[tk]\d+\/?>)/g).map((seg) =>
              /^<\/?x[a-z0-9]+[tk]\d+\/?>$/.test(seg) ? seg : " " + TA + PAYLOAD + " "
            ).join(""));
          }
          res.writeHead(200, { "content-type": "application/json" });
          res.end(JSON.stringify({ success: true, data: { translatedTexts: out, targetLanguage, provider: "XssFixture" }, error: null, traceId: "t" }));
          return;
        }
        if (mangle === "roundtrip" || mangle === "roundtrip-failback") {
          // deterministic round-trip fixture: forward maps "Sign out" to the
          // known wrong Tamil; back maps it to "Sign In" (opposite meaning).
          const FW = { "Sign out": "\u0b89\u0bb3\u0bcd\u0ba8\u0bc1\u0bb4\u0bc8\u0baf\u0bc1\u0b99\u0bcd\u0b95\u0bb3\u0bcd", "Deny": "\u0b85\u0ba9\u0bc1\u0bae\u0ba4\u0bbf" };
          const BW = { "\u0b89\u0bb3\u0bcd\u0ba8\u0bc1\u0bb4\u0bc8\u0baf\u0bc1\u0b99\u0bcd\u0b95\u0bb3\u0bcd": "Sign In", "\u0b85\u0ba9\u0bc1\u0bae\u0ba4\u0bbf \u0bae\u0bca\u0bb4\u0bbf\u0baa\u0bc6\u0baf\u0bb0\u0bcd\u0baa\u0bcd\u0baa\u0bc1": "Allow access" };
          let out;
          if (mangle === "roundtrip-failback" && sourceLanguage === "ta") {
            res.writeHead(500, { "content-type": "application/json" });
            res.end(JSON.stringify({ success: false, data: null, error: { code: "BACK_FAIL", message: "back-translation unavailable" }, traceId: "t" }));
            return;
          }
          if (sourceLanguage === "ta" && targetLanguage === "en") {
            out = texts.map((t) => BW[t.trim()] !== undefined ? BW[t.trim()] : t.replace(/[A-Za-z\u0b80-\u0bff]{2,}/g, (w) => "word" + w.length));
          } else {
            out = texts.map((t) => t.split(/(<\/?x[a-z0-9]+[tk]\d+\/?>)/g).map((seg) => {
              if (/^<\/?x[a-z0-9]+[tk]\d+\/?>$/.test(seg)) return seg;
              return FW[seg.trim()] !== undefined ? FW[seg.trim()] : seg.replace(/[A-Za-z]{2,}/g, "\u0bae\u0bca\u0bb4\u0bbf\u0baa\u0bc6\u0baf\u0bb0\u0bcd\u0baa\u0bcd\u0baa\u0bc1");
            }).join(""));
          }
          res.writeHead(200, { "content-type": "application/json" });
          res.end(JSON.stringify({ success: true, data: { translatedTexts: out, targetLanguage, provider: "RoundTripFixture" }, error: null, traceId: "t" }));
          return;
        }
        if (mangle === "tokenreorder") {
          // engine reverses token-marker order (aggressive SOV); words translated.
          // Named tokens may reorder safely; positional printf tokens may not.
          const out = texts.map((t) => {
            const ms = t.match(/<\/?x[a-z0-9]+k\d+\/?>/g) || [];
            let r = t;
            if (ms.length) {
              const rev = ms.slice().reverse(); let i = 0;
              r = r.replace(/<\/?x[a-z0-9]+k\d+\/?>/g, () => rev[i++]);
            }
            return r.split(/(<\/?x[a-z0-9]+[tk]\d+\/?>)/g).map((seg) =>
              /^<\/?x[a-z0-9]+[tk]\d+\/?>$/.test(seg) ? seg : seg.replace(/[A-Za-z]{2,}/g, "\u0bae\u0bca\u0bb4\u0bbf\u0baa\u0bc6\u0baf\u0bb0\u0bcd\u0baa\u0bcd\u0baa\u0bc1")
            ).join("");
          });
          res.writeHead(200, { "content-type": "application/json" });
          res.end(JSON.stringify({ success: true, data: { translatedTexts: out, targetLanguage, provider: "TokenReorderFixture" }, error: null, traceId: "t" }));
          return;
        }
        if (mangle === "tokenok") {
          // engine keeps ALL markers verbatim and translates words
          const out = texts.map((t) => t.split(/(<\/?x[a-z0-9]+[tk]\d+\/?>)/g).map((seg) =>
            /^<\/?x[a-z0-9]+[tk]\d+\/?>$/.test(seg) ? seg : seg.replace(/[A-Za-z]{2,}/g, "\u0bae\u0bca\u0bb4\u0bbf\u0baa\u0bc6\u0baf\u0bb0\u0bcd\u0baa\u0bcd\u0baa\u0bc1")
          ).join(""));
          res.writeHead(200, { "content-type": "application/json" });
          res.end(JSON.stringify({ success: true, data: { translatedTexts: out, targetLanguage, provider: "TokenOkFixture" }, error: null, traceId: "t" }));
          return;
        }
        if (mangle === "echo") {
          // returns text nearly unchanged (source retention) with markers intact
          const out = texts.map((t) => t.replace(/\bnested\b/i, "NESTED"));
          res.writeHead(200, { "content-type": "application/json" });
          res.end(JSON.stringify({ success: true, data: { translatedTexts: out, targetLanguage, provider: "EchoFixture" }, error: null, traceId: "t" }));
          return;
        }
        if (mangle === "flakyonce-reset") { global.__flakyOnceLeft = 2; res.writeHead(200, { "content-type": "application/json" }); res.end(JSON.stringify({ success: true, data: { translatedTexts: texts, targetLanguage, provider: "Reset" }, error: null, traceId: "t" })); return; }
        if (mangle === "flakyonce") {
          // first TWO requests drop a marker like "flaky"; afterwards clean
          global.__flakyOnceLeft = (global.__flakyOnceLeft === undefined ? 2 : global.__flakyOnceLeft);
          const lossy = global.__flakyOnceLeft-- > 0;
          const out = texts.map((t) => lossy
            ? t.replace(/<x[a-z0-9]+t\d+>/, "") // drop first OPENING skeleton marker (token k-markers untouched)
            : t.split(/(<\/?x[a-z0-9]+[tk]\d+\/?>)/g).map((seg) =>
                /^<\/?x[a-z0-9]+[tk]\d+\/?>$/.test(seg) ? seg : seg.replace(/[A-Za-z]{2,}/g, "\u0bae\u0bca\u0bb4\u0bbf\u0baa\u0bc6\u0baf\u0bb0\u0bcd\u0baa\u0bcd\u0baa\u0bc1")
              ).join(""));
          res.writeHead(200, { "content-type": "application/json" });
          res.end(JSON.stringify({ success: true, data: { translatedTexts: out, targetLanguage, provider: "FlakyOnceFixture" }, error: null, traceId: "t" }));
          return;
        }
        if (mangle === "padfixture") {
          // deterministic fixture: keep markers verbatim, "translate" words by
          // uppercasing, and pad around markers exactly like real engines do
          const out = texts.map((t) =>
            t.split(/(<\/?x[a-z0-9]+t\d+\/?>)/g).map((seg) =>
              /^<\/?x[a-z0-9]+t\d+\/?>$/.test(seg) ? " " + seg + " " : seg.replace(/[A-Za-z]{2,}/g, (w) => "\u0bae\u0bca\u0bb4\u0bbf\u0baa\u0bc6\u0baf\u0bb0\u0bcd\u0baa\u0bcd\u0baa\u0bc1" + w.toUpperCase())
            ).join("").replace(/  +/g, " ").trim()
          );
          res.writeHead(200, { "content-type": "application/json" });
          res.end(JSON.stringify({ success: true, data: { translatedTexts: out, targetLanguage, provider: "PadFixture" }, error: null, traceId: "t" }));
          return;
        }
        if (mangle) {
          // adversarial fixture: corrupt placeholder markers in the "translation"
          const out = texts.map((t) => {
            const ms = t.match(/<\/?x[a-z0-9]+t\d+\/?>/g) || [];
            if (!ms.length) return t + " (ta)";
            let r = t.replace("Nested", "NESTED-TA");
            if (mangle === "drop") r = r.replace(ms[ms.length - 1], "");
            else if (mangle === "reorder") { r = r.replace(ms[0], "@@TMP@@").replace(ms[1], ms[0]).replace("@@TMP@@", ms[1]); }
            else if (mangle === "malformed") r = r.replace(ms[0], "<xZZZt999>");
            return r;
          });
          res.writeHead(200, { "content-type": "application/json" });
          res.end(JSON.stringify({ success: true, data: { translatedTexts: out, targetLanguage, provider: "MangleFixture" }, error: null, traceId: "t" }));
          return;
        }
        // Propagate a browser abort to the sidecar (its disconnect polling then cancels the job).
        const ac = new AbortController();
        res.on("close", () => { if (!res.writableEnded) ac.abort(); });
        const r = await fetch(`${SIDECAR}/translate`, {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({ source: sourceLanguage, target: targetLanguage, items: texts }),
          signal: ac.signal,
        });
        const data = await r.json();
        const q = data.quality && data.quality.checked === true && Array.isArray(data.quality.script_anomaly_items) ? data.quality.script_anomaly_items : undefined;
        res.writeHead(200, { "content-type": "application/json" });
        res.end(JSON.stringify({
          success: true,
          data: { translatedTexts: data.translations, targetLanguage, provider: "PixousSelfHosted", scriptAnomalyIndexes: q },
          error: null, traceId: "t",
        }));
      } catch (e) {
        res.writeHead(502, { "content-type": "application/json" });
        res.end(JSON.stringify({ success: false, data: null, error: { code: "UPSTREAM", message: String(e) }, traceId: "t" }));
      }
    });
    return;
  }
  let file = req.url.split("?")[0]; if (file === "/") file = "/demo.html";
  // Portable paths: the SDK under test lives one level up; fixtures sit next
  // to this server. No machine-absolute paths, so the battery runs identically
  // on any dev box and CI (Windows included).
  const src = file === "/pixous-i18n.js" ? path.join(__dirname, "..", "pixous-i18n.js") : path.join(__dirname, file);
  try {
    const content = fs.readFileSync(src);
    res.writeHead(200, { "content-type": file.endsWith(".js") ? "text/javascript" : "text/html" });
    res.end(content);
  } catch {
    res.writeHead(404); res.end("nf");
  }
});
server.listen(process.env.PORT || 8901, "127.0.0.1", () => console.log("demo up"));
