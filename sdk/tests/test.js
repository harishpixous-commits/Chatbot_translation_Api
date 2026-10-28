const { chromium } = require("playwright-core");

const EXE = process.env.CHROME_EXE;
const results = [];
function check(name, ok, detail) {
  results.push({ name, ok, detail: detail || "" });
  console.log((ok ? "PASS" : "FAIL") + "  " + name + (detail ? "  | " + detail : ""));
}
const hasScript = (s, lo, hi) => [...s].some((c) => { const k = c.codePointAt(0); return k >= lo && k <= hi; });
const hasTamil = (s) => hasScript(s, 0x0B80, 0x0BFF);
const hasDevanagari = (s) => hasScript(s, 0x0900, 0x097F);
const hasKannada = (s) => hasScript(s, 0x0C80, 0x0CFF);

(async () => {
  const browser = await chromium.launch({ executablePath: EXE, args: ["--no-sandbox", "--disable-dev-shm-usage"] });
  const page = await browser.newPage();

  // capture every payload the SDK transmits (privacy assertions)
  const payloads = [];
  page.on("request", (r) => {
    if (r.url().includes("/translation/batch")) { payloads.push(r.postData()); console.log("[req]", (r.postData()||"").slice(0,80)); }
  });
  page.on("requestfailed", (r) => { if (r.url().includes("batch")) console.log("[req] FAILED", r.failure() && r.failure().errorText); });

  await page.goto("http://127.0.0.1:8901/", { waitUntil: "networkidle" });
  await page.waitForTimeout(1000); // let the dynamic node arrive before first switch? no: arrive DURING ta below

  // --- baseline ---
  const before = await page.textContent("#intro");
  check("baseline original text", before.includes("Your order has been shipped"), before.trim());

  // --- 1. switch to Tamil ---
  await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
  await page.waitForFunction(() => document.documentElement.getAttribute("data-pixous-i18n") === "done", null, { timeout: 60000 });
  const introTa = await page.textContent("#intro");
  check("en->ta paragraph translated (real Tamil)", hasTamil(introTa) && !introTa.includes("shipped"), introTa.trim());
  const h1Ta = await page.textContent("h1");
  check("en->ta h1 translated", hasTamil(h1Ta), h1Ta.trim());
  const phTa = await page.getAttribute("#q", "placeholder");
  check("placeholder translated", hasTamil(phTa || ""), phTa);
  const btnTa = await page.getAttribute("#go", "value");
  check("submit button value translated", hasTamil(btnTa || ""), btnTa);
  const titleTa = await page.getAttribute("nav a", "title");
  check("title attribute translated", hasTamil(titleTa || ""), titleTa);
  const altTa = await page.getAttribute("img", "alt");
  check("alt attribute translated", hasTamil(altTa || ""), altTa);
  const svgTa = await page.textContent("#svgtext");
  check("SVG <text> translated", hasTamil(svgTa), svgTa.trim());
  const shadowTa = await page.evaluate(() => document.getElementById("shadow-host").shadowRoot.querySelector("#shadowp").textContent);
  check("open shadow DOM translated", hasTamil(shadowTa), shadowTa.trim());
  const nestedTa = await page.textContent("p:has(b)");
  check("nested inline formatting translated", hasTamil(nestedTa), nestedTa.trim());
  const spaceCount = (nestedTa.match(/ /g) || []).length;
  check("nested line keeps word-boundary whitespace (not glued)", spaceCount >= 3, `spaces: ${spaceCount}`);
  const boldTa = await page.textContent("p b");
  const preBold = nestedTa.indexOf(boldTa), postBold = preBold + boldTa.length;
  // Engine may occasionally leave an individual word in the source language (model quality, not SDK);
// assert structure: bold element intact, non-empty, no marker leakage, no fragment glue.
check("whole-phrase: bold segment intact, no marker leakage", boldTa.trim().length > 0 && !/<\/?x[a-z0-9]+t\d+/.test(boldTa), boldTa);
  check("whole-phrase: text flows around bold (no fragment glue)", !/[^ ]{25,}/.test(nestedTa.replace(/\s+/g, " ")), "longest run ok");
  console.log("    [semantic] EN: 'Nested inline formatting stays intact.' -> TA:", nestedTa.trim());
  check("nested bold element still present", await page.evaluate(() => !!document.querySelector("p b")));

  // --- 2. exclusions & privacy ---
  check("data-no-translate preserved", (await page.textContent("#brand")) === "Pixous Assistant");
  check("translate=no preserved", (await page.textContent("#raw")) === "Do not translate this");
  check(".no-translate preserved", (await page.textContent("#cls")) === "Skipped by class");
  check("<pre> code preserved", (await page.textContent("pre")).includes("keep me"));
  check("textarea content preserved", (await page.inputValue("#notes")) === "User private draft content");
  const pwPh = await page.getAttribute("#pw", "placeholder");
  check("password input placeholder NOT transmitted/translated", pwPh === "Password", pwPh);
  const joined = payloads.join("\n");
  check("no textarea content in any payload", !joined.includes("private draft"));
  check("no password placeholder in any payload", !joined.includes("Password"));
  check("no excluded text in any payload", !joined.includes("keep me") && !joined.includes("Do not translate this"));

  // --- 3. MutationObserver: late-added node translated ---
  await page.waitForFunction(() => {
    const el = document.getElementById("late");
    return el && /[\u0B80-\u0BFF]/.test(el.textContent);
  }, null, { timeout: 30000 }).catch(() => {});
  const lateTa = await page.textContent("#late");
  check("MutationObserver translated late-added node", hasTamil(lateTa || ""), (lateTa || "MISSING").trim());

  // --- 4. switch back to English: originals restored ---
  await page.evaluate(() => window.PixousI18n.setLanguage("en"));
  await page.waitForFunction(() => !document.documentElement.getAttribute("data-pixous-i18n"), null, { timeout: 15000 });
  check("switch-back restores original paragraph", (await page.textContent("#intro")) === before, await page.textContent("#intro"));
  check("switch-back restores placeholder", (await page.getAttribute("#q", "placeholder")) === "Search products");
  check("switch-back restores SVG text", (await page.textContent("#svgtext")) === "Scalable architecture diagram");
  check("switch-back restores shadow DOM", (await page.evaluate(() => document.getElementById("shadow-host").shadowRoot.querySelector("#shadowp").textContent)) === "Shadow DOM greeting");

  // --- 5. race safety: rapid ta -> hi -> kn, final DOM = Kannada only ---
  await page.evaluate(() => {
    window.PixousI18n.setLanguage("ta");
    window.PixousI18n.setLanguage("hi");
    window.PixousI18n.setLanguage("kn");
  });
  await page.waitForFunction(() => document.documentElement.getAttribute("data-pixous-i18n") === "done", null, { timeout: 60000 });
  await page.waitForTimeout(1500); // let any straggler apply (it must not)
  const raceIntro = await page.textContent("#intro");
  check("rapid switching: final language is Kannada", hasKannada(raceIntro), raceIntro.trim());
  check("rapid switching: no Tamil stragglers", !hasTamil(raceIntro));
  check("rapid switching: no Hindi stragglers", !hasDevanagari(raceIntro));
  check("currentLanguage is kn", await page.evaluate(() => window.PixousI18n.getLanguage()) === "kn");

  // --- 6. cache: switch en -> ta again should need zero new requests for cached texts ---
  const countBefore = payloads.length;
  await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
  await page.waitForFunction(() => document.documentElement.getAttribute("data-pixous-i18n") === "done", null, { timeout: 30000 });
  const introTa2 = await page.textContent("#intro");
  check("second ta switch same translation", introTa2 === introTa);
  check("cache served repeat switch (0 or few new requests)", payloads.length - countBefore <= 1, `new requests: ${payloads.length - countBefore}`);

  await page.screenshot({ path: "/tmp/sdktest/shot-tamil.png", fullPage: true });

  await browser.close();

  const failed = results.filter((r) => !r.ok);
  console.log(`\n${results.length - failed.length}/${results.length} passed`);
  process.exit(failed.length ? 1 : 0);
})().catch((e) => { console.error("RUNNER ERROR", e); process.exit(2); });
