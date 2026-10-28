const { chromium } = require("playwright-core");
(async () => {
  const lang = process.argv[2];
  const browser = await chromium.launch({ executablePath: process.env.CHROME_EXE, args: ["--no-sandbox","--disable-dev-shm-usage"] });
  const page = await browser.newPage({ viewport: { width: 1100, height: 900 } });
  await page.goto("http://127.0.0.1:8901/", { waitUntil: "networkidle" });
  await page.evaluate((l) => window.PixousI18n.setLanguage(l), lang);
  await page.waitForFunction(() => ["done","partial"].includes(document.documentElement.getAttribute("data-pixous-i18n")), null, { timeout: 90000 });
  await page.waitForTimeout(1200);
  await page.screenshot({ path: "/tmp/sdktest/shot-" + lang + ".png", fullPage: true });
  const st = await page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n"));
  const checks = await page.evaluate(() => ({
    brand: document.getElementById("brand").textContent,
    raw: document.getElementById("raw").textContent,
    cls: document.getElementById("cls").textContent,
    pre: document.querySelector("pre").textContent,
    notes: document.getElementById("notes").value,
    pw: document.getElementById("pw").getAttribute("placeholder"),
    nav: document.querySelector("nav").textContent.trim().slice(0, 60),
    h1: document.querySelector("h1").textContent.slice(0, 60)
  }));
  console.log(lang, "state:", st, JSON.stringify(checks, null, 1));
  await browser.close();
})().catch(e => { console.error("ERR", e.message); process.exit(1); });
