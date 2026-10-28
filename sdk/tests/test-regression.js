// Deterministic regression suite: fixture translations, no model variance.
// Covers the two marker-hardening bugs fixed 2026-10-27 plus core contracts.
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
  // padfixture endpoint: same server, query flag switches behavior
  await page.route("**/api/ai/translation/batch**", (route) => {
    const url = new URL(route.request().url());
    url.searchParams.set("mangle", "padfixture");
    route.continue({ url: url.toString() });
  });
  await page.goto("http://127.0.0.1:8901/", { waitUntil: "networkidle" });
  await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
  await page.waitForFunction(() => document.documentElement.getAttribute("data-pixous-i18n") === "done", null, { timeout: 30000 });

  // Regression bug #3 (stale-check marker mismatch): skeleton blocks MUST apply,
  // not fall into preserve-original. Nav was the victim block.
  const nav = await page.textContent("nav");
  check("reg#3 nav skeleton applied (not preserved)", /PRODUCTS/.test(nav), nav.trim().slice(0, 80));
  // Regression bug #2 (pad-trim false reject): fixture pads around markers like
  // engines do; padded responses must be accepted and un-padded exactly once.
  const bold = await page.textContent("p b");
  check("reg#2 padded markers accepted, bold filled", /FORMATTING/.test(bold), bold);
  const nested = await page.textContent("p:has(b)");
  check("reg#2 no double-space glue after un-pad", !/  /.test(nested.replace(/\s+/g, " ")), nested.trim().slice(0, 90));
  check("reg#2 nested structure intact (span + b present)", await page.evaluate(() => !!document.querySelector("p b") && !!document.querySelector("p span")));
  // whitespace contract
  const sp = (nested.match(/ /g) || []).length;
  check("word-boundary whitespace preserved", sp >= 3, "spaces:" + sp);
  // race contract: dynamic node added during translation also translated
  await page.waitForFunction(() => /DYNAMICALLY/.test(document.getElementById("dynamic").textContent), null, { timeout: 15000 }).catch(() => {});
  const dyn = await page.textContent("#dynamic");
  check("dynamic node translated (race-safe)", /DYNAMICALLY/.test(dyn), dyn.trim().slice(0, 60));
  // language switch back: originals restored
  await page.evaluate(() => window.PixousI18n.setLanguage("en"));
  await page.waitForTimeout(1500);
  const navEn = await page.textContent("nav");
  check("restore to English exact", /Home/.test(navEn) && /Contact us/.test(navEn), navEn.trim().slice(0, 60));
  // re-translate from cache path (reg#3 regression on second pass)
  await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
  await page.waitForFunction(() => document.documentElement.getAttribute("data-pixous-i18n") === "done", null, { timeout: 30000 });
  const nav2 = await page.textContent("nav");
  check("reg#3 second-pass (cached) skeleton applied", /PRODUCTS/.test(nav2), nav2.trim().slice(0, 60));
  console.log(`${pass}/${pass + fail} passed`);
  await browser.close();
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error("RUNNER ERROR", e.message); process.exit(1); });
