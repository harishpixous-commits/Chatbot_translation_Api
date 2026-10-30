// Stranded-block semantics (deterministic, padfixture, no model). Documents, not changes, the contract:
// a block flagged by the server on 2 separate passes (failedBlockMaxAttempts=2) is "stranded": never re-sent
// in this session, original kept, and EVERY later pass for that language ends "partial" even when the server
// response of that later pass carries no flag (scriptAnomalyIndexes empty). clearCache() re-enables it.
const { chromium } = require("playwright-core");
(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME_EXE, args: ["--no-sandbox", "--disable-dev-shm-usage"] });
  let fail = 0;
  const chk = (n, ok, d) => { console.log((ok ? "PASS" : "FAIL") + "  " + n + (d ? "  | " + d : "")); if (!ok) fail++; };
  const page = await browser.newPage();
  let flagForward = true; const sentForward = []; const flaggedTexts = new Set(); let anomalyReturned = [];
  await page.route(/\/api\/ai\/translation\/batch/, async (route) => {
    const req = JSON.parse(route.request().postData());
    const resp = await route.fetch(); const j = await resp.json(); const d = j.data || j;
    if (req.targetLanguage === "ta") {
      sentForward.push(req.texts.slice());
      if (flagForward) { d.scriptAnomalyIndexes = [0]; flaggedTexts.add(req.texts[0]); } else delete d.scriptAnomalyIndexes;
      anomalyReturned.push(d.scriptAnomalyIndexes || []);
    }
    await route.fulfill({ response: resp, json: j });
  });
  const state = () => page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n"));
  const sw = async (l) => { await page.evaluate((x) => window.PixousI18n.setLanguage(x), l); if (l === "en") { await page.waitForTimeout(500); return; } await page.waitForFunction(() => ["partial", "done", "error"].includes(document.documentElement.getAttribute("data-pixous-i18n")), null, { timeout: 30000 }); await page.waitForTimeout(300); };
  await page.goto("http://127.0.0.1:8901/?mangle=padfixture", { waitUntil: "networkidle" });
  await sw("ta"); chk("pass 1 (flagged): partial", (await state()) === "partial", await state());
  await sw("en"); await sw("ta"); chk("pass 2 (flagged again): partial", (await state()) === "partial", await state());
  const att = await page.evaluate(() => window.PixousI18n._debugState().failedAttempts);
  chk("flagged texts reached 2 attempts", att.some(([k, n]) => n >= 2), JSON.stringify(att.map(([k, n]) => [k.slice(0, 40), n])));
  flagForward = false; const sentBefore = sentForward.length; anomalyReturned = [];
  await sw("en"); await sw("ta");
  const resent = sentForward.slice(sentBefore).flat();
  chk("pass 3 (server no longer flags): stranded text NOT re-sent", ![...flaggedTexts].some((t) => resent.includes(t)), "resent=" + resent.length);
  chk("pass 3: response had NO anomaly indexes, yet state is partial (stranded)", anomalyReturned.every((a) => a.length === 0) && (await state()) === "partial", JSON.stringify(anomalyReturned) + " " + await state());
  await page.evaluate(() => window.PixousI18n.clearCache());
  await sw("en"); await sw("ta");
  chk("after clearCache with clean server: done", (await state()) === "done", await state());
  await browser.close();
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error("RUNNER ERROR", e); process.exit(2); });
