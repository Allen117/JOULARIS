---
target: JOULARIS 首頁
total_score: 21
max_score: 32
na_heuristics: 7
p0_count: 0
p1_count: 3
target_identity: "file:C:\\Users\\A50388.ITRI\\Desktop\\JOULARIS\\src\\pages\\index.astro"
target_fingerprint: "sha256:ff84a8cc88c970dbf3215516f42eb0f0dd7d097721348a7c9887814b622e8d24"
target_path: "C:\\Users\\A50388.ITRI\\Desktop\\JOULARIS\\src\\pages\\index.astro"
timestamp: 2026-09-29T05-22-06Z
slug: src-pages-index-astro
---
Method: dual-agent (A: design review · B: detector + browser)

## Design Health Score: 21/32 (Acceptable, 66%) — n/a: #7
| # | Heuristic | Score | Key Issue |
|---|---|---|---|
| 1 | Visibility of System Status | 2 | nav 無目前區段狀態；mailto 失敗無回饋 |
| 2 | Match System / Real World | 2 | Features 用詞貼近產業，但 TechSpecs 術語與英文 eyebrow 拉遠 |
| 3 | User Control and Freedom | 3 | 錨點與 sticky nav 正常；聯絡卡 preventDefault 無替代 |
| 4 | Consistency and Standards | 3 | Contact h2 尺寸不一；nav 文字與標題不一致 |
| 5 | Error Prevention | 2 | 唯一聯絡管道是 mailto，webmail 使用者卡住 |
| 6 | Recognition Rather Than Recall | 3 | 同一主張在三個區塊重複 |
| 7 | Flexibility and Efficiency | n/a | 單頁 landing page |
| 8 | Aesthetic and Minimalist Design | 2 | 8 個 eyebrow、8 張卡、11 列規格表 |
| 9 | Error Recovery | 1 | mailto 失敗後無地址可複製、無表單、無電話 |
| 10 | Help and Documentation | 3 | FAQ 涵蓋主要疑慮，但答案不連結下一步 |

## Design Specificity Verdict
Mostly category-interchangeable. Product-specific: real EMS screenshots, Taiwan energy vocabulary in Features/FAQ. Template: 8 English eyebrows (detector kicker-above-heading x8), centered section-head everywhere, identical icon card grids (Features 4x2, UseCases 3), gradient text (Hero h1, step-num; detector gradient-text x4, ai-color-palette x7), side-tab WhyUs.astro:47, 01/02/03 steps, blur glow blobs, textbook section order.
Detector: CLI 1 (side-tab WhyUs.astro:47); browser 24 anti-patterns. em-dash-overuse = false positive (Chinese ——).

## Priority Issues
1. [P1] Contact exit fragile, weak pull — Contact.astro:8-58. mailto only, hidden address, h2 smaller, no CTA between hero (8.7%) and TechSpecs (68%); mobile 4.5%→73.5%. Fix: closing band + visible/copyable address + second channel + response promise + mid-page CTAs + FAQ last answer links to #contact. → layout, clarify, harden
2. [P1] No proof/numbers anywhere; Cases hidden. Fix: one quantified pilot result right after Hero with annotated real screenshot. → shape, clarify
3. [P1] Section order + duplication: BrandStory before value; WhyUs repeats Features; TechSpecs (11 rows, jargon, mobile h-scroll) is the main drop-off at 52-62%. Fix: Hero→proof→3 core capabilities→Onboarding→UseCases→FAQ→Contact; merge WhyUs; collapse TechSpecs. → distill, layout
4. [P2] Hero information priority: H1 is brand name only; "不需換硬體、讀既有電表" buried; screenshots unreadable; LogicFlow SCADA shot confuses; primary CTA goes to #features. → clarify, bolder
5. [P2] Template visual language + a11y: remove English eyebrows, gradient text, side-tab; Features as alternating real-UI crops; contrast #7c8d88 3.2:1, btn-primary 3.0:1, no focus-visible. → typeset, audit

## Persona Red Flags
Jordan: brand name H1 + naming story 2nd; 8 equal cards; lost in TechSpecs jargon; never sees a saving number.
Riley: contact needs JS + mail client; 4 vague AI claims; "—" cells look unfinished; EMS/SCADA colour clash.
Casey: 9007px (~10.7 screens); Features 2078px; spec table h-scroll cut; no sticky contact CTA on mobile.

## Minor Observations
Tagline repeated 3x; nav skips Onboarding; BgMotion blur(60px) paint cost; Footer --ink-1 undefined; dark mode hero shots glare; 3 svg without aria-hidden.

## Questions to Consider
- 只能說一個數字，會是什麼？為什麼不在第一屏？
- 「不用換硬體」為什麼是第五張卡而不是標題？
- TechSpecs 是寫給決策者還是導入者？
- 拿掉所有標題後，內容本身還能認出是 JOULARIS 嗎？
