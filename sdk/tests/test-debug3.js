const { chromium } = require("playwright-core");
(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME_EXE, args: ["--no-sandbox", "--disable-dev-shm-usage"] });
  const page = await browser.newPage();
  page.on("response", async (r) => {
    if (r.url().includes("batch")) {
      const j = await r.json();
      j.data.translatedTexts.forEach((t, i) => { if (t.includes("<x")) console.log("[resp]", JSON.stringify(t).slice(0, 300)); });
    }
  });
  await page.goto("http://127.0.0.1:8901/", { waitUntil: "networkidle" });
  const reqs = [];
  page.on("request", (r) => { if (r.url().includes("batch")) { const d = JSON.parse(r.postData()); d.texts.forEach((t) => { if (t.includes("<x")) reqs.push(t); }); } });
  await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
  await page.waitForTimeout(20000);
  console.log("state:", await page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n")));
  reqs.slice(0, 3).forEach((t) => console.log("[sent]", JSON.stringify(t).slice(0, 300)));
  await browser.close();
})().catch((e) => { console.error("ERR", e); process.exit(2); });
