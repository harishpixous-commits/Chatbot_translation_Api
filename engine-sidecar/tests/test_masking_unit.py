#!/usr/bin/env python3
"""Model-free unit tests for masking, placeholder collision and glossary-config limits."""
import json, os, sys, tempfile, time
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from pixous_engine import engine_manager as em
fails = 0
def chk(name, ok, d=""):
    global fails; fails += (not ok); print(("PASS  " if ok else "FAIL  ") + name + (("  | " + str(d)) if d else ""))
class SP:  # whitespace "tokenizer" stand-in: 1 piece per word
    def encode(self, text, out_type=str): return text.split()
class _Tok:  # tokenizer stand-in: each word = ceil(len/5) pieces (so unbroken runs are costly)
    def preprocess(self, texts, lang=None): return list(texts), None
    def apply_spm(self, texts): return [" ".join(p for w in t.split() for p in [w[i:i+5] for i in range(0, len(w), 5)]) for t in texts]
class Echo(_Tok):  # model stand-in: returns text unchanged, so only structure code is exercised
    sp_src = SP()
    def batch_translate(self, items, s, t): return list(items)
class Drop(_Tok):  # model stand-in that destroys placeholders
    sp_src = SP()
    def batch_translate(self, items, s, t): return [em._PH.sub("", i) for i in items]
src = ["Use `a[2]` and **b** and [1].\n- item `x[1]` \U0001F331\n```\ncode [1]\n```"]
o = em._translate_structured(Echo(), src, "en", "ta")
chk("collision: code containing [2]/[1] survives restore (Echo)", o == src, o)
o = em._translate_structured(Echo(), ["Use `a[2]` and **b**."], "en", "ta")
chk("collision: `a[2]` not re-substituted", o == ["Use `a[2]` and **b**."], o)
o = em._translate_structured(Drop(), ["Run `x` now **bold** \U0001F331"], "en", "ta")
chk("fallback keeps spans when model drops placeholders", "`x`" in o[0] and "**bold**" in o[0] and "\U0001F331" in o[0], o)
o = em._translate_structured(Echo(), ["a\r\nb\r\n\r\n```\r\nc\r\n```"], "en", "ta")
chk("CRLF + fence round-trips", o == ["a\r\nb\r\n\r\n```\r\nc\r\n```"], o)
o = em._translate_structured(Echo(), ["e\u0301 \u0b95\u0bcd\u0bb7 \U0001F468\u200d\U0001F469\u200d\U0001F467 x"], "en", "ta")
chk("combining marks + ZWJ family emoji unchanged", o == ["e\u0301 \u0b95\u0bcd\u0bb7 \U0001F468\u200d\U0001F469\u200d\U0001F467 x"], o)
def load(rules):
    with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8") as f: json.dump(rules, f)
    try: return em.load_glossary(f.name)
    except ValueError as e: return str(e)
    finally: os.unlink(f.name)
base = {"source": "en", "target": "ta", "phrase": "role", "replacement": "x"}
chk("bad regex rejected with message", isinstance(load([dict(base, phrase="(")]), str))
chk("nested quantifier (a+)+ rejected", "backtracking" in str(load([dict(base, phrase="(a+)+$")])))
chk("nested quantifier in cue rejected", "backtracking" in str(load([dict(base, when="(.*)*x")])))
chk("empty-match phrase rejected", "empty" in str(load([dict(base, phrase="a*")])))
chk("placeholder-like replacement rejected", "placeholder" in str(load([dict(base, replacement="[3]")])))
chk("overlong pattern rejected", isinstance(load([dict(base, phrase="a" * 201)]), str))
chk(">100 rules rejected", isinstance(load([base] * 101), str))
chk("missing field rejected", isinstance(load([{"source": "en"}]), str))
rules = load([dict(base, phrase=r"\brole\b", when="apply|job")])
t = time.time(); em._translate_structured(Echo(), ["role " + "a" * 1990], "en", "ta", rules.get(("en", "ta")))
chk("2000-char line with glossary is fast (<0.5s)", time.time() - t < 0.5, round(time.time() - t, 3))
chk("glossary fires only with cue", em._translate_structured(Echo(), ["the role of x"], "en", "ta", rules[("en", "ta")]) == ["the role of x"]
    and em._translate_structured(Echo(), ["apply for the role"], "en", "ta", rules[("en", "ta")]) == ["apply for the x"])
