// Interpolation-token protection + glossary override contracts.
const { chromium } = require("playwright-core");
const EXE = process.env.CHROME_EXE;
let pass = 0, fail = 0;
function check(name, ok, detail) {
  console.log((ok ? "PASS" : "FAIL") + "  " + name + (detail ? "  | " + String(detail).slice(0, 110) : ""));
  ok ? pass++ : fail++;
}
async function newPage(browser, mode, track) {
  const page = await browser.newPage();
  if (track) page.on("request", (r) => { if (r.url().includes("/translation/batch")) track.push(r.postData() || ""); });
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

  // 1. token corruption (observed te/kn {{date}} defect) -> fail closed: originals + partial
  {
    const page = await newPage(browser, "tokencorrupt");
    await initWith(page, {});
    await toLang(page, "ta");
    const t1 = await page.evaluate(() => document.getElementById("tok1").textContent);
    const t2 = await page.evaluate(() => document.getElementById("tok2").textContent);
    const t3 = await page.evaluate(() => document.getElementById("tok3").textContent);
    const plain = await page.evaluate(() => document.getElementById("plain").textContent);
    check("corrupt: tok1 original preserved ({{date}} intact)", t1 === "Your order has been shipped and will arrive on {{date}}.", t1.slice(0, 60));
    check("corrupt: tok2 original preserved ({name}/%d/${amount} intact)", t2.includes("{name}") && t2.includes("%d") && t2.includes("${amount}") && t2.includes("Hello"), t2.slice(0, 60));
    check("corrupt: tok3 skeleton original preserved", t3.includes("{{count}}") && t3.includes("Nested"), t3.slice(0, 60));
    check("corrupt: no mutated token rendered anywhere", ![t1, t2, t3].some((x) => x.includes("{{உரை}}")), "");
    check("corrupt: untouched plain block still translated", plain.includes("மொழிபெயர்ப்பு"), plain.slice(0, 40));
    check("corrupt: state=partial", (await state(page)) === "partial", await state(page));
    await page.close();
  }

  // 2. tokens kept verbatim by engine -> applied, tokens restored byte-identical
  {
    const page = await newPage(browser, "tokenok");
    await initWith(page, {});
    await toLang(page, "ta");
    const t1 = await page.evaluate(() => document.getElementById("tok1").textContent);
    const t2 = await page.evaluate(() => document.getElementById("tok2").textContent);
    const t3 = await page.evaluate(() => document.getElementById("tok3").textContent);
    check("ok: {{date}} restored verbatim in translated text", t1.includes("{{date}}") && !t1.includes("arrive"), t1.slice(0, 80));
    check("ok: {name} %d ${amount} restored verbatim", t2.includes("{name}") && t2.includes("%d") && t2.includes("${amount}"), t2.slice(0, 80));
    check("ok: {{count}} restored inside skeleton", t3.includes("{{count}}") && !t3.includes("inline"), t3.slice(0, 80));
    const t4 = await page.evaluate(() => document.getElementById("tok4").textContent);
    check("ok: positional %s/%d/%d kept in source order", t4.indexOf("%s") < t4.indexOf("%d") && t4.indexOf("%d") < t4.lastIndexOf("%d"), t4.slice(0, 70));
    check("ok: state=done", (await state(page)) === "done", await state(page));
    await page.close();
  }

  // 3. adversarial literal collision: page text contains the factory's FIRST prefix
  {
    const page = await newPage(browser, "tokenok");
    await initWith(page, {});
    // tokenMarkFactory is a function: set it via an in-page re-init instead
    await page.evaluate(() => {
      window.PixousI18n.destroy();
      const seq = ["<xcollk", "<xok99k"]; let i = 0;
      window.PixousI18n.init({ sourceLanguage: "en", endpoint: "/api/ai/translation/batch?mangle=tokenok", tokenMarkFactory: () => seq[i++ % seq.length] });
    });
    await toLang(page, "ta");
    const adv = await page.evaluate(() => document.getElementById("adv").textContent);
    check("adversarial: literal <xcollk0> survived verbatim", adv.includes("<xcollk0>"), adv.slice(0, 70));
    check("adversarial: text translated around the literal", adv.includes("மொழிபெயர்ப்பு"), adv.slice(0, 70));
    check("adversarial: state=done (collision regenerated)", (await state(page)) === "done", await state(page));
    await page.close();
  }

  // 4. glossary override: user-supplied value used, text never sent to endpoint
  {
    const bodies = [];
    const page = await newPage(browser, "tokenok", bodies);
    await initWith(page, { overrides: { "Sign out": { ta: "FIXTURE-USER-சைன்-அவுட்" } } });
    await toLang(page, "ta");
    const so = await page.evaluate(() => document.getElementById("so").textContent);
    check("override: user-supplied value applied", so === "FIXTURE-USER-சைன்-அவுட்", so);
    check("override: 'Sign out' never sent to endpoint", !bodies.some((b) => b.includes("Sign out")), bodies.length + " reqs");
    await page.close();
  }

  // 5. override exempt from echo guard (owner keeps Latin deliberately)
  {
    const page = await newPage(browser, "tokenok");
    await initWith(page, { overrides: { "Sign out": { ta: "Sign out" } } });
    await toLang(page, "ta");
    const so = await page.evaluate(() => document.getElementById("so").textContent);
    check("override echo-skip: Latin override applied, not rejected", so === "Sign out", so);
    check("override echo-skip: state=done", (await state(page)) === "done", await state(page));
    await page.close();
  }

  // 6. reorder semantics: named tokens may reorder, positional printf tokens fail closed
  {
    const page = await newPage(browser, "tokenreorder");
    await initWith(page, {});
    await toLang(page, "ta");
    const t1 = await page.evaluate(() => document.getElementById("tok1").textContent);
    const t2 = await page.evaluate(() => document.getElementById("tok2").textContent);
    const t4 = await page.evaluate(() => document.getElementById("tok4").textContent);
    check("reorder: named {{date}} reordered and applied", t1.includes("{{date}}") && !t1.includes("arrive"), t1.slice(0, 70));
    check("reorder: single positional + named reordered and applied", t2.includes("{name}") && t2.includes("%d") && t2.includes("${amount}") && !t2.includes("Hello"), t2.slice(0, 70));
    check("reorder: multi-positional reorder REJECTED, original preserved", t4 === "%s has %d unread of %d total.", t4.slice(0, 70));
    check("reorder: state=partial (fail closed on positional)", (await state(page)) === "partial", await state(page));
    await page.close();
  }

  // 7. REAL ENGINE regression: the observed te/kn {{date}} mutation can no longer render
  for (const lang of ["te", "kn"]) {
    const page = await newPage(browser, null);
    await initWith(page, {});
    await toLang(page, lang);
    const t1 = await page.evaluate(() => document.getElementById("tok1").textContent);
    const corrupted = /\{\{[^\x00-\x7F]/.test(t1);
    const ok = t1.includes("{{date}}") || t1 === "Your order has been shipped and will arrive on {{date}}.";
    check("real " + lang + ": {{date}} verbatim OR original preserved", ok && !corrupted, t1.slice(0, 80));
    await page.close();
  }

  await browser.close();
  console.log(pass + "/" + (pass + fail) + " passed");
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error("RUNNER ERROR", e); process.exit(2); });
