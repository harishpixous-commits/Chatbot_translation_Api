const { chromium } = require("playwright-core");
(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME_EXE, args: ["--no-sandbox","--disable-dev-shm-usage"] });
  const page = await browser.newPage({ viewport: { width: 1100, height: 900 } });
  await page.goto("http://127.0.0.1:8901/", { waitUntil: "networkidle" });
  await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
  await page.waitForFunction(() => ["done","partial"].includes(document.documentElement.getAttribute("data-pixous-i18n")), null, { timeout: 60000 });
  await page.waitForTimeout(1000);
  await page.screenshot({ path: "/tmp/sdktest/shot-tamil-final.png", fullPage: true });
  console.log("state:", await page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n")));
  await browser.close();
})().catch(e => { console.error("ERR", e.message); process.exit(1); });
