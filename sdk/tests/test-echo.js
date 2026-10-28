// Echo-heuristic exemption contracts: brand/URL/allowlist handling.
const { chromium } = require("playwright-core");
const EXE = process.env.CHROME_EXE;
let pass = 0, fail = 0;
function check(name, ok, detail) {
  console.log((ok ? "PASS" : "FAIL") + "  " + name + (detail ? "  | " + String(detail).slice(0, 110) : ""));
  ok ? pass++ : fail++;
}
async function newPage(browser, mode) {
  const page = await browser.newPage();
  await page.route("**/api/ai/translation/batch**", (route) => {
    const url = new URL(route.request().url());
    url.searchParams.set("mangle", mode);
    route.continue({ url: url.toString() });
  });
  await page.goto("http://127.0.0.1:8901/", { waitUntil: "networkidle" });
  return page;
}
async function initWith(page, opts) {
  await page.evaluate((o) => {
    window.PixousI18n.destroy();
    const s = document.createElement("script"); s.src = "/pixous-i18n.js";
    s.onload = () => { window.PixousI18n.init(Object.assign({ sourceLanguage: "en", endpoint: "/api/ai/translation/batch" }, o)); window.__ready = true; };
    document.head.appendChild(s);
  }, opts);
  await page.waitForFunction(() => window.__ready, null, { timeout: 10000 });
  await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
  await page.waitForFunction(() => ["done", "partial"].includes(document.documentElement.getAttribute("data-pixous-i18n")), null, { timeout: 15000 });
}
(async () => {
  const browser = await chromium.launch({ executablePath: EXE, args: ["--no-sandbox", "--disable-dev-shm-usage"] });

  // 1. brand+URL retained, NO allowlist -> heuristic rejects (documented strictness), originals preserved
  {
    const page = await newPage(browser, "echobrand");
    await initWith(page, {});
    const h1 = await page.evaluate(() => document.querySelector("h1").textContent);
    const st = await page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n"));
    check("no-allowlist: English-heavy brand translation REJECTED (fail-closed)", /Welcome to our website/.test(h1), h1.trim().slice(0, 60));
    check("no-allowlist: state=partial", st === "partial", st);
    await page.close();
  }

  // 2. same translation WITH echoAllowlist ["Pixous"] (URL auto-exempt) -> applied
  {
    const page = await newPage(browser, "echobrand");
    await initWith(page, { echoAllowlist: ["Pixous","GitHub","Docker","Kubernetes"] });
    const h1 = await page.evaluate(() => document.querySelector("h1").textContent);
    const st = await page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n"));
    check("allowlist: brand translation APPLIED", /Pixous/.test(h1) && /https:\/\/pixous\.example\/app/.test(h1), h1.trim().slice(0, 80));
    check("allowlist: state=done", st === "done", st);
    await page.close();
  }

  // 3. pure source echo still rejected even with allowlist set
  {
    const page = await newPage(browser, "echo");
    await initWith(page, { echoAllowlist: ["Pixous","GitHub","Docker","Kubernetes"] });
    const h1 = await page.evaluate(() => document.querySelector("h1").textContent);
    const st = await page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n"));
    check("echo: raw source retention still REJECTED", /Welcome to our website/.test(h1), h1.trim().slice(0, 60));
    check("echo: state=partial", st === "partial", st);
    await page.close();
  }

  await browser.close();
  console.log(pass + "/" + (pass + fail) + " passed");
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(2); });
