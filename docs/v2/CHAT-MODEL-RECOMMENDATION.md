# Chat LLM recommendation (researched at primary sources, 2026-10-27)

Decision remains the owner's. No purchase, no deploy, no gate acceptance done by us.

## Recommended default: Qwen3-4B-Instruct-2507 (GGUF via llama.cpp)

Verified at primary sources:
- Model card: https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507
  - API metadata: license:apache-2.0 (permissive, commercial use OK), gated:false,
    revision sha cdbee75f17c01a7cc42f958dc650907174af0554
  - Card claims: 4.0B params, 256K context, multilingual benchmarks on card
    (MultiIF 70.8, MMLU-ProX 65.1, INCLUDE 67.8)
- GGUF build (community quant by bartowski, base weights Apache-2.0):
  https://huggingface.co/bartowski/Qwen_Qwen3-4B-Instruct-2507-GGUF
  - gated:false, revision sha ae44f08e1392f39c0e474af10c3ff8355c8b6688,
    Q4_K_M present (~2.5GB class file)
- Runtime compatibility (PROVEN locally, not estimated):
  - llama-cpp-python 0.3.36 (the exact server already wired to SampleApi)
    registers both `qwen` and `chatml` chat formats - Qwen3 uses a ChatML-style
    template, and llama.cpp applies the template embedded in the GGUF.
  - The OpenAiCompatible provider -> llama-cpp-python server path was proven
    end-to-end with a real reply (SmolLM2-135M wiring fixture). Swapping the
    GGUF file is a config change; provider code unchanged.
  - Caveat (estimated, not proven): this specific Q4_K_M artifact was not loaded
    here - sandbox RAM (2GB) cannot hold a 4B model. First boot on the target
    host is a smoke test to run.

## Alternative: Qwen3-8B (better quality ceiling, heavier)

- https://huggingface.co/Qwen/Qwen3-8B - apache-2.0, gated:false,
  revision b968826d9c46dd6066d109eabc6255188de91218
- Official GGUF: https://huggingface.co/Qwen/Qwen3-8B-GGUF (Q4_K_M ~5GB,
  Q8_0 ~8.5GB), gated:false, revision 7c41481f57cb95916b40956ab2f0b139b296d974
- Card claims "100+ languages and dialects" with multilingual instruction
  following - note: the card does NOT enumerate ta/te/ml/kn individually;
  Indian-language coverage is vendor-claimed at family level, not per-language
  verified. Recommend a small owner-side smoke test with Tamil/Hindi prompts.

## Low-resource option: Phi-4-mini-instruct (3.8B)

- https://huggingface.co/microsoft/Phi-4-mini-instruct - license:mit,
  gated:false, revision cfbefacb99257ffa30c83adab238a50856ac3083

## License caution (verified)

- Gemma 3 and Llama 4 are open-WEIGHT with extra license conditions (Meta
  community license; Gemma terms), not Apache/MIT. Avoid unless legal reviews.

## Host sizing (estimates from vendor/quantization norms, not measured here)

- Qwen3-4B Q4_K_M: ~4GB RAM headroom, comfortable on 8GB RAM CPU-only host.
- Qwen3-8B Q4_K_M: ~6-8GB RAM, 16GB host comfortable; small GPU speeds tokens.
- These are provisioning estimates, not benchmarked numbers.

## Measured local-boot boundary (2026-10-27, 2 GB RAM sandbox, CPU, llama-cpp-python 0.3.36)

- Qwen3-0.6B Q8_0 (official Qwen/Qwen3-0.6B-GGUF, rev 23749fefcc72300e3a2ad315e1317431b06b590a, apache-2.0, gated:false): BOOTS and serves end-to-end through SampleApi at n_ctx 2048. OOM-killed at n_ctx 4096 on the full system prompt (anon-rss 1.82 GB at kill).
- Quality at 0.6B is NOT deployable: EN instruction-following OK, but ta/hi factual answers measured WRONG; empty <think></think> tags leak into replies (strip provider-side or set enable_thinking off).
- Conclusion unchanged: Qwen3-4B-Instruct-2507 remains the recommendation for real use; it does not fit this sandbox. GROUNDED SIZING (2026-10-27): Q4_K_M weights 2.33 GiB (observed x-linked-size 2497280736, bartowski/Qwen_Qwen3-4B-Instruct-2507-GGUF); runtime overhead measured here on 0.6B is ~1.1-1.2 GB on top of weights at n_ctx 4096; 4B KV scales larger (~0.3-0.6 GB) -> ~4-5 GB working set -> practical TOTAL host RAM ~8 GB (ESTIMATE, not measured on a real host). 8B Q4_K_M weights 4.68 GiB (observed x-linked-size 5027784224) -> ~16 GB total (ESTIMATE). QUALITY AT 4B/8B IS UNTESTED LOCALLY (they do not boot in 2 GB): vendor card benchmarks (MultiIF 70.8, INCLUDE 67.8 incl. Indic) are vendor-published, not our measurement - verify on the chosen host with the same battery before production. Host/model purchase remains an owner decision.

## Think-tag handling (added 2026-10-27, second pass)

Qwen3 emits empty `<think></think>` blocks into chat content even with /no_think. Handled provider-side: ThinkTagFilter behind OpenAiCompatibleOptions.StripThinkTags (default true) - streaming-safe (tags split across SSE chunks recognized), legitimate text preserved, unterminated think tails dropped. Unit-tested (12 cases) and verified live through SampleApi (non-streaming + SSE). Disable only if the deployed model never emits think tags and literal "<think>" text must pass through.
