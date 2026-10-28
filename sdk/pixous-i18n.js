/*!
 * pixous-i18n.js v1.2.0 — dynamic whole-site translation SDK (Pixous AI Suite).
 *
 * Talks ONLY to the host site's own backend (default /api/ai/translation/batch,
 * served by Pixous.AI.AspNetCore). Never calls an external provider directly.
 *
 * Privacy rails (do not weaken):
 * - Never reads or transmits form VALUES: input.value (except button/submit/
 *   reset labels, which are developer text), textarea content, and
 *   contenteditable content are never collected.
 * - password/hidden inputs are skipped entirely (not even placeholders).
 * - Exclusion rails: [data-no-translate], [translate="no"], .no-translate,
 *   and <script>/<style>/<noscript>/<code>/<pre>/<kbd>/<samp>/<var>/<template>
 *   subtrees are never collected or modified.
 * - Cross-origin iframes and closed shadow roots are skipped (not accessible).
 */
(function (global) {
  "use strict";

  var DEFAULTS = {
    endpoint: "/api/ai/translation/batch",
    sourceLanguage: "en",
    batchSize: 50,          // server accepts up to 100 per call
    maxCharsPerText: 2000,  // matches sidecar default
    observe: true,
    cache: true,              // in-memory cache (this page session)
    persistCache: false,      // OPT-IN: localStorage persistence. Hosts enabling it MUST call clearCache() on logout/account switch.
    cachePrefix: "pixous-i18n:",
    cacheVersion: "v1",       // bump to invalidate every stored entry
    cacheTtlMs: 7 * 24 * 3600 * 1000,  // entries expire after 7 days (persisted path only)
    tenant: "",               // set per tenant/user so persisted strings never leak across accounts
    debounceMs: 250,
    markerRetry: true,        // one bounded re-fetch with a fresh marker prefix for
                              // blocks whose markers failed validation or echo check
                              // (default ON 2026-10-27: measured 5/6 fresh-prefix recovery;
                              // set false to opt out).
    markerRetryMax: 10,       // max retried blocks per pass (single batched request)
    failedBlockMaxAttempts: 2, // a block is stranded (never auto re-fetched) only after this
                               // many failed fetch attempts per session - transient endpoint
                               // failures must not permanently strand corrected text.
                               // clearCache() resets. Session-scoped by design: an engine,
                               // model or config change implies a fresh init/session.
    echoCheckLanguages: ["ta","hi","te","ml","kn"],  // non-Latin targets: detect untranslated-source retention
    echoLatinThreshold: 0.6,  // reject when >60% of letters in the translation are still Latin
    echoAllowlist: [],        // strings/RegExp exempt from the Latin fraction (brand/product names);
                              // URLs and emails are always exempt. HEURISTIC guard, not semantic QA.
    markFactory: null,        // testing seam: () => marker prefix, defaults to random
    interpolationPatterns: null, // regexes for in-text interpolation tokens protected
                              // verbatim through the engine; default covers Angular/Vue/i18next
                              // {{x}}, template-literal ${x}, printf %s/%d/%i/%f, ICU {name}
    tokenMarkFactory: null,   // testing seam for the interpolation-marker prefix
    roundTripCheck: true,     // short plain blocks: back-translate through the SAME
                              // self-hosted endpoint and reject opposite-meaning flips
                              // (e.g. Sign out -> "Sign In"). Fail-closed on detected
                              // flip; fail-OPEN (accept + log) if the guard itself errors.
    roundTripMaxChars: 60,
    roundTripLanguages: ["ta","hi","te","ml","kn"],
    overrides: {},            // integrator-supplied exact-match glossary:
                              // { "Source text": { ta: "..." } }. NONE shipped by default;
                              // values are trusted verbatim and skip the echo heuristic.
    root: null,             // defaults to document.body
    onError: null,
    onLanguageChange: null
  };

  var SKIP_TAGS = {
    SCRIPT: 1, STYLE: 1, NOSCRIPT: 1, CODE: 1, PRE: 1, KBD: 1, SAMP: 1,
    VAR: 1, TEMPLATE: 1, TEXTAREA: 1, IFRAME: 1, OBJECT: 1, CANVAS: 1
  };
  var SKIP_INPUT_TYPES = { password: 1, hidden: 1, file: 1 };
  var BUTTON_INPUT_TYPES = { button: 1, submit: 1, reset: 1 };
  var TEXT_ATTRS = ["title", "alt", "aria-label"];
  var INLINE_TAGS = { A:1,B:1,STRONG:1,I:1,EM:1,U:1,SMALL:1,SUB:1,SUP:1,SPAN:1,ABBR:1,MARK:1,Q:1,CITE:1,DFN:1,TIME:1 };
  var VOID_INLINE = { BR:1, WBR:1 };

  var cfg = {};
  var originals = new WeakMap();      // Text node -> {kind:"text",value} | Element -> {kind:"attrs",values}
  var originalNodes = new Set();      // every node/element with an originals entry (WeakMap isn't iterable)
  var memCache = new Map();           // lang + "|" + text -> translation
  var polarityRejected = new Set();   // lang + "|" + text -> opposite-meaning flip detected; never re-sent
  var failedAttempts = new Map();     // lang + "|" + text -> failed fetch attempts this session.
                                      // A block is stranded (never auto re-fetched) only once its count
                                      // reaches cfg.failedBlockMaxAttempts: transient failures get another
                                      // chance on the next pass; persistent failures stop duplicate-storm
                                      // re-sends. Cleared by clearCache() - an explicit reset re-enables.
  function failNote(key) { failedAttempts.set(key, (failedAttempts.get(key) || 0) + 1); }
  function failStranded(key) { return (failedAttempts.get(key) || 0) >= cfg.failedBlockMaxAttempts; }
  function stableSkeletonKey(text) {
    // strip ALL marker-shaped tags (skeleton marks AND protected-token marks):
    // both are randomized per attempt, so a stable failure key must drop them
    return text.replace(/<\/?x[a-z0-9]+[tk]\d+>/g, "");
  }
  var generation = 0;
  var currentLanguage = null;
  var controller = null;              // AbortController for in-flight batches
  var applying = false;               // true while we mutate the DOM
  var observer = null;
  var observedRoots = [];
  var debounceTimer = null;
  var inFlight = null;
  var needsPass = false;
  var partialBlocks = 0;
  // Random per-init marker prefix: page content cannot collide with (or inject)
  // placeholder markers it cannot predict. Verified to survive the engine.
  function newMark() {
    if (typeof cfg.markFactory === "function") return cfg.markFactory();
    return "<x" + Math.random().toString(36).slice(2, 6) + "t";
  }
  function markerRe(mark) {
    var p = mark.slice(1); // without '<'
    return new RegExp("</?" + p.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "\\d+/?>", "g");
  }

  function log0(err) { if (cfg.onError) { try { cfg.onError(err); } catch (e) {} } }

  function isExcluded(el) {
    if (!el || el.nodeType !== 1) return false;
    if (SKIP_TAGS[el.tagName]) return true;
    if (el.isContentEditable) return true;
    if (el.hasAttribute("data-no-translate")) return true;
    if (el.getAttribute("translate") === "no") return true;
    if (el.classList && el.classList.contains("no-translate")) return true;
    if (el.tagName === "INPUT" && SKIP_INPUT_TYPES[(el.getAttribute("type") || "text").toLowerCase()]) return true;
    return false;
  }

  function splitWs(t) {
    var m = t.match(/^(\s*)([\s\S]*?)(\s*)$/);
    return { lead: m[1], core: m[2], trail: m[3] };
  }

  function collectTextNodes(root, out) {
    var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
      acceptNode: function (node) {
        if (!node.nodeValue || !node.nodeValue.trim()) return NodeFilter.FILTER_REJECT;
        var el = node.parentElement;
        if (!el) return NodeFilter.FILTER_REJECT;
        for (var p = el; p && p !== root.parentElement; p = p.parentElement) {
          if (isExcluded(p)) return NodeFilter.FILTER_REJECT;
        }
        return NodeFilter.FILTER_ACCEPT;
      }
    });
    var node;
    while ((node = walker.nextNode())) out.push(node);
  }

  // Build translation units from text nodes:
  // - "merged": element with only text children -> one whole-sentence unit.
  // - "skeleton": block containing text + inline markup -> ONE unit with
  //   <tK>/</tK> placeholder tags protecting element structure (verified to
  //   survive the engine); restored only if the marker sequence comes back
  //   EXACTLY intact, otherwise that block falls back to per-node.
  // - "text": single node (fallback).
  var BLOCK_TAGS = { ADDRESS:1,ARTICLE:1,ASIDE:1,BLOCKQUOTE:1,BODY:1,BUTTON:1,DIV:1,DL:1,DT:1,DD:1,FIELDSET:1,FIGCAPTION:1,FIGURE:1,FOOTER:1,FORM:1,H1:1,H2:1,H3:1,H4:1,H5:1,H6:1,HEADER:1,LABEL:1,LEGEND:1,LI:1,MAIN:1,NAV:1,OL:1,OPTION:1,P:1,SECTION:1,SUMMARY:1,TABLE:1,TD:1,TH:1,UL:1 };

  function blockAncestor(node, root) {
    var el = node.parentElement;
    while (el && el !== root && el !== root.parentElement) {
      if (BLOCK_TAGS[el.tagName] || el === root) return el;
      if (!INLINE_TAGS[el.tagName]) return el; // unknown container: stop here
      el = el.parentElement;
    }
    return null;
  }

  // All descendants of block are text or inline/void-inline, nothing excluded?
  function skeletonEligible(block, nodesInBlock) {
    var set = new Set(nodesInBlock);
    var ok = true;
    (function walk(el) {
      if (!ok) return;
      for (var i = 0; i < el.childNodes.length; i++) {
        var c = el.childNodes[i];
        if (c.nodeType === 3) continue;
        if (c.nodeType !== 1) { ok = false; return; }
        if (set.has(c)) continue;
        if (isExcluded(c) || c.ownerSVGElement || (!INLINE_TAGS[c.tagName] && !VOID_INLINE[c.tagName])) { ok = false; return; }
        walk(c);
      }
    })(block);
    return ok;
  }

  function emitSkeleton(sk) {
    // (re)render the skeleton text + marker sequence for sk.mark
    var markerSeq = [];
    var text = "";
    var collision = false;
    sk.parts.forEach(function (part) {
      if (part.t === "text") { var v = sk.valueOf(slotsOf(sk)[part.i]); if (v.indexOf(sk.mark) !== -1) collision = true; text += " " + v + " "; }
      else if (part.t === "open") { text += sk.mark + part.i + ">"; markerSeq.push(sk.mark + part.i + ">"); }
      else if (part.t === "close") { text += "</" + sk.mark.slice(1) + part.i + ">"; markerSeq.push("</" + sk.mark.slice(1) + part.i + ">"); }
      else { text += sk.mark + part.i + "/>"; markerSeq.push(sk.mark + part.i + "/>"); }
    });
    sk.markerSeq = markerSeq; sk.text = text; sk.collision = collision;
    return sk;
  }
  function slotsOf(sk) { return sk.slots; }

  function buildSkeleton(block, valueOf) {
    var slots = [];       // ALL text nodes in document order (incl whitespace-only)
    var markerSeq = [];
    var elIndex = new Map();
    var parts = [];
    (function walk(el) {
      for (var i = 0; i < el.childNodes.length; i++) {
        var c = el.childNodes[i];
        if (c.nodeType === 3) { slots.push(c); parts.push({ t: "text", i: slots.length - 1 }); }
        else if (c.nodeType === 1) {
          if (VOID_INLINE[c.tagName]) {
            var vi = elIndex.size; elIndex.set(c, vi);
            parts.push({ t: "void", i: vi, el: c });
          } else {
            var k = elIndex.size; elIndex.set(c, k);
            parts.push({ t: "open", i: k, el: c });
            walk(c);
            parts.push({ t: "close", i: k, el: c });
          }
        }
      }
    })(block);
    return emitSkeleton({ slots: slots, parts: parts, mark: newMark(), valueOf: valueOf });
  }

  function buildSkeletonFromValues(sk) {
    var cur = "";
    sk.parts.forEach(function (part) {
      if (part.t === "text") cur += sk.slots[part.i].nodeValue;
      else if (part.t === "open") cur += sk.mark + part.i + ">";
      else if (part.t === "close") cur += "</" + sk.mark.slice(1) + part.i + ">";
      else cur += sk.mark + part.i + "/>";
    });
    return cur;
  }

  function escRe(x) { return x.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"); }

  // Apply a translated skeleton string. Returns true only when every marker
  // came back exactly once, in the original order and nesting.
  // Untranslated-source retention check: for non-Latin targets, a translation
  // whose letters are still mostly Latin means the engine echoed source text.
  // Brand-name tokens are a minority of letters, so a 0.6 threshold lets them pass.
  // Heuristic partial guard, NOT semantic quality assurance: legitimate
  // names/brands/code/English-heavy UI can trip it. URLs, emails and
  // cfg.echoAllowlist entries are exempted before the fraction is computed.
  function stripEchoExemptions(s) {
    s = s.replace(/https?:\/\/\S+/gi, " ").replace(/[\w.+-]+@[\w-]+\.[\w.]+/g, " ");
    for (var i = 0; i < cfg.echoAllowlist.length; i++) {
      var e = cfg.echoAllowlist[i];
      s = e instanceof RegExp ? s.replace(new RegExp(e.source, e.flags.includes("g") ? e.flags : e.flags + "g"), " ") : s.split(e).join(" ");
    }
    return s;
  }

  function echoDetected(translated, mark) {
    if (cfg.echoCheckLanguages.indexOf(currentLanguage) === -1) return false;
    var stripped = stripEchoExemptions(mark ? translated.replace(markerRe(mark), " ") : translated);
    var latin = (stripped.match(/[A-Za-z]/g) || []).length;
    var nonAsciiLetters = (stripped.match(/[^\x00-\x7F]/g) || []).length;
    var total = latin + nonAsciiLetters;
    return total > 0 && latin / total > cfg.echoLatinThreshold;
  }

  function applySkeleton(sk, translated, originalCurrent) {
    var found = translated.match(markerRe(sk.mark)) || [];
    if (found.length !== sk.markerSeq.length) { return false; }
    for (var i = 0; i < found.length; i++) if (found[i] !== sk.markerSeq[i]) { return false; }
    if (echoDetected(translated, sk.mark)) { return false; }
    if (originalCurrent !== null) {
      // A slot is safe to overwrite when it still holds the ORIGINAL text or
      // exactly what WE last wrote (e.g. a previous language). Anything else
      // means the page itself changed the content: preserve it, do not apply.
      for (var si = 0; si < sk.slots.length; si++) {
        var sn = sk.slots[si];
        var recS = originals.get(sn);
        var origS = recS && recS.kind === "text" ? recS.value : null;
        if (origS !== null && sn.nodeValue === origS) continue;
        if (weApplied.has(sn) && weApplied.get(sn) === sn.nodeValue) continue;
        if (origS === null && buildSkeletonFromValues(sk) === originalCurrent) break; // no originals recorded yet: legacy compare
        return false;
      }
    }
    // split translated text into chunks aligned to text slots
    var textParts = sk.parts.filter(function (pt) { return pt.t === "text"; });
    textParts.forEach(function (pt) { pt.v = sk.slots[pt.i].nodeValue; });
    var markerAlt = sk.markerSeq.map(escRe).join("|");
    var re = new RegExp("(" + markerAlt + ")", "g");
    var markerOnly = new RegExp("^" + markerAlt + "$");
    var pieces = translated.split(re).filter(function (x) { return !markerOnly.test(x); });
    // Engines legitimately pad around markers ("<x0> text </x0>"), yielding a
    // leading/trailing empty segment. Trim exactly those; any other count
    // mismatch still fails closed and preserves the original block.
    if (pieces.length > textParts.length && pieces[0] === "") pieces.shift();
    if (pieces.length > textParts.length && pieces[pieces.length - 1] === "") pieces.pop();
    // pieces align positionally with text parts (slot order) when structure matched
    if (pieces.length !== textParts.length) { return false; }
    for (var j = 0; j < textParts.length; j++) {
      var node = sk.slots[textParts[j].i];
      // Skeleton slots are sent space-padded so markers never glue to words;
      // take back exactly one pad space the original slot did not have.
      var piece = pieces[j], origSlot = textParts[j].v;
      if (!/^\s/.test(origSlot) && piece.charAt(0) === " ") piece = piece.slice(1);
      if (!/\s$/.test(origSlot) && piece.charAt(piece.length - 1) === " ") piece = piece.slice(0, -1);
      node.nodeValue = piece;
      weApplied.set(node, piece);
    }
    return true;
  }

  function buildTranslationUnits(nodes, root) {
    // group by block ancestor
    var byBlock = new Map();
    nodes.forEach(function (n) {
      if (n.ownerSVGElement) { byBlock.set(n, [n]); return; } // svg text: own unit
      var b = blockAncestor(n, root) || n.parentElement;
      if (!byBlock.has(b)) byBlock.set(b, []);
      byBlock.get(b).push(n);
    });
    var units = [];
    byBlock.forEach(function (group, block) {
      if (group.length === 1 && block.nodeType === 3) { units.push({ kind: "text", node: group[0], text: group[0].nodeValue }); return; }
      var parent = group[0].parentElement;
      var textOnly = group.length > 1 && parent.childNodes.length === group.length &&
        Array.prototype.every.call(parent.childNodes, function (c) { return c.nodeType === 3; });
      if (textOnly) {
        units.push({ kind: "merged", nodes: group, text: group.map(function (n) { return n.nodeValue; }).join("") });
        return;
      }
      if (block.nodeType === 1 && group.length >= 2 && skeletonEligible(block, group)) {
        var sk = buildSkeleton(block, function (n) {
          var r = originals.get(n);
          return r && r.kind === "text" ? r.value : n.nodeValue;
        });
        if (sk.text.trim().length && !sk.collision && sk.text.length <= cfg.maxCharsPerText && sk.markerSeq.length) {
          units.push({ kind: "skeleton", sk: sk, text: sk.text });
          return;
        }
      }
      group.forEach(function (n) { units.push({ kind: "text", node: n, text: n.nodeValue }); });
    });
    return units;
  }

  function collectAttrs(root, out) {
    var walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT, {
      acceptNode: function (el) {
        for (var p = el; p && p !== root.parentElement; p = p.parentElement) {
          if (isExcluded(p)) return NodeFilter.FILTER_REJECT;
        }
        return NodeFilter.FILTER_ACCEPT;
      }
    });
    var el;
    while ((el = walker.nextNode())) {
      if (el.tagName === "INPUT") {
        var type = (el.getAttribute("type") || "text").toLowerCase();
        if (BUTTON_INPUT_TYPES[type]) {
          var v = el.getAttribute("value");
          if (v && v.trim()) out.push({ el: el, attr: "value", text: v });
        }
        var ph = el.getAttribute("placeholder");
        if (ph && ph.trim()) out.push({ el: el, attr: "placeholder", text: ph });
        continue;
      }
      for (var i = 0; i < TEXT_ATTRS.length; i++) {
        var a = TEXT_ATTRS[i], val = el.getAttribute(a);
        if (val && val.trim()) out.push({ el: el, attr: a, text: val });
      }
    }
  }

  function shadowRoots(root, out) {
    var walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
    var el;
    while ((el = walker.nextNode())) {
      if (el.shadowRoot) out.push(el.shadowRoot);  // open roots only; closed are inaccessible
    }
  }

  function sameOriginFrames(root, out) {
    var frames = root.querySelectorAll ? root.querySelectorAll("iframe") : [];
    for (var i = 0; i < frames.length; i++) {
      try {
        var doc = frames[i].contentDocument;
        if (doc && doc.body) out.push(doc.body);
      } catch (e) { /* cross-origin: skip by design */ }
    }
  }

  function cacheKey(lang, text) {
    // namespace: version + tenant + target language isolate entries; logout calls clearCache()
    return cfg.cachePrefix + cfg.cacheVersion + "|" + cfg.tenant + "|" + lang + "|" + text;
  }

  function cacheGet(lang, text) {
    var k = lang + "|" + text;
    if (memCache.has(k)) return memCache.get(k);
    if (cfg.cache && cfg.persistCache) {
      try {
        var raw = global.localStorage.getItem(cacheKey(lang, text));
        if (raw !== null) {
          var rec = JSON.parse(raw);
          if (rec && typeof rec.v === "string" && (Date.now() - rec.t) < cfg.cacheTtlMs) {
            memCache.set(k, rec.v); return rec.v;
          }
          global.localStorage.removeItem(cacheKey(lang, text)); // expired
        }
      } catch (e) { /* storage full/blocked: memory cache still works */ }
    }
    return null;
  }

  function cacheSet(lang, text, translation) {
    var k = lang + "|" + text;
    memCache.set(k, translation);
    if (cfg.cache && cfg.persistCache) {
      try { global.localStorage.setItem(cacheKey(lang, text), JSON.stringify({ t: Date.now(), v: translation })); }
      catch (e) { /* ignore */ }
    }
  }

  var weApplied = new WeakMap(); // node/element -> last value we set (observer self-filter)

  var DEFAULT_TOKEN_RES = [/\{\{[^{}]+\}\}/g, /\$\{[^}]+\}/g, /%[sdif]\b/g, /\{[a-zA-Z_][\w.]*\}/g];
  var POSITIONAL_TOKEN_RE = /^%[sdif]$/; // printf-style: binding is by ORDER, values may not reorder
  // Sentinel: engine response failed token round-trip validation. Fail-closed:
  // the original block stays, the page is marked partial. Never cached.
  var TOKEN_FAIL = { tokenFail: true };

  function newTokenPrefix(texts) {
    for (var attempt = 0; attempt < 20; attempt++) {
      var p = (typeof cfg.tokenMarkFactory === "function")
        ? cfg.tokenMarkFactory()
        : "<x" + Math.random().toString(36).slice(2, 8) + "k";
      // literal/adversarial collision: page text must never contain our prefix
      var collides = false;
      for (var i = 0; i < texts.length; i++) if (texts[i].indexOf(p) !== -1) { collides = true; break; }
      if (!collides) return p;
    }
    return null; // cannot protect safely: caller must fail closed
  }

  function textHasTokens(text) {
    var res = cfg.interpolationPatterns || DEFAULT_TOKEN_RES;
    for (var i = 0; i < res.length; i++) { res[i].lastIndex = 0; if (res[i].test(text)) return true; }
    return false;
  }

  function stripTokenPatterns(text) {
    var res = cfg.interpolationPatterns || DEFAULT_TOKEN_RES, out = text;
    for (var i = 0; i < res.length; i++) { res[i].lastIndex = 0; out = out.replace(res[i], " "); }
    return out.replace(/\s+/g, " ").trim();
  }

  function protectTokens(texts) {
    var res = cfg.interpolationPatterns || DEFAULT_TOKEN_RES;
    var prefix = newTokenPrefix(texts);
    if (prefix === null) return null;
    var maps = texts.map(function (t) {
      var tokens = [];
      var out = t;
      res.forEach(function (re) {
        re.lastIndex = 0;
        out = out.replace(re, function (m) { tokens.push(m); return prefix + (tokens.length - 1) + ">"; });
      });
      return { text: out, tokens: tokens };
    });
    return { prefix: prefix, maps: maps };
  }

  // Strict identity/count validation: every protected token exactly once,
  // no extra/leftover markers. NAMED tokens ({{x}}, ${x}, {name}) may reorder
  // freely (SOV reordering is correct). POSITIONAL printf tokens (%s/%d/...)
  // may NOT: their binding is by position, so a reordered sequence would
  // silently bind values to the wrong words - fail closed instead.
  function restoreTokens(str, map, prefix) {
    var i, out = str;
    for (i = 0; i < map.tokens.length; i++) {
      var n = out.split(prefix + i + ">").length - 1;
      if (n !== 1) return null;
    }
    var lastPos = -1;
    for (i = 0; i < map.tokens.length; i++) {
      if (!POSITIONAL_TOKEN_RE.test(map.tokens[i])) continue;
      var at = out.indexOf(prefix + i + ">");
      if (at < lastPos) return null; // positional token reordered: unsafe binding
      lastPos = at;
    }
    for (i = 0; i < map.tokens.length; i++) out = out.replace(prefix + i + ">", map.tokens[i]);
    if (out.indexOf(prefix) !== -1) return null; // duplicated/foreign marker left behind
    return out;
  }

  // English-side opposite-meaning word pairs. This is NOT a translation and
  // NOT generic quality scoring (measured: token-overlap F1 both misses real
  // flips and flags good paraphrases); it catches polarity flips only.
  var POLARITY_PAIRS = [
    ["sign in","sign out"],["log in","log out"],["login","logout"],
    ["in","out"],["on","off"],["enable","disable"],["enabled","disabled"],
    ["open","close"],["show","hide"],["lock","unlock"],["yes","no"],
    ["allow","deny"],["accept","reject"],["start","stop"],["enter","exit"]
  ];
  function phraseOf(s) { return " " + s.toLowerCase().replace(/[^a-z]+/g, " ").replace(/\s+/g, " ").trim() + " "; }
  // Directional short labels ("sign out", "log in"): the direction word carries
  // the meaning. If the back-translation keeps the shared stem but loses EVERY
  // direction-bearing wording, the translation is unverifiable - fail closed.
  // A clean paraphrase that drops the stem entirely (e.g. "Exit") is accepted;
  // only observed stem-without-direction is ambiguous. Bounded table, same
  // fail-closed posture as polarityFlip; not a per-case patch list.
  // Direction SETS per stem: out/in/up (register). "Sign up" vs "Sign in" is
  // not an opposite pair but an equally dangerous wrong-action change, so a
  // back-translation landing in a DIFFERENT set fails closed here too
  // (polarityFlip only covers true opposites).
  var DIRECTIONAL_LABELS = [
    { stem: "sign",
      out: ["sign out", "log out", "logout", "sign off", "signoff"],
      inn: ["sign in", "log in", "login", "signin", "sign on"],
      up:  ["sign up", "signup", "register", "create account", "create an account"] },
    { stem: "log",
      out: ["log out", "logout", "sign out", "sign off", "signoff"],
      inn: ["log in", "login", "sign in", "signin", "sign on"],
      up:  [] }
  ];
  var DIRECTION_SET_NAMES = ["out", "inn", "up"];
  function directionalAmbiguity(source, backTranslation) {
    var ps = phraseOf(source), pb = phraseOf(backTranslation);
    for (var i = 0; i < DIRECTIONAL_LABELS.length; i++) {
      var g = DIRECTIONAL_LABELS[i], srcSet = null, j, k;
      for (k = 0; k < DIRECTION_SET_NAMES.length; k++) {
        var setName = DIRECTION_SET_NAMES[k], phrases = g[setName];
        for (j = 0; j < phrases.length; j++) if (ps.indexOf(" " + phrases[j] + " ") !== -1) srcSet = setName;
      }
      if (srcSet === null) continue;
      for (j = 0; j < g[srcSet].length; j++) if (pb.indexOf(" " + g[srcSet][j] + " ") !== -1) return null; // direction preserved
      for (k = 0; k < DIRECTION_SET_NAMES.length; k++) {
        var other = DIRECTION_SET_NAMES[k];
        if (other === srcSet) continue;
        for (j = 0; j < g[other].length; j++) {
          if (pb.indexOf(" " + g[other][j] + " ") !== -1) {
            // True opposites (out vs in) are polarityFlip's report; other
            // direction changes (e.g. register vs login) are reported here.
            var isOppositePair = (srcSet === "out" && other === "inn") || (srcSet === "inn" && other === "out");
            if (isOppositePair) return null;
            return [srcSet, "direction changed in round-trip"];
          }
        }
      }
      if (pb.indexOf(" " + g.stem + " ") !== -1) return [srcSet, "direction word lost in round-trip"];
    }
    return null;
  }
  function polarityFlip(source, backTranslation) {
    var ps = phraseOf(source), pb = phraseOf(backTranslation);
    for (var i = 0; i < POLARITY_PAIRS.length; i++) {
      var a = " " + POLARITY_PAIRS[i][0] + " ", b = " " + POLARITY_PAIRS[i][1] + " ";
      if (ps.indexOf(a) !== -1 && pb.indexOf(b) !== -1 && pb.indexOf(a) === -1) return [a.trim(), b.trim()];
      if (ps.indexOf(b) !== -1 && pb.indexOf(a) !== -1 && pb.indexOf(b) === -1) return [b.trim(), a.trim()];
    }
    return null;
  }

  function fetchBatch(texts, target, gen, srcLang) {
    var prot = protectTokens(texts);
    if (prot === null) {
      // cannot protect tokens without collision: fail closed for every text
      return Promise.resolve(texts.map(function () { return TOKEN_FAIL; }));
    }
    var sendTexts = prot.maps.map(function (m) { return m.text; });
    if (controller && controller._gen !== gen) controller.abort();
    controller = new AbortController();
    controller._gen = gen;
    var signal = controller.signal;
    return fetch(cfg.endpoint, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        texts: sendTexts,
        targetLanguage: target,
        sourceLanguage: srcLang || cfg.sourceLanguage
      }),
      signal: signal
    }).then(function (res) {
      if (!res.ok) throw new Error("translation endpoint returned " + res.status);
      return res.json();
    }).then(function (envelope) {
      var data = envelope && (envelope.data || envelope);
      if (!data || !Array.isArray(data.translatedTexts)) throw new Error("unexpected response shape");
      return data.translatedTexts.map(function (t, i) {
        if (typeof t !== "string") return TOKEN_FAIL;
        var restored = restoreTokens(t, prot.maps[i], prot.prefix);
        return restored === null ? TOKEN_FAIL : restored;
      });
    }).catch(function (err) {
      if (err && err.name === "AbortError") return null; // superseded by a newer switch
      throw err;
    });
  }

  function setState(s) {
    var de = document.documentElement;
    if (s) de.setAttribute("data-pixous-i18n", s); else de.removeAttribute("data-pixous-i18n");
  }

  // Apply a translation map to collected targets. Only applies if gen is current.
  function applyAll(plan, map, gen, retryQueue, overrideApplied, target) {
    if (gen !== generation) return; // stale: user switched again
    applying = true;
    try {
      if (!overrideApplied) overrideApplied = new Map();
      var failedSkeletons = [];
      var echoRejected = 0;
      var tokenRejected = 0;
      for (var i = 0; i < plan.length; i++) {
        var item = plan[i];
        var t = map.get(item.text);
        if (t == null) continue;
        if (t === TOKEN_FAIL) {
          // interpolation-token corruption in the engine response: preserve
          // original + partial. Skeletons go through the one bounded retry
          // (marker loss is prefix-dependent); plain blocks do not.
          if (item.kind === "skeleton") failedSkeletons.push(item);
          else { tokenRejected++; if (target) failNote(target + "|" + item.text); }
          continue;
        }
        if (item.kind !== "skeleton" && overrideApplied.get(currentLanguage + "|" + item.text) !== t && echoDetected(t, null)) {
          // untranslated-source retention on a plain block: preserve original,
          // count partial. Not retried - plain text has no prefix dependence,
          // a re-fetch returns the same echo.
          echoRejected++;
          if (target) failNote(target + "|" + item.text);
          continue;
        }
        if (item.kind === "skeleton") {
          var _ok = applySkeleton(item.sk, t, item.current);
          if (!_ok) { failedSkeletons.push(item); }
          continue;
        }
        if (item.kind === "merged") {
          var now = item.nodes.map(function (n) { return n.nodeValue; }).join("");
          var oursAll = item.nodes.every(function (n) { return weApplied.has(n) && weApplied.get(n) === n.nodeValue; });
          if (now === item.current || oursAll) {
            var full = item.lead + t + item.trail;
            item.nodes[0].nodeValue = full;
            weApplied.set(item.nodes[0], full);
            for (var j = 1; j < item.nodes.length; j++) { item.nodes[j].nodeValue = ""; weApplied.set(item.nodes[j], ""); }
          }
        } else if (item.kind === "text") {
          if (item.node.nodeValue === item.current ||
              (weApplied.has(item.node) && weApplied.get(item.node) === item.node.nodeValue)) {
            var v = item.lead + t + item.trail;
            item.node.nodeValue = v; weApplied.set(item.node, v);
          }
        } else {
          if (item.el.getAttribute(item.attr) === item.current) { item.el.setAttribute(item.attr, t); weApplied.set(item.el, t); }
        }
      }
    } finally {
      applying = false;
    }
    if (tokenRejected && gen === generation) {
      partialBlocks += tokenRejected;
      setState("partial");
      log0(new Error(tokenRejected + " block(s) kept in original language: interpolation token corrupted in translation"));
    }
    if (echoRejected && gen === generation) {
      partialBlocks += echoRejected;
      setState("partial");
      log0(new Error(echoRejected + " block(s) kept in original language: untranslated source detected (echo guard)"));
    }
    if (failedSkeletons.length && gen === generation) {
      // Marker validation failed for these blocks: the ORIGINAL text stays in
      // place (never fragment-translate). With markerRetry enabled they are
      // queued for ONE bounded re-fetch with a fresh marker prefix instead of
      // an immediate partial; otherwise the page is marked partial directly.
      if (retryQueue) {
        failedSkeletons.forEach(function (it) {
          if (retryQueue.length < cfg.markerRetryMax && retryQueue.indexOf(it) === -1) retryQueue.push(it);
        });
      } else {
        partialBlocks += failedSkeletons.length;
        setState("partial");
        log0(new Error(failedSkeletons.length + " block(s) kept in original language: translation markers failed validation"));
      }
    }
  }

  // One bounded retry pass: re-render each failed skeleton with a FRESH random
  // marker prefix (loss is prefix-dependent, not content-dependent - measured),
  // single batched fetch, strict validation again. Anything still failing stays
  // in the original language and marks the page partial. No loop, no fragments.
  function retryFailedSkeletons(queue, target, gen) {
    if (!queue.length || gen !== generation) return Promise.resolve(0);
    var attempts = queue.map(function (item) {
      item.sk.mark = newMark();
      emitSkeleton(item.sk);
      // stale-check baseline must be re-rendered with the NEW mark
      item.current = buildSkeletonFromValues(item.sk);
      return { item: item, text: item.sk.text };
    });
    return fetchBatch(attempts.map(function (a) { return a.text; }), target, gen).then(function (translations) {
      if (translations === null || gen !== generation) return 0; // superseded
      var stillFailed = 0;
      applying = true;
      try {
        for (var i = 0; i < attempts.length; i++) {
          var a = attempts[i];
          var t = translations[i];
          var ok = typeof t === "string" && t.length && applySkeleton(a.item.sk, t, a.item.current);
          if (!ok) { stillFailed++; failNote(target + "|" + stableSkeletonKey(a.text)); }
        }
      } finally {
        applying = false;
      }
      return stillFailed;
    }).catch(function () {
      attempts.forEach(function (a) { failNote(target + "|" + stableSkeletonKey(a.text)); });
      return queue.length;
    }); // retry fetch failed: all stay original and are not auto re-fetched
  }

  function restoreRoot(root) {
    var nodes = [];
      collectTextNodes(root, nodes);
      for (var i = 0; i < nodes.length; i++) {
        var rec = originals.get(nodes[i]);
        if (rec && rec.kind === "text") nodes[i].nodeValue = rec.value;
      }
      // Attributes
      var attrs = [];
      collectAttrs(root, attrs);
      for (var j = 0; j < attrs.length; j++) {
        var recA = originals.get(attrs[j].el);
        if (recA && recA.kind === "attrs" && attrs[j].attr in recA.values) {
          attrs[j].el.setAttribute(attrs[j].attr, recA.values[attrs[j].attr]);
        }
      }
  }

  function restoreOriginals() {
    generation++;
    if (controller) { controller.abort(); controller = null; }
    applying = true;
    try {
      var roots = [cfg.root].concat(scanExtraRoots());
      for (var r = 0; r < roots.length; r++) restoreRoot(roots[r]);
    } finally {
      applying = false;
    }
  }

  function translateRoot(gen) {
    // Queued observer passes can fire after a language switch or a newer pass;
    // bail unless this pass still belongs to the active target language.
    if (gen !== generation || !currentLanguage || currentLanguage === cfg.sourceLanguage) return Promise.resolve();
    var target = currentLanguage;
    var nodes = [];
    collectTextNodes(cfg.root, nodes);
    var attrs = [];
    collectAttrs(cfg.root, attrs);

    var plan = [];       // {kind:"text"|"merged"|"attr", ...}
    var toTranslate = new Map(); // core source text -> null placeholder
    var i, rec;

    var kindByText = new Map(); // text -> {plain, skel}
    function markKind(text, kind) {
      var k = kindByText.get(text);
      if (!k) { k = { plain: false, skel: false }; kindByText.set(text, k); }
      if (kind === "skeleton") k.skel = true; else k.plain = true;
    }
    var units = buildTranslationUnits(nodes, cfg.root);
    for (i = 0; i < units.length; i++) {
      var u = units[i];
      if (u.kind === "skeleton") {
        u.sk.slots.forEach(function (n) {
          if (!originals.has(n)) { originals.set(n, { kind: "text", value: n.nodeValue }); originalNodes.add(n); }
        });
        var cur = buildSkeletonFromValues(u.sk);
        plan.push({ kind: "skeleton", sk: u.sk, text: u.text, current: cur });
        markKind(u.text, "skeleton");
        if (cacheGet(target, u.text) === null && !failStranded(target + "|" + stableSkeletonKey(u.text))) toTranslate.set(u.text, null);
        continue;
      }
      if (u.kind === "merged") {
        var srcMerged = u.nodes.map(function (n) {
          var r = originals.get(n);
          return r && r.kind === "text" ? r.value : n.nodeValue;
        }).join("");
        u.nodes.forEach(function (n) {
          if (!originals.has(n)) { originals.set(n, { kind: "text", value: n.nodeValue }); originalNodes.add(n); }
        });
        var wsM = splitWs(srcMerged);
        if (!wsM.core || wsM.core.length > cfg.maxCharsPerText) continue;
        plan.push({ kind: "merged", nodes: u.nodes, text: wsM.core, lead: wsM.lead, trail: wsM.trail,
                    current: u.nodes.map(function (n) { return n.nodeValue; }).join("") });
        markKind(wsM.core, "merged");
        if (cacheGet(target, wsM.core) === null && !polarityRejected.has(target + "|" + wsM.core) && !failStranded(target + "|" + wsM.core)) toTranslate.set(wsM.core, null);
      } else {
        var n = u.node;
        rec = originals.get(n);
        var sourceText = rec && rec.kind === "text" ? rec.value : n.nodeValue;
        if (!originals.has(n)) { originals.set(n, { kind: "text", value: n.nodeValue }); originalNodes.add(n); }
        var ws = splitWs(sourceText);
        if (!ws.core || ws.core.length > cfg.maxCharsPerText) continue;
        plan.push({ kind: "text", node: n, text: ws.core, lead: ws.lead, trail: ws.trail, current: n.nodeValue });
        markKind(ws.core, "text");
        if (cacheGet(target, ws.core) === null && !polarityRejected.has(target + "|" + ws.core) && !failStranded(target + "|" + ws.core)) toTranslate.set(ws.core, null);
      }
    }
    for (i = 0; i < attrs.length; i++) {
      var a = attrs[i];
      rec = originals.get(a.el);
      if (!rec || rec.kind !== "attrs") { rec = { kind: "attrs", values: {} }; originals.set(a.el, rec); originalNodes.add(a.el); }
      var sourceAttr = (a.attr in rec.values) ? rec.values[a.attr] : a.text;
      if (!(a.attr in rec.values)) rec.values[a.attr] = a.text;
      if (sourceAttr.length > cfg.maxCharsPerText) continue;
      plan.push({ kind: "attr", el: a.el, attr: a.attr, text: sourceAttr, current: a.text });
      markKind(sourceAttr, "attr");
      if (cacheGet(target, sourceAttr) === null && !polarityRejected.has(target + "|" + sourceAttr) && !failStranded(target + "|" + sourceAttr)) toTranslate.set(sourceAttr, null);
    }

    // Apply cached translations and integrator-supplied overrides immediately.
    // Overrides: exact-match on the trimmed core text, plain block kinds only
    // (skeleton texts contain internal markers and are not override keys).
    var retryQueue = cfg.markerRetry ? [] : null;
    var map = new Map();
    var overrideApplied = new Map(); // lang|text -> value, for echo-guard exemption
    plan.forEach(function (item) {
      var cached = cacheGet(target, item.text);
      if (cached !== null) { map.set(item.text, cached); return; }
      if (item.kind === "skeleton") return;
      var o = cfg.overrides && cfg.overrides[item.text];
      var ov = o && typeof o[target] === "string" ? o[target] : null;
      if (ov !== null) { map.set(item.text, ov); overrideApplied.set(target + "|" + item.text, ov); }
    });
    applyAll(plan, map, gen, retryQueue, overrideApplied, target);

    var pending = Array.from(toTranslate.keys()).filter(function (t) { return !map.has(t); });
    if (pending.length === 0) {
      // nothing to fetch: stranded (repeatedly failed) blocks still mean partial, not done
      var stranded0 = false;
      failedAttempts.forEach(function (n, key) { if (n >= cfg.failedBlockMaxAttempts && key.indexOf(target + "|") === 0) stranded0 = true; });
      polarityRejected.forEach(function (key) { if (key.indexOf(target + "|") === 0) stranded0 = true; });
      setState(stranded0 ? "partial" : "done");
      return Promise.resolve();
    }

    setState("translating");
    // Chunk sequentially; each chunk checks staleness before applying.
    var chunks = [];
    for (i = 0; i < pending.length; i += cfg.batchSize) chunks.push(pending.slice(i, i + cfg.batchSize));

    var superseded = false;
    var rtPending = new Map(); // source -> translation awaiting round-trip polarity check
    var done = chunks.reduce(function (chain, chunk) {
      return chain.then(function () {
        if (gen !== generation) { superseded = true; return null; }
        return fetchBatch(chunk, target, gen).then(function (translations) {
          if (translations === null) { superseded = true; return; } // aborted: superseded
          for (var k = 0; k < chunk.length; k++) {
            var t = translations[k];
            if (t === TOKEN_FAIL) { map.set(chunk[k], TOKEN_FAIL); }
            else if (typeof t === "string" && t.length) {
              var kd = kindByText.get(chunk[k]);
              if (cfg.roundTripCheck && kd && kd.plain &&
                  cfg.roundTripLanguages.indexOf(target) !== -1 &&
                  chunk[k].length <= cfg.roundTripMaxChars) {
                // Token-bearing short labels are checked too: tokens are STRIPPED
                // from the back-translation input (markers are unreliable through
                // the engine), and polarity is verified on the remaining words.
                // If stripping leaves nothing to verify, the block fails closed.
                var srcStripped = stripTokenPatterns(chunk[k]);
                var backInput = stripTokenPatterns(t);
                rtPending.set(chunk[k], { t: t, backInput: backInput, srcForCheck: srcStripped });
              } else {
                cacheSet(target, chunk[k], t);
                map.set(chunk[k], t);
              }
            }
          }
          applyAll(plan, map, gen, retryQueue, overrideApplied, target);
        });
      });
    }, Promise.resolve()).then(function () {
      if (gen !== generation || superseded) return null;
      if (!rtPending.size) return null;
      // One bounded back-translation batch through the SAME self-hosted
      // endpoint (languages swapped). Detected flips fail closed; a guard
      // error ALSO fails closed (original + partial) - an unchecked short
      // label may be a dangerous opposite-meaning flip, so it is never
      // served unverified.
      var srcs = Array.from(rtPending.keys());
      // blocks whose stripped form carries no verifiable words fail closed up front
      var presplitRejected = 0;
      var verifiable = srcs.filter(function (sc) {
        var e = rtPending.get(sc);
        if (!e.backInput.length || !e.srcForCheck.length) {
          polarityRejected.add(target + "|" + sc);
          presplitRejected++;
          log0(new Error("round-trip check impossible (token-only label), kept original: " + JSON.stringify(sc)));
          return false;
        }
        return true;
      });
      if (presplitRejected) { partialBlocks += presplitRejected; setState("partial"); }
      if (!verifiable.length) { applyAll(plan, map, gen, retryQueue, overrideApplied, target); return null; }
      return fetchBatch(verifiable.map(function (sc) { return rtPending.get(sc).backInput; }), cfg.sourceLanguage, gen, target).then(function (back) {
        if (back === null || gen !== generation) return null;
        var rejected = 0;
        for (var i = 0; i < verifiable.length; i++) {
          var sc0 = verifiable[i], entry = rtPending.get(sc0);
          var b = back[i];
          if (b === TOKEN_FAIL || typeof b !== "string" || !b.length) {
            // unverifiable back-translation: fail closed, keep original
            rejected++;
            log0(new Error("round-trip check unverifiable, kept original: " + JSON.stringify(sc0)));
            continue;
          }
          var flip = polarityFlip(entry.srcForCheck, b) || directionalAmbiguity(entry.srcForCheck, b);
          if (flip) {
            polarityRejected.add(target + "|" + sc0);
            rejected++;
            log0(new Error("opposite-meaning flip kept in original: " + JSON.stringify(sc0) +
                           " round-trips as " + JSON.stringify(b) + " (" + flip[0] + "->" + flip[1] + ")"));
          } else {
            cacheSet(target, sc0, entry.t);
            map.set(sc0, entry.t);
          }
        }
        if (rejected) { partialBlocks += rejected; setState("partial"); }
        applyAll(plan, map, gen, retryQueue, overrideApplied, target);
        return null;
      }).catch(function (err) {
        if (gen !== generation) return null;
        // back-translation batch failed: fail closed on every pending short
        // label (original preserved + partial), never serve unchecked text
        partialBlocks += verifiable.length; setState("partial");
        applyAll(plan, map, gen, retryQueue, overrideApplied, target);
        log0(err);
        return null;
      });
    }).then(function () {
      if (gen !== generation || superseded) return;
      var hasPolarity = false;
      polarityRejected.forEach(function (key) { if (key.indexOf(target + "|") === 0) hasPolarity = true; });
      var hasFailed = false;
      failedAttempts.forEach(function (n, key) { if (n >= cfg.failedBlockMaxAttempts && key.indexOf(target + "|") === 0) hasFailed = true; });
      if (!retryQueue || !retryQueue.length) { setState((partialBlocks > 0 || hasPolarity || hasFailed) ? "partial" : "done"); return; }
      retryFailedSkeletons(retryQueue, target, gen).then(function (stillFailed) {
        if (gen !== generation) return;
        if (stillFailed > 0) {
          partialBlocks += stillFailed;
          setState("partial");
          log0(new Error(stillFailed + " block(s) kept in original language after one marker retry"));
        } else {
          var hp = false;
          polarityRejected.forEach(function (key) { if (key.indexOf(target + "|") === 0) hp = true; });
          setState((partialBlocks > 0 || hp) ? "partial" : "done");
        }
      });
    }).catch(function (err) {
      if (gen === generation && !superseded) setState("error");
      log0(err); // fallback: originals stay in place
    });

    // serialize observer-triggered passes behind this run.
    // inFlight must BE the tracked promise itself: done.finally() returns a
    // NEW promise, so comparing against it never cleared and wedged inFlight.
    inFlight = done;
    done.finally(function () {
      if (inFlight !== done) return; // a newer pass owns the slot
      inFlight = null;
      if (needsPass && gen === generation) {
        needsPass = false;
        translateRoot(generation);
      }
    });
    return done;
  }

  function scanExtraRoots() {
    var extra = [];
    shadowRoots(cfg.root, extra);
    sameOriginFrames(cfg.root, extra);
    return extra;
  }

  function observeRoot(root) {
    if (!cfg.observe || observedRoots.indexOf(root) !== -1) return;
    observedRoots.push(root);
    var obs = new MutationObserver(function (mutations) {
      if (applying) return;
      var relevant = mutations.some(function (m) {
        if (m.type === "characterData") {
          // ignore mutations we caused: value still equals what we applied
          if (weApplied.has(m.target) && m.target.nodeValue === weApplied.get(m.target)) return false;
          return true;
        }
        if (m.type === "childList" && m.addedNodes.length) return true;
        if (m.type === "attributes") {
          if (weApplied.has(m.target) && m.target.getAttribute(m.attributeName) === weApplied.get(m.target)) return false;
          return TEXT_ATTRS.indexOf(m.attributeName) !== -1 || m.attributeName === "placeholder" || m.attributeName === "value";
        }
        return false;
      });
      if (!relevant || !currentLanguage || currentLanguage === cfg.sourceLanguage) return;
      clearTimeout(debounceTimer);
      debounceTimer = setTimeout(function () {
        if (inFlight) { needsPass = true; return; }
        translateRoot(generation);
      }, cfg.debounceMs);
    });
    obs.observe(root, {
      childList: true, subtree: true, characterData: true,
      attributes: true, attributeFilter: TEXT_ATTRS.concat(["placeholder", "value"])
    });
    if (!observer) observer = [];
    observer.push(obs);
  }

  var PixousI18n = {
    init: function (options) {
      cfg = {};
      for (var k in DEFAULTS) cfg[k] = DEFAULTS[k];
      if (options) for (var k2 in options) cfg[k2] = options[k2];
      if (!cfg.root) cfg.root = document.body;
      currentLanguage = cfg.sourceLanguage;
      observeRoot(cfg.root);
      return PixousI18n;
    },

    setLanguage: function (lang) {
      if (!cfg.root) throw new Error("PixousI18n.init() first");
      if (typeof lang !== "string" || !lang.trim()) return Promise.resolve();
      lang = lang.trim();
      if (lang === currentLanguage) return Promise.resolve();
      var prev = currentLanguage;
      currentLanguage = lang;
      if (cfg.onLanguageChange) { try { cfg.onLanguageChange(lang, prev); } catch (e) {} }

      if (lang === cfg.sourceLanguage) {
        restoreOriginals();
        setState(null);
        return Promise.resolve();
      }

      var gen = ++generation;
      partialBlocks = 0;
      if (controller) { controller.abort(); controller = null; }

      // Translate extra roots (open shadow DOM, same-origin iframes) too.
      var roots = [cfg.root].concat(scanExtraRoots());
      var mainRoot = cfg.root;
      var chain = Promise.resolve();
      roots.forEach(function (r) {
        chain = chain.then(function () {
          if (gen !== generation) return;
          var saved = cfg.root; cfg.root = r;
          observeRoot(r);
          var p = translateRoot(gen);
          cfg.root = saved;
          return p;
        });
      });
      cfg.root = mainRoot;
      return chain;
    },

    getLanguage: function () { return currentLanguage; },

    _debugState: function () { // test/diagnostics only
      return { failedAttempts: Array.from(failedAttempts.entries()), polarityRejected: Array.from(polarityRejected) };
    },

    // Call on logout / account switch: drops this tenant's persisted entries.
    clearCache: function () {
      memCache.clear();
      failedAttempts.clear();
      polarityRejected.clear();
      try {
        var doomed = [];
        for (var i = 0; i < global.localStorage.length; i++) {
          var key = global.localStorage.key(i);
          if (key && key.indexOf(cfg.cachePrefix + cfg.cacheVersion + "|" + cfg.tenant + "|") === 0) doomed.push(key);
        }
        doomed.forEach(function (k) { global.localStorage.removeItem(k); });
      } catch (e) { /* ignore */ }
    },

    restore: function () { return PixousI18n.setLanguage(cfg.sourceLanguage); },

    destroy: function () {
      generation++;
      if (controller) controller.abort();
      clearTimeout(debounceTimer);
      (observer || []).forEach(function (o) { o.disconnect(); });
      observer = null; observedRoots = [];
      setState(null);
    }
  };

  global.PixousI18n = PixousI18n;
})(typeof window !== "undefined" ? window : this);
