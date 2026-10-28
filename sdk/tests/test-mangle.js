const { chromium } = require("playwright-core");
(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME_EXE, args: ["--no-sandbox", "--disable-dev-shm-usage"] });
  let fail = 0;
  for (const mode of ["drop", "reorder", "malformed"]) {
    const page = await browser.newPage();
    await page.goto("http://127.0.0.1:8901/?mangle=" + mode, { waitUntil: "networkidle" });
    const origNested = await page.textContent("p:has(b)");
    const origBold = await page.textContent("p b");
    await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
    await page.waitForFunction(() => {
      const s = document.documentElement.getAttribute("data-pixous-i18n");
      return s === "partial" || s === "done" || s === "error";
    }, null, { timeout: 30000 });
    const state = await page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n"));
    const nestedNow = await page.textContent("p:has(b)");
    const boldNow = await page.textContent("p b");
    const ok = state === "partial" && nestedNow === origNested && boldNow === origBold;
    console.log((ok ? "PASS" : "FAIL") + `  mangle=${mode}: original block preserved + partial status (state=${state})`);
    if (!ok) { fail++; console.log("   nested now:", JSON.stringify(nestedNow)); }
    await page.close();
  }
  await browser.close();
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error("RUNNER ERROR", e); process.exit(2); });
