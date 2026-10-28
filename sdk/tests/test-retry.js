// markerRetry (opt-in) + echo-retention detection contracts.
const { chromium } = require("playwright-core");
const EXE = process.env.CHROME_EXE;
let pass = 0, fail = 0;
function check(name, ok, detail) {
  console.log((ok ? "PASS" : "FAIL") + "  " + name + (detail ? "  | " + String(detail).slice(0, 100) : ""));
  ok ? pass++ : fail++;
}
async function newPage(browser, mode, extra) {
  const page = await browser.newPage();
  page.on("console", (m) => { if (m.text().includes("PXTRACE")) console.log("   [pg]", m.text().slice(0, 150)); });
  page.on("console", m => { if (m.text().includes("PXTRACE")) console.log(m.text().slice(0,140)); });
  let batchReqs = 0;
  page.on("request", (r) => { if (r.url().includes("/translation/batch")) batchReqs++; });
  await page.route("**/api/ai/translation/batch**", (route) => {
    const url = new URL(route.request().url());
    url.searchParams.set("mangle", mode);
    route.continue({ url: url.toString() });
  });
  await page.goto("http://127.0.0.1:8901/", { waitUntil: "networkidle" });
  if (extra) await page.evaluate(extra);
  return { page, reqs: () => batchReqs };
}
(async () => {
  const browser = await chromium.launch({ executablePath: EXE, args: ["--no-sandbox", "--disable-dev-shm-usage"] });

  // 1. default OFF: flaky marker loss -> originals preserved + partial, NO retry fetch
  {
    const { page, reqs } = await newPage(browser, "flaky");
    await page.evaluate(() => {
      window.PixousI18n.destroy();
      const s = document.createElement("script"); s.src = "/pixous-i18n.js";
      s.onload = () => {
        const seq = ["<xaaaat", "<xbbbbt", "<xcccct", "<xddddt"]; let i = 0;
        window.PixousI18n.init({ sourceLanguage: "en", endpoint: "/api/ai/translation/batch", markerRetry: false, markFactory: () => seq[i++ % seq.length] });
        window.__ready = true;
      };
      document.head.appendChild(s);
    });
    await page.waitForFunction(() => window.__ready, null, { timeout: 10000 });
    await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
    await page.waitForFunction(() => ["done","partial"].includes(document.documentElement.getAttribute("data-pixous-i18n")), null, { timeout: 20000 });
    // assert within the same pass, before the demo's 800ms dynamic node fires
    // its own legitimate new-plan fetch
    const nav = await page.textContent("nav");
    const st = await page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n"));
    check("retry OFF: failed block preserved in English", /Home/.test(nav) && !/HOME/.test(nav), nav.trim().slice(0, 50));
    check("retry OFF: state=partial", st === "partial", st);
    check("retry OFF: no recovery within the failed pass", /Home/.test(nav), nav.trim().slice(0, 30));
    await page.close();
  }

  // 2. DEFAULT (now ON): same flaky loss recovered by ONE bounded retry, state=done
  {
    const { page, reqs } = await newPage(browser, "flaky", () => window.PixousI18n.init && null);
    await page.evaluate(() => { window.PixousI18n.destroy(); });
    await page.evaluate(() => {
      const s = document.createElement("script"); s.src = "/pixous-i18n.js";
      s.onload = () => { window.PixousI18n.init({ sourceLanguage: "en", endpoint: "/api/ai/translation/batch?mangle=flaky" }); window.__ready = true; }; // default markerRetry=true
      document.head.appendChild(s);
    });
    await page.waitForFunction(() => window.__ready, null, { timeout: 10000 });
    await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
    await page.waitForFunction(() => ["done","partial"].includes(document.documentElement.getAttribute("data-pixous-i18n")), null, { timeout: 20000 });
    const nav = await page.textContent("nav");
    const st = await page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n"));
    check("retry DEFAULT-ON: block recovered by bounded retry", !/Home/.test(nav) && /\u0bae\u0bca\u0bb4\u0bbf/.test(nav), nav.trim().slice(0, 50));
    check("retry DEFAULT-ON: state=done", st === "done", st);
    await page.close();
  }

  // 3. echo fixture: source retention detected -> preserved + partial (retry ON must NOT apply echoed text)
  {
    const { page } = await newPage(browser, "echo");
    await page.evaluate(() => {
      window.PixousI18n.destroy();
      const s = document.createElement("script"); s.src = "/pixous-i18n.js";
      s.onload = () => { window.PixousI18n.init({ sourceLanguage: "en", endpoint: "/api/ai/translation/batch?mangle=echo", markerRetry: true }); window.__ready = true; };
      document.head.appendChild(s);
    });
    await page.waitForFunction(() => window.__ready, null, { timeout: 10000 });
    await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
    await page.waitForFunction(() => ["done","partial"].includes(document.documentElement.getAttribute("data-pixous-i18n")), null, { timeout: 20000 });
    await page.waitForTimeout(1500);
    const h1 = await page.textContent("h1");
    const st = await page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n"));
    check("echo: untranslated h1 NOT applied", /Welcome to our website/.test(h1), h1.trim().slice(0, 50));
    check("echo: state=partial", st === "partial", st);
    await page.close();
  }

  console.log(`${pass}/${pass + fail} passed`);
  await browser.close();
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error("RUNNER ERROR", e.message); process.exit(1); });
