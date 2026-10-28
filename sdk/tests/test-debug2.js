const { chromium } = require("playwright-core");
(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME_EXE, args: ["--no-sandbox", "--disable-dev-shm-usage"] });
  const page = await browser.newPage();
  page.on("pageerror", (e) => console.log("[pageerror]", e.message.slice(0, 200)));
  await page.goto("http://127.0.0.1:8901/", { waitUntil: "networkidle" });
  await page.evaluate(() => {
    window.__errs = [];
    window.PixousI18n.init && 0;
  });
  await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
  await page.waitForTimeout(25000);
  console.log("state:", await page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n")));
  console.log("nav:", await page.textContent("nav"));
  console.log("nested:", await page.textContent("p:has(b)"));
  const payloads = [];
  await browser.close();
})().catch((e) => { console.error("ERR", e); process.exit(2); });