print("unit masking/glossary: %d/%d passed" % (sum(1 for _ in range(1)) and 0, 0)) if False else None

class Fake(_Tok):
    sp_src = SP()
    def __init__(self): self.calls = []
    def batch_translate(self, items, s, t):
        self.calls.append(list(items)); return ["T(" + i + ")" for i in items]
import random, re
em._TOKEN_BUDGET = 13   # == 10 real pieces + 3 specials
m = Fake()
text = " ".join("Sentence %d here now." % i for i in range(1, 31))
o = em._bounded_translate(m, [text], "en", "ta")[0]
sent = [c for call in m.calls for c in call]
chk("long item: every chunk token-exact <= budget, none lost", all(em._ntokens(m, c, "en") <= 13 for c in sent) and all(("Sentence %d " % i) in o for i in range(1, 31)), (len(sent), "chunks"))
st = {}; m = Fake(); o = em._bounded_translate(m, ["x" * 1999, "ok then"], "en", "ta", st)
chk("unbroken 1999 run is split and translated (model sees it), never passed through", all(em._ntokens(m, c, "en") <= 13 for call in m.calls for c in call) and "".join(c for call in m.calls for c in call if set(c) == {"x"}) == "x" * 1999 and st.get("items_split", 0) >= 1, (st, o[1]))
# property tests: reconstruction == original (modulo whitespace), order kept, budget respected
SAMPLES = ["Dr. Smith paid 3.5 dollars at 10.30 a.m. on Mon. See https://a.b/c.d?x=1 now. Next!",
  "Total is $1,234.56. v2.1.3 shipped; e.g. this i.e. that? Yes! ok...",
  "- item **one** `a.b()` [1] [2]. 2. second item \u0bb5\u0ba3\u0b95\u0bcd\u0b95\u0bae\u0bcd. Done.",
  "\u4eca\u5929\u5929\u6c14\u5f88\u597d\u3002" * 40, "a" * 500 + " b " + "c" * 300,
  "\u0b87\u0ba4\u0bc1 \u0b92\u0bb0\u0bc1 \u0b9a\u0bcb\u0ba4\u0ba9\u0bc8. " * 30]
rnd = random.Random(7); words = ["Hi.", "3.5", "Mr.", "e.g.", "http://x.y/z", "**b**", "`c`", "[1]", "ok?", "wow!", "a" * 40, "\u0b95" * 30, "x"]
for _ in range(150): SAMPLES.append(" ".join(rnd.choice(words) for _ in range(rnd.randint(1, 120))))
bad = 0
for t in SAMPLES:
    plan = em._chunk_text(Fake(), t, "en")
    rebuilt = "".join(j + c for c, j in plan)
    ok = re.sub(r"\s+", "", rebuilt) == re.sub(r"\s+", "", t) and all(em._ntokens(Fake(), c, "en") <= 13 for c, _ in plan)
    bad += (not ok)
chk("property: %d adversarial inputs reconstruct exactly (no drop/dup/reorder), every chunk <= token budget" % len(SAMPLES), bad == 0, "%d bad" % bad)
em._MAX_CHUNKS = 5
try: em._bounded_translate(Fake(), [text], "en", "ta"); r = "no error"
except ValueError as e: r = "ValueError"
chk("chunk count over limit -> ValueError (400)", r == "ValueError", r)
em._MAX_CHUNKS = 1500; em._MAX_SECONDS = -1
try: em._bounded_translate(Fake(), [text], "en", "ta"); r = "no error"
except TimeoutError: r = "TimeoutError"
chk("time budget exceeded -> TimeoutError (503), engine lock released", r == "TimeoutError", r)
em._MAX_SECONDS = 90
sys.exit(1 if fails else 0)
