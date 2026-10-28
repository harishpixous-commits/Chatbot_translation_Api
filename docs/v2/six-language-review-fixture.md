# Six-language review fixture (bounded, for targeted human review)

Purpose: 10 representative UI strings translated en -> ta/hi/te/ml/kn by the ACTUAL
self-hosted engine (IndicTrans2 en-indic dist-200M, CT2 int8, local sidecar, no external
calls). Raw machine-readable outputs: six-language-outputs.json (same folder).
Captured 2026-10-27. Reviewer needs only the target language(s) they speak; each row
stands alone. This is a REVIEW FIXTURE, not a quality acceptance: semantic sign-off
remains with native speakers. Automated checks are heuristic only (see notes).

## Strings and outputs

| # | English source | Intent / expected meaning |
|---|----------------|---------------------------|
| 1 | Home | UI nav label: main page (NOT the building) |
| 2 | Settings | UI label: preferences page |
| 3 | Sign out | Log out of the account |
| 4 | Search results | Heading above result list |
| 5 | Welcome to our website. | Greeting sentence |
| 6 | Your order has been shipped and will arrive on {{date}}. | Status; {{date}} is a template variable and MUST stay verbatim |
| 7 | Please enter a valid email address. | Form validation error |
| 8 | Click <0>here</0> to download the report. | "here" is a link; the tag pair must survive, content between tags = link text |
| 9 | Pixous Assistant helps you translate your website. | Product name should stay recognizable |
| 10 | Are you sure you want to delete 3 items? | Confirmation dialog; number must survive |

(Full per-language outputs in six-language-outputs.json; review columns below.)

## Issues already visible to a non-native reviewer (flag for confirmation)

- #3 "Sign out" -> ta output reads as "உள்நுழையுங்கள்" which appears to mean
  "Sign IN" (opposite meaning). kn output "ಸಹಿ ಮಾಡಿ" appears to mean "sign" as in
  signature. HIGH priority review item: short imperative UI labels without context.
- #1 "Home" -> ta "வீடு" appears to be the building, not the UI home page
  (UI convention would be முகப்பு). te/ml transliterate. Context-less short-string
  weakness; fix path = per-string glossary/context overrides, not engine swap.
- #6 te output contained "{{தேদీ}}" and kn "{{দিনాంಕ}}": the engine TRANSLATED the
  template variable (raw unprotected engine behavior, kept here for the record).
  FIXED in the SDK (2026-10-27): in-text interpolation tokens are protected
  verbatim end-to-end with strict validation and fail-closed preserve;
  real-engine regression confirms te/kn now render {{date}} intact. The raw
  outputs in six-language-outputs.json still show the unprotected defect for
  reviewer context.
- #8 link tag pairs survived all five languages (reordered but paired and
  balanced). The SDK's own randomized markers are stricter than these raw tags
  and are validated for exact sequence/order.

## Privacy / provenance notes

- All outputs generated locally on CPU (int8 CT2), no text left the machine;
  no external provider, no telemetry, no per-call cost.
- Engine provenance: AI4Bharat IndicTrans2 (MIT); CT2 ports adalat-ai rev
  ee04de9a (en-indic) / 524aade4 (indic-en) as previously verified.
- Benchmark context (separate evidence): FLORES-200 chrF direct avg 63.9,
  pivot 54.0 (n=50/lang) - aggregate only, does not replace this review.

## How to review

For each language you read: mark each row OK / WRONG MEANING / AWKWARD /
FORMAT BROKEN. Rows 1, 3, 6 (te, kn) are the priority checks. Return the
marked table; no need to re-translate everything.

## Whole-site SDK verification (2026-10-27, evidence/ folder)

Real engine, real headless Chromium, full demo page per language: hi, te, ml, kn
(+ ta previously). All four: state=done; nav + h1 + body translated in the correct
script; every excluded zone untouched (data-no-translate brand, translate=no,
.no-translate class, <pre> code, textarea draft, password placeholder). Fonts:
full Noto families installed for all five scripts, so screenshots render real
glyphs (not tofu). Screenshots: evidence/shot-{hi,te,ml,kn}.png (+ shot-tamil-final.png).
Note for reviewer: kn nav uses ಮುಖಪುಟ (proper UI "home") while ta uses வீடு
(literal "house") - consistent with the #1 context-less-label weakness already flagged.

Fixture limitation (intentional): the company-logo <img> uses a 1x1 transparent
data-URI placeholder, so it renders as a broken/empty image in every screenshot.
That is a demo-fixture property, not a translation regression; its alt text
("Company logo") is part of the translatable-attribute coverage.

## Addendum 2026-10-27 (evening): same fixture with the SDK polarity guard LIVE

six-language-guard-outcomes.json (same folder) records the DOM outcome per string
with roundTripCheck + markerRetry at their shipped defaults, real engine:

- ta: state=partial - "Sign out" is PRESERVED IN ENGLISH by the round-trip
  polarity guard (live interception of issue #3); every other string applied,
  {{date}} verbatim.
- hi/te/ml/kn: state=done - no guard interceptions; {{date}} verbatim in all.
- kn #3 "ಸಹಿ ಮಾಡಿ" was APPLIED in the first guard run: the back-translation
  ("Sign it") kept the "sign" stem but dropped the direction word, so no
  opposite polarity pair matched. FIXED (2026-10-27, later same day): the
  guard was extended with a bounded directional-label check (sign/log stems
  must keep a same-direction word; a bare stem with no direction word fails
  closed). Re-measured on the real engine: kn "Sign out" is now
  PRESERVED-ORIGINAL, state=partial; ta unchanged; hi/te/ml applied with zero
  new false flags (six-language-guard-outcomes.json, refreshed). The table
  remains a bounded heuristic, not semantic verification: direction words
  outside it are not caught, and native review stays the acceptance path.
