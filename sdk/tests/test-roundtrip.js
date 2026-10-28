// Round-trip polarity guard contracts: opposite-meaning flip fails closed.
const { chromium } = require("playwright-core");
const EXE = process.env.CHROME_EXE;
let pass = 0, fail = 0;
function check(name, ok, detail) {
  console.log((ok ? "PASS" : "FAIL") + "  " + name + (detail ? "  | " + String(detail).slice(0, 110) : ""));
  ok ? pass++ : fail++;
}
async function newPage(browser, mode, bodies) {
  const page = await browser.newPage();
  if (bodies) page.on("request", (r) => { if (r.url().includes("/translation/batch")) bodies.push(r.postData() || ""); });
  await page.route("**/api/ai/translation/batch**", (route) => {
    const url = new URL(route.request().url());
    if (mode) url.searchParams.set("mangle", mode);
    route.continue({ url: url.toString() });
  });
  await page.goto("http://127.0.0.1:8901/demo-tokens.html", { waitUntil: "networkidle" });
  return page;
}
async function initWith(page, opts) {
  await page.evaluate((o) => {
    const s = document.createElement("script"); s.src = "/pixous-i18n.js";
    s.onload = () => { window.PixousI18n.init(Object.assign({ sourceLanguage: "en", endpoint: "/api/ai/translation/batch" }, o)); window.__ready = true; };
    document.head.appendChild(s);
  }, opts);
  await page.waitForFunction(() => window.__ready, null, { timeout: 10000 });
}
async function toLang(page, lang) {
  await page.evaluate((l) => window.PixousI18n.setLanguage(l), lang);
  await page.waitForFunction(() => ["done", "partial", "error"].includes(document.documentElement.getAttribute("data-pixous-i18n")), null, { timeout: 30000 });
}
const state = (page) => page.evaluate(() => document.documentElement.getAttribute("data-pixous-i18n"));

(async () => {
  const browser = await chromium.launch({ executablePath: EXE, args: ["--no-sandbox", "--disable-dev-shm-usage"] });

  // 1b. directional ambiguity (real kn #3 mechanism): back-translation keeps the
  // stem but loses the direction word -> preserved original + partial.
  // Positive control: "Log out" back-translates as "Logout" -> applied.
  {
    const page = await newPage(browser, "roundtrip-ambiguous");
    await initWith(page, {});
    await toLang(page, "kn");
    const so = await page.evaluate(() => document.getElementById("so").textContent);
    const lo = await page.evaluate(() => document.getElementById("lo").textContent);
    check("ambiguous: 'Sign out' preserved in English (direction lost)", so === "Sign out", so);
    check("ambiguous: wrong kn label never rendered", !(await page.content()).includes("\u0CB8\u0CB9\u0CBF"), "");
    check("ambiguous: good paraphrase 'Log out'->kn applied", /[\u0c80-\u0cff]/.test(lo), lo);
    const su = await page.evaluate(() => document.getElementById("su").textContent);
    check("ambiguous: 'Sign up' preserved in English (direction changed to 'Sign in')", su === "Sign up", su);
    check("ambiguous: register-vs-login flip never rendered", !(await page.content()).includes("\u0CB8\u0CBF\u0C97\u0CCD\u0CA8\u0CCD"), "");
    check("ambiguous: state=partial", (await state(page)) === "partial", await state(page));
    await page.close();
  }

  // 1. fixture: opposite-meaning flip on "Sign out" -> preserved original + partial; good labels applied
  {
    const bodies = [];
    const page = await newPage(browser, "roundtrip", bodies);
    await initWith(page, {});
    await toLang(page, "ta");
    const so = await page.evaluate(() => document.getElementById("so").textContent);
    const plain = await page.evaluate(() => document.getElementById("plain").textContent);
    check("flip: 'Sign out' preserved in English", so === "Sign out", so);
    check("flip: 'Sign In' never rendered", !(await page.content()).includes("Sign In"), "");
    check("flip: good blocks still applied", plain.includes("மொழிபெயர்ப்பு"), plain.slice(0, 40));
    check("flip: state=partial", (await state(page)) === "partial", await state(page));
    check("flip: exactly one back-translate call", bodies.filter((b) => b.includes('"sourceLanguage":"ta"') && b.includes('"targetLanguage":"en"')).length === 1,
          bodies.length + " total reqs");
    // 2. rejected string is never re-sent on a later pass
    await page.evaluate(() => window.PixousI18n.setLanguage("en"));
    await page.waitForTimeout(300);
    const before = bodies.length;
    await toLang(page, "ta");
    const resent = bodies.slice(before).filter((b) => b.includes("Sign out")).length;
    check("flip: rejected string never re-sent on later passes", resent === 0, resent + " resends");
    check("flip: state stays partial on later pass", (await state(page)) === "partial", await state(page));
    await page.close();
  }

  // 2a. token-bearing polarity label: "Deny {{name}} access" flips to "Allow access" -> preserved
  {
    const page = await newPage(browser, "roundtrip");
    await page.evaluate(() => {
      const p = document.createElement("p"); p.id = "deny"; p.textContent = "Deny {{name}} access";
      document.body.appendChild(p);
    });
    await initWith(page, {});
    await toLang(page, "ta");
    const deny = await page.evaluate(() => document.getElementById("deny").textContent);
    check("token-flip: 'Deny {{name}} access' preserved in English", deny === "Deny {{name}} access", deny);
    check("token-flip: 'Allow access' never rendered", !(await page.content()).includes("Allow access"), "");
    await page.close();
  }

  // 2b. guard error fails CLOSED: back-translation 500 -> short labels stay original + partial
  {
    const page = await newPage(browser, "roundtrip-failback");
    await initWith(page, { roundTripMaxChars: 10 }); // only "Sign out" (8 chars) is guarded
    await toLang(page, "ta");
    const so = await page.evaluate(() => document.getElementById("so").textContent);
    const plain = await page.evaluate(() => document.getElementById("plain").textContent);
    const st = await state(page);
    check("guard-error: short label NOT served unchecked", so === "Sign out", so);
    check("guard-error: long unchecked block still applied", plain.includes("\u0bae\u0bca\u0bb4\u0bbf\u0baa\u0bc6\u0baf\u0bb0\u0bcd\u0baa\u0bcd\u0baa\u0bc1"), plain.slice(0, 40));
    check("guard-error: state=partial", st === "partial", st);
    await page.close();
  }

  // 3. roundTripCheck:false -> flip NOT caught (documented opt-out)
  {
    const page = await newPage(browser, "roundtrip");
    await initWith(page, { roundTripCheck: false });
    await toLang(page, "ta");
    const so = await page.evaluate(() => document.getElementById("so").textContent);
    check("opt-out: guard disabled applies engine output", so === "உள்நுழையுங்கள்", so);
    await page.close();
  }

  // 4. REAL ENGINE: the actual ta defect is intercepted live
  {
    const page = await newPage(browser, null);
    await initWith(page, {});
    await toLang(page, "ta");
    const so = await page.evaluate(() => document.getElementById("so").textContent);
    const st = await state(page);
    check("real ta: 'Sign out' NOT rendered as opposite meaning", so === "Sign out", so + " | " + st);
    check("real ta: state=partial (fail-closed, rest applied)", st === "partial", st);
    const t1 = await page.evaluate(() => document.getElementById("tok1").textContent);
    check("real ta: long sentence still translated (check is short-blocks only)", !t1.includes("shipped"), t1.slice(0, 50));
    await page.close();
  }

  await browser.close();
  console.log(pass + "/" + (pass + fail) + " passed");
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error("RUNNER ERROR", e); process.exit(2); });
