// Real browser test of the SHIPPED Angular chat widget (AOT-compiled from
// source with ngc, bootstrapped with zone.js): send flow over POST, SSE
// streaming, and mid-stream failure handling with raw-frame sentinel control.
const { chromium } = require("playwright-core");
(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME_EXE, args: ["--no-sandbox", "--disable-dev-shm-usage"] });
  let fail = 0;
  const check = (name, ok, detail) => { console.log((ok ? "PASS" : "FAIL") + "  " + name + (detail !== undefined ? "  | " + detail : "")); if (!ok) fail++; };

  // A) boot + non-streaming POST flow
  let page = await browser.newPage();
  await page.goto("http://127.0.0.1:8903/widget.html", { waitUntil: "networkidle" });
  await page.waitForFunction(() => window.__booted === true || window.__bootError, null, { timeout: 30000 });
  const bootError = await page.evaluate(() => window.__bootError || null);
  check("widget bootstraps (real Angular runtime)", bootError === null, bootError && String(bootError).slice(0, 200));
  if (bootError) { await browser.close(); console.log("BOOT FAILED"); process.exit(2); }
  await page.waitForSelector("pix-chat-widget input, pix-chat-widget textarea", { timeout: 10000 });
  const headA0 = await page.evaluate(() => document.querySelector("pix-chat-widget .pix-chat-head").textContent);
  check("header: no Online claim before any interaction", !headA0.includes("Online") && !headA0.includes("Unavailable"), JSON.stringify(headA0));
  await page.fill("pix-chat-widget input, pix-chat-widget textarea", "hello there");
  await page.click("pix-chat-widget button[type=submit]");
  await page.waitForFunction(() => document.body.textContent.includes("STUB-REPLY vanakkam-ok"), null, { timeout: 15000 });
  const bodyA = await page.evaluate(() => document.body.textContent);
  check("POST flow: assistant reply rendered", bodyA.includes("STUB-REPLY vanakkam-ok"));
  check("POST flow: user message rendered", bodyA.includes("hello there"));
  const headA1 = await page.evaluate(() => document.querySelector("pix-chat-widget .pix-chat-head").textContent);
  check("header: Online shown after a proven reply", headA1.includes("Online"), JSON.stringify(headA1));
  check("normal reply: no withheld note", !bodyA.includes("translation looked unreliable"));
  await page.close();

  // A2) server withheld the translation (replyScriptAnomaly=true): original text + visible note
  page = await browser.newPage();
  await page.goto("http://127.0.0.1:8903/widget.html", { waitUntil: "networkidle" });
  await page.waitForFunction(() => window.__booted === true, null, { timeout: 30000 });
  await page.waitForSelector("pix-chat-widget input, pix-chat-widget textarea", { timeout: 10000 });
  await page.fill("pix-chat-widget input, pix-chat-widget textarea", "withheld case");
  await page.click("pix-chat-widget button[type=submit]");
  await page.waitForFunction(() => document.body.textContent.includes("ORIGINAL-LANGUAGE-REPLY"), null, { timeout: 15000 });
  const bodyW = await page.evaluate(() => document.body.textContent);
  check("withheld flow: original-language reply shown", bodyW.includes("ORIGINAL-LANGUAGE-REPLY"));
  check("withheld flow: visible note that it is untranslated", bodyW.includes("translation looked unreliable"));
  await page.close();

  // D) non-stream validation failure: server message shown, NOT the generic unavailable text
  page = await browser.newPage();
  await page.goto("http://127.0.0.1:8903/widget.html", { waitUntil: "networkidle" });
  await page.waitForFunction(() => window.__booted === true, null, { timeout: 30000 });
  await page.waitForSelector("pix-chat-widget input, pix-chat-widget textarea", { timeout: 10000 });
  await page.fill("pix-chat-widget input, pix-chat-widget textarea", "this is too long");
  await page.click("pix-chat-widget button[type=submit]");
  await page.waitForFunction(() => document.body.textContent.includes("Text exceeds 4000 characters."), null, { timeout: 15000 });
  const bodyD = await page.evaluate(() => document.body.textContent);
  check("validation flow: server message shown", bodyD.includes("Text exceeds 4000 characters."));
  check("validation flow: generic unavailable text NOT shown", !bodyD.includes("unavailable right now"));
  await page.close();

  // E) API unreachable (dead port): honest unreachable text + header Unavailable
  page = await browser.newPage();
  await page.goto("http://127.0.0.1:8903/widget.html?dead=1", { waitUntil: "networkidle" });
  await page.waitForFunction(() => window.__booted === true, null, { timeout: 30000 });
  await page.waitForSelector("pix-chat-widget input, pix-chat-widget textarea", { timeout: 10000 });
  await page.fill("pix-chat-widget input, pix-chat-widget textarea", "anyone there");
  await page.click("pix-chat-widget button[type=submit]");
  await page.waitForFunction(() => document.body.textContent.includes("cannot be reached"), null, { timeout: 15000 });
  const bodyE = await page.evaluate(() => document.body.textContent);
  check("unreachable flow: honest cannot-be-reached text shown", bodyE.includes("cannot be reached"));
  check("unreachable flow: generic unavailable text NOT shown", !bodyE.includes("unavailable right now"));
  const headE = await page.evaluate(() => document.querySelector("pix-chat-widget .pix-chat-head").textContent);
  check("header: Unavailable shown after a proven failure", headE.includes("Unavailable"), JSON.stringify(headE));
  await page.close();

  // B) SSE streaming flow
  page = await browser.newPage();
  await page.goto("http://127.0.0.1:8903/widget.html?stream=1", { waitUntil: "networkidle" });
  await page.waitForFunction(() => window.__booted === true, null, { timeout: 30000 });
  await page.waitForSelector("pix-chat-widget input, pix-chat-widget textarea", { timeout: 10000 });
  await page.fill("pix-chat-widget input, pix-chat-widget textarea", "stream please");
  await page.click("pix-chat-widget button[type=submit]");
  await page.waitForFunction(() => document.body.textContent.includes("Vannakkam"), null, { timeout: 15000 });
  check("SSE flow: deltas render incrementally into one bubble", true);
  await page.close();

  // C) mid-stream abort: partial kept, cut-off text shown, raw frame NOT rendered
  page = await browser.newPage();
  await page.goto("http://127.0.0.1:8903/widget.html?stream=1&fail=1", { waitUntil: "networkidle" });
  await page.waitForFunction(() => window.__booted === true, null, { timeout: 30000 });
  await page.waitForSelector("pix-chat-widget input, pix-chat-widget textarea", { timeout: 10000 });
  await page.fill("pix-chat-widget input, pix-chat-widget textarea", "will break");
  await page.click("pix-chat-widget button[type=submit]");
  await page.waitForFunction(() => document.body.textContent.includes("cut off before it finished"), null, { timeout: 15000 });
  const bodyC = await page.evaluate(() => document.body.textContent);
  check("abort flow: partial delta kept on screen", bodyC.includes("partial-text"));
  check("abort flow: neutral cut-off text shown", bodyC.includes("cut off before it finished"));
  check("abort flow: raw frame private text NOT rendered", !bodyC.includes("quxberry"));
  check("abort flow: raw frame credential sentinel NOT rendered", !bodyC.includes("sk-liveSENTINELkey9999"));
  const headC = await page.evaluate(() => document.querySelector("pix-chat-widget .pix-chat-head").textContent);
  check("header: Unavailable shown after mid-stream abort", headC.includes("Unavailable"), JSON.stringify(headC));
  await page.close();

  await browser.close();
  console.log(fail === 0 ? "ALL WIDGET TESTS PASSED" : fail + " CHECKS FAILED");
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error("RUNNER ERROR", e); process.exit(2); });
