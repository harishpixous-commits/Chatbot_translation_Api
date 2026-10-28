# 30 directed-pair evaluation — FLORES-200 dev, human references (2026-10-27)

Engine: IndicTrans2 dist-200M CT2 ports (adalat-ai), CPU int8, official AI4Bharat
inference engine. Corpus: FLORES-200 **dev** split, first 50 sentences per language,
official public download (https://dl.fbaipublicfiles.com/nllb/flores200_dataset.tar.gz
via https://github.com/facebookresearch/flores — no gated-license acceptance needed).
Metric: sacrebleu corpus chrF against the official human references. n=50 per pair
(subsample of 997; full-set numbers will differ). chrF is an automatic metric, not
native-speaker acceptance.

**Measured on this corpus: direct en-paired pairs avg chrF 63.9
(range 57.7-69.8); English-pivot Indic-Indic pairs avg chrF
54.0 (range 49.6-58.7).** The pivot path scores
measurably lower on identical sentences — this is a measurement on 50 sentences, not
a universal quality law.

| pair | pivot | n | chrF |
|---|---|---|---|
| en->hi | no | 50 | 60.4 |
| en->kn | no | 50 | 57.7 |
| en->ml | no | 50 | 60.0 |
| en->ta | no | 50 | 62.5 |
| en->te | no | 50 | 66.1 |
| hi->en | no | 50 | 67.5 |
| hi->kn | yes (via English) | 50 | 51.6 |
| hi->ml | yes (via English) | 50 | 54.1 |
| hi->ta | yes (via English) | 50 | 56.2 |
| hi->te | yes (via English) | 50 | 56.9 |
| kn->en | no | 50 | 64.1 |
| kn->hi | yes (via English) | 50 | 51.0 |
| kn->ml | yes (via English) | 50 | 53.4 |
| kn->ta | yes (via English) | 50 | 55.1 |
| kn->te | yes (via English) | 50 | 55.8 |
| ml->en | no | 50 | 68.6 |
| ml->hi | yes (via English) | 50 | 52.2 |
| ml->kn | yes (via English) | 50 | 52.7 |
| ml->ta | yes (via English) | 50 | 57.7 |
| ml->te | yes (via English) | 50 | 58.7 |
| ta->en | no | 50 | 62.0 |
| ta->hi | yes (via English) | 50 | 49.9 |
| ta->kn | yes (via English) | 50 | 49.6 |
| ta->ml | yes (via English) | 50 | 52.1 |
| ta->te | yes (via English) | 50 | 55.4 |
| te->en | no | 50 | 69.8 |
| te->hi | yes (via English) | 50 | 53.1 |
| te->kn | yes (via English) | 50 | 53.4 |
| te->ml | yes (via English) | 50 | 55.6 |
| te->ta | yes (via English) | 50 | 56.3 |
