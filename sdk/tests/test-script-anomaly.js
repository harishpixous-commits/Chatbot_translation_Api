// Server-reported script anomaly: original kept for that item + state=partial.
// Old-server compatibility: no field -> unchanged behavior (state done). Deterministic (padfixture, no model).
const { chromium } = require("playwright-core");
(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME_EXE, args: ["--no-sandbox", "--disable-dev-shm-usage"] });
  let fail = 0;
  const chk = (n, ok, d) => { console.log((ok ? "PASS" : "FAIL") + "  " + n + (d ? "  | " + d : "")); if (!ok) fail++; };
  for (const mode of ["flag-first", "no-field"]) {
    const page = await browser.newPage();
    let firstOriginals = null;
    await page.route(/\/api\/ai\/translation\/batch/, async (route) => {
      const resp = await route.fetch();
      const j = await resp.json();
      const d = j.data || j;
      if (mode === "flag-first") d.scriptAnomalyIndexes = [0];
      await route.fulfill({ response: resp, json: j });
    });
    await page.goto("http://127.0.0.1:8901/?mangle=padfixture", { waitUntil: "networkidle" });
    const before = await page.textContent("h1");
    await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
    await page.waitForFunction(() => ["partial","done","error"].includes(document.documentElement.getAttribute("data-pixous-i18n")), null, { timeout: 30000 });
    const state = await page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n"));
    const html = await page.content();
    if (mode === "flag-first") {
      chk("flagged item: state partial", state === "partial", state);
      // at least one block must remain in original English
      chk("flagged item: original English kept somewhere", /Your order has been shipped|Welcome to our website|Home/.test(await page.evaluate(() => document.body.innerText)));
    } else {
      chk("old server (no field): state done, behavior unchanged", state === "done", state);
    }
    await page.close();
  }
  await browser.close();
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error("RUNNER ERROR", e); process.exit(2); });
