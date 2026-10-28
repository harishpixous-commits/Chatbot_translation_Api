// Hostile engine output must land as INERT text: the SDK writes translations
// only via node.nodeValue and setAttribute on title/alt/aria-label/placeholder/
// input-value, so markup in a translation can never create elements or run.
// This test pins that property end-to-end with an adversarial fixture.
const { chromium } = require("playwright-core");
(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME_EXE, args: ["--no-sandbox", "--disable-dev-shm-usage"] });
  const page = await browser.newPage();
  let fail = 0;
  const check = (name, ok, detail) => { console.log((ok ? "PASS" : "FAIL") + "  " + name + (detail !== undefined ? "  | " + detail : "")); if (!ok) fail++; };

  await page.goto("http://127.0.0.1:8901/demo-xss.html?mangle=xss", { waitUntil: "networkidle" });
  const before = await page.evaluate(() => ({
    elements: document.querySelectorAll("*").length,
    scripts: document.querySelectorAll("script").length,
    linkHref: document.getElementById("lnk").getAttribute("href"),
  }));
  await page.evaluate(() => window.PixousI18n.setLanguage("ta"));
  await page.waitForFunction(() => ["done", "partial", "error"].includes(document.documentElement.getAttribute("data-pixous-i18n")), null, { timeout: 30000 });
  await page.waitForTimeout(500); // give any injected handler a chance to fire (it must not)

  const after = await page.evaluate(() => ({
    state: document.documentElement.getAttribute("data-pixous-i18n"),
    xss1: window.__xss1, xss2: window.__xss2, xss3: window.__xss3,
    elements: document.querySelectorAll("*").length,
    scripts: document.querySelectorAll("script").length,
    svgs: document.querySelectorAll("svg").length,
    imgs: document.querySelectorAll("img").length,
    handlers: document.querySelectorAll("[onload],[onerror],[onfocus],[onclick]").length,
    plainText: document.getElementById("plain").textContent,
    skelText: document.getElementById("skel").textContent,
    linkHref: document.getElementById("lnk").getAttribute("href"),
    altText: document.getElementById("pic").getAttribute("alt"),
    titleText: document.getElementById("btn").getAttribute("title"),
  }));

  check("no payload executed (svg onload)", after.xss1 === undefined);
  check("no payload executed (img onerror)", after.xss2 === undefined);
  check("no payload executed (script tag)", after.xss3 === undefined);
  check("no new elements created", after.elements === before.elements, before.elements + " -> " + after.elements);
  check("no new script elements", after.scripts === before.scripts);
  check("no svg element exists", after.svgs === 0);
  check("only the original img exists", after.imgs === 1);
  check("no inline event-handler attributes", after.handlers === 0);
  check("hostile markup landed as literal text (plain block)", after.plainText.includes("<svg onload=window.__xss1=1>"), JSON.stringify(after.plainText.slice(0, 60)));
  check("hostile markup landed as literal text (skeleton block)", after.skelText.includes("<script>window.__xss3=1</script>"));
  check("link element untouched (href intact)", after.linkHref === before.linkHref, after.linkHref);
  check("hostile text landed as literal attribute (alt)", after.altText.includes("<svg"), JSON.stringify((after.altText || "").slice(0, 40)));
  check("hostile text landed as literal attribute (title)", after.titleText.includes("<svg"));
  check("pass completed (state done or partial, not error)", after.state === "done" || after.state === "partial", after.state);

  await browser.close();
  console.log(fail === 0 ? "ALL XSS-SAFETY CHECKS PASSED" : fail + " CHECKS FAILED");
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error("RUNNER ERROR", e); process.exit(2); });
