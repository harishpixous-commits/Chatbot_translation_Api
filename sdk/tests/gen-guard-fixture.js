// Renders the 10 review-fixture strings through the SDK with the round-trip
// polarity guard LIVE against the real engine, per language, and records the
// DOM outcome per string (applied translation vs preserved original).
const { chromium } = require("playwright-core");
const fs = require("fs");
const STRINGS = [
  "Home", "Settings", "Sign out", "Search results", "Welcome to our website.",
  "Your order has been shipped and will arrive on {{date}}.",
  "Please enter a valid email address.",
  "Pixous Assistant helps you translate your website.",
  "Are you sure you want to delete 3 items?",
];
(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME_EXE, args: ["--no-sandbox", "--disable-dev-shm-usage"] });
  const out = { captured: new Date().toISOString(), guard: "roundTripCheck=true (default), markerRetry=true (default)", engine: "IndicTrans2 en-indic/indic-en dist-200M CT2 int8 via local sidecar", languages: {} };
  for (const lang of (process.env.ONLY_LANGS ? process.env.ONLY_LANGS.split(",") : ["ta", "hi", "te", "ml", "kn"])) {
    const page = await browser.newPage();
    await page.goto("http://127.0.0.1:8901/demo.html", { waitUntil: "domcontentloaded" });
    await page.evaluate((strings) => {
      document.body.innerHTML = strings.map((t, i) => `<p id="s${i}">${t.replace(/</g, "&lt;")}</p>`).join("");
    }, STRINGS);
    await page.addScriptTag({ url: "/pixous-i18n.js" });
    await page.evaluate(() => window.PixousI18n.init({ sourceLanguage: "en", endpoint: "/api/ai/translation/batch" }));
    await page.evaluate((l) => window.PixousI18n.setLanguage(l), lang);
    await page.waitForFunction(() => ["done", "partial", "error"].includes(document.documentElement.getAttribute("data-pixous-i18n")), null, { timeout: 60000 });
    const state = await page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n"));
    const rows = [];
    for (let i = 0; i < STRINGS.length; i++) {
      const dom = await page.evaluate((id) => document.getElementById(id).textContent, "s" + i);
      rows.push({ source: STRINGS[i], rendered: dom, outcome: dom === STRINGS[i] ? "PRESERVED-ORIGINAL (guard/quality fail-closed)" : "applied" });
    }
    out.languages[lang] = { state, rows };
    await page.close();
  }
  fs.writeFileSync(process.env.OUT_PATH || "/tmp/fixwork/docs/v2/six-language-guard-outcomes.json", JSON.stringify(out, null, 1));
  for (const l of Object.keys(out.languages)) {
    const r = out.languages[l];
    console.log(l, r.state, "| preserved:", r.rows.filter((x) => x.outcome.startsWith("PRESERVED")).map((x) => x.source.slice(0, 30)).join("; ") || "none");
  }
  await browser.close();
})();
