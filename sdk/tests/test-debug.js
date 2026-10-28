const { chromium } = require("playwright-core");
(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME_EXE, args: ["--no-sandbox", "--disable-dev-shm-usage"] });
  const page = await browser.newPage();
  page.on("pageerror", (e) => console.log("[pageerror]", e.message));
  page.on("console", (m) => { if (m.type() === "error") console.log("[console]", m.text().slice(0, 300)); });
  await page.goto("http://127.0.0.1:8901/?mangle=reorder", { waitUntil: "networkidle" });
  await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
  await page.waitForTimeout(6000);
  console.log("state:", await page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n")));
  await browser.close();
})().catch((e) => { console.error("ERR", e); process.exit(2); });
