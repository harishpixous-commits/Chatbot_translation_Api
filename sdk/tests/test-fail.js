const { chromium } = require("playwright-core");
(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME_EXE, args: ["--no-sandbox", "--disable-dev-shm-usage"] });
  const page = await browser.newPage();
  await page.goto("http://127.0.0.1:8902/", { waitUntil: "networkidle" });
  const before = await page.textContent("#intro");
  await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
  await page.waitForFunction(() => document.documentElement.getAttribute("data-pixous-i18n") === "error", null, { timeout: 30000 });
  const after = await page.textContent("#intro");
  const ok = after === before;
  console.log((ok ? "PASS" : "FAIL") + "  endpoint failure: originals preserved + error state");
  console.log("state:", await page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n")));
  await browser.close();
  process.exit(ok ? 0 : 1);
})().catch((e) => { console.error("RUNNER ERROR", e); process.exit(2); });
