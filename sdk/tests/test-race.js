// Race/cancellation coverage: language switch mid-flight, overlapping
// mutations, clearCache. Deterministic fixture with controllable latency.
const { chromium } = require("playwright-core");
const EXE = process.env.CHROME_EXE;
let pass = 0, fail = 0;
function check(name, ok, detail) {
  console.log((ok ? "PASS" : "FAIL") + "  " + name + (detail ? "  | " + String(detail).slice(0, 90) : ""));
  ok ? pass++ : fail++;
}
(async () => {
  const browser = await chromium.launch({ executablePath: EXE, args: ["--no-sandbox", "--disable-dev-shm-usage"] });
  const page = await browser.newPage();
  let release = null, inFlight = 0;
  await page.route("**/api/ai/translation/batch**", async (route) => {
    const url = new URL(route.request().url());
    url.searchParams.set("mangle", "padfixture");
    // hold responses until released, to create a deterministic mid-flight window
    inFlight++;
    await new Promise((res) => { (release = release ? (() => { const p = release; release = res; p(); }) : res); setTimeout(res, 150); });
    route.continue({ url: url.toString() });
  });
  await page.goto("http://127.0.0.1:8901/", { waitUntil: "networkidle" });

  // 1. switch ta -> en mid-flight: en restore must win, no ta text leaks
  await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
  await page.waitForTimeout(80); // first fetch now in flight
  await page.evaluate(() => window.PixousI18n.setLanguage("en"));
  await page.waitForTimeout(4000);
  const nav1 = await page.textContent("nav");
  const st1 = await page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n"));
  check("switch-to-en mid-flight: originals restored, state cleared", /Home/.test(nav1) && !/PRODUCTS/.test(nav1) && !st1, `state=${st1} nav=${nav1.trim().slice(0,40)}`);

  // 2. switch ta -> hi -> ta rapidly: final language must win coherently
  await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
  await page.waitForTimeout(60);
  await page.evaluate(() => window.PixousI18n.setLanguage("hi"));
  await page.waitForTimeout(60);
  await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
  await page.waitForFunction(() => document.documentElement.getAttribute("data-pixous-i18n") === "done", null, { timeout: 20000 });
  await page.waitForTimeout(2000);
  const nav2 = await page.textContent("nav");
  const st2 = await page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n"));
  check("rapid ta->hi->ta: final language applied coherently", /PRODUCTS/.test(nav2) && st2 === "done", `state=${st2} nav=${nav2.trim().slice(0,40)}`);

  // 3. overlapping mutations during an active pass all get translated
  await page.evaluate(() => {
    const d = document.getElementById("dynamic");
    const p1 = document.createElement("p"); p1.id = "lateA"; p1.textContent = "First late arrival"; d.appendChild(p1);
    setTimeout(() => { const p2 = document.createElement("p"); p2.id = "lateB"; p2.textContent = "Second late arrival"; d.appendChild(p2); }, 120);
  });
  await page.waitForFunction(() => /FIRST LATE/.test(document.getElementById("dynamic").textContent) && /SECOND LATE/.test(document.getElementById("dynamic").textContent), null, { timeout: 20000 }).catch(() => {});
  const dynAll = await page.textContent("#dynamic");
  check("overlapping mutations both translated", /FIRST[\s\S]*LATE[\s\S]*ARRIVAL/.test(dynAll) && /SECOND[\s\S]*LATE[\s\S]*ARRIVAL/.test(dynAll), dynAll.trim().slice(0, 80));

  // 4. clearCache runs without error and page still functions
  const cc = await page.evaluate(() => { try { window.PixousI18n.clearCache(); return "ok"; } catch (e) { return "err:" + e.message; } });
  check("clearCache() callable, no throw", cc === "ok", cc);

  console.log(`${pass}/${pass + fail} passed`);
  await browser.close();
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error("RUNNER ERROR", e.message); process.exit(1); });
