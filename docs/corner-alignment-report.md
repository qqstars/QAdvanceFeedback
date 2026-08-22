# Corner-Alignment Consistency Report: 1.0.6.5 – 1.0.6.8

**Scope:** analysis only. No source file under any `QAdvanceFeedback*` folder was modified. All
scripts used to produce the numbers below live under
`C:\Development\Repos\Samples\simhub\QAdvanceFeedback\scratchpad\corner-align\` (`common.py`,
`segment.py`, `build_table.py`, `task2.py`/`task2b.py`, `task3.py`, `task4.py`,
`pooled_check.py`, `rr_check.py`). Raw intermediate tables (`corner_table_raw.csv`,
`corner_validation.csv`, `task2_table.csv`, `task2b_table.csv`, `task3_table.csv`,
`task4_table.csv`) are in the same folder. Data source: the eight CSVs and four
`QAdvanceFeedback.Parameters.json` files under `C:\Development\Repos\Samples\simhub\1.0.6_logs\`.
`QAdvanceFeedback_1.0.6.2_prerelease` was not touched. No git operations were performed.

---

## 0. Methodology and assumptions (read this before the tables)

**Event segmentation rule.** A frame belongs to a braking event if `Diag.Direction == "Slowing"`.
Contiguous Slowing runs separated by a non-Slowing gap **shorter than 0.30 s** are merged into one
event (handles brief telemetry flicker mid-corner). A merged event is kept only if its duration is
**≥ 0.35 s** *and* its peak `Diag.Telemetry.BrakePercent` is **≥ 15 %** (drops lift-offs and
noise). This yielded 9–10 raw candidate events per log.

**Corner-exit window for Slip.** `WheelSlip.Normalized.All` is essentially always zero during the
Slowing (braking) window — verified directly: on 1.0.6.5 Raw, every one of the 3,111 frames with
`WheelSlip.Normalized.All > 5` occurs on a `SpeedingUp` frame, none on `Slowing`. Slip is a
throttle/wheelspin phenomenon on corner exit, not a braking phenomenon. So for the Slip channel I
define each corner's window as the `SpeedingUp`-direction frames between the end of that corner's
braking event and the start of the next one (corner-exit acceleration zone), and use that window
everywhere Slip is evaluated in Tasks 2–4. Lock uses the braking (Slowing) window throughout. I
independently re-derived the brief's pooled "known context" percentiles using exactly this
Slowing/SpeedingUp split and reproduced them almost exactly (e.g. Lock ShakeIt p90 75.5–80.8,
Raw p90 73.3–79.8; Slip Raw p90 20.4–34.3, ShakeIt p90 17.4–30.4) — this confirms the window
definition is the correct one and that the cited pooled figures are accurate.

**1.0.6.8 Raw is not one lap — it is two, joined by a mid-recording restart.** This log is 183.8 s
long versus 112.9–116.1 s for every other log, with 15 raw candidate events instead of 9–10.
Direct inspection of the frames (`t_s` 52–65 s) shows: the car brakes normally through corner 6,
decelerates to a complete stop (speed **3–4 km/h, held for ~4 s**, `Diag.Direction` goes
`Unknown`), then speed **teleports from 3 km/h to 329 km/h in a single frame** at `t≈60.9 s` —
the signature of a mid-session restart-to-grid, not a spin (a spin does not produce an
instantaneous 300+ km/h jump). Everything from that teleport onward (events 7–15 in raw
numbering) is a second, complete pass through the same 9-corner sequence. **I used only this
second pass ("lap B", relabeled corners 1–9) for 1.0.6.8 Raw** and discarded the first, aborted
pass (events 1–6, only 6 of 9 corners present). This is a recording artifact, not a code
behaviour, but it has a real consequence noted in Task 4: the Slip learner's `SourceScaleCeiling`
in lap B sits flat at the file's cold value (23.28) the entire time, and the Slip *source itself*
is near-zero at every corner in lap B — 1.0.6.8 Raw's Slip numbers should be read as
"possibly still cold after the restart," not as a clean like-for-like sample.

**`Diag.Lock.SourceFallbackActive`.** Pooled across each full log: 0.00 % in all four Raw-mode
logs, 0.53–2.90 % in ShakeIt-mode logs — consistent with the brief's cited 1.9–4.7 % (that figure
is evidently Lock-only). **`Diag.Slip.SourceFallbackActive` is a different story and is not
mentioned in the brief's cited range**: 0.00 % in every Raw-mode log, but **58.2–68.2 % in every
ShakeIt-mode log** (1.0.6.5: 58.2 %, 1.0.6.6: 59.5 %, 1.0.6.7: 68.2 %, 1.0.6.8: 66.5 %). This is a
real, large, previously-unflagged channel asymmetry — see §5.

**Differing log lengths.** Aside from the 1.0.6.8 Raw restart, lap lengths differ by at most ~3 s
(112.9–116.1 s) — normal lap-to-lap variance, not truncation. No other log needed special
handling.

**Frame-count floor for percentiles.** Any corner/channel/log cell with fewer than 30 window
frames is flagged rather than trusted for a percentile. Only one cell in the full analysis falls
this low: 1.0.6.7 ShakeIt, corner 9, Lock window, **n = 37 frames** (the final chicane's braking
zone was segmented as a short 0.6 s event in this log only — see corner 9 validation below). One
further cell (1.0.6.7 ShakeIt, corner 9, Slip exit window) produced **zero** frames because the
very next braking event starts almost immediately after corner 9 in that log, leaving no
`SpeedingUp` frames to sample — reported as n/a, not estimated.

---

## Task 1 — Corner alignment

Of 9 candidate corner indices, **8 aligned successfully across all 8 logs**; corner 6 failed
validation and was excluded. A further, inconsistently-segmented 10th braking zone (present in
only 4 of 8 logs) was not treated as an aligned corner at all (see below).

**Validation table** (entry speed / peak G / peak brake% / duration, n = 8 logs per row unless noted):

| Corner | n | Entry speed km/h (min–max, CV%) | Peak G (min–max, CV%) | Peak brake% (min–max, CV%) | Duration s (min–max, CV%) | Verdict |
|---|---|---|---|---|---|---|
| 1 | 8 | 329–329 (0.0%) | 3.81–3.94 (1.4%) | 77.6–83.8 (3.3%) | 1.63–1.77 (3.4%) | **Aligned** |
| 2 | 8 | 285–286 (0.2%) | 4.14–4.62 (3.8%) | 100–100 (0.0%) | 2.65–3.00 (4.7%) | **Aligned** |
| 3 | 8 | 290–295 (0.7%) | 4.08–4.31 (2.1%) | 100–100 (0.0%) | 2.18–3.33 (15.2%) | **Aligned** |
| 4 | 8 | 143–163 (4.1%) | 2.61–3.03 (4.6%) | 94.8–100 (1.9%) | 1.30–2.13 (17.8%) | **Aligned** |
| 5 | 8 | 318–322 (0.4%) | 4.36–4.47 (0.7%) | 100–100 (0.0%) | 1.85–3.07 (19.6%) | **Aligned** |
| **6** | 8 | 161–190 (5.0%) | **1.49–3.53 (21.8%)** | **30.9–100 (27.6%)** | 1.20–2.57 (25.4%) | **FAILED — excluded** |
| 7 | 8 | 254–288 (4.3%) | 3.93–4.20 (2.4%) | 100–100 (0.0%) | 1.20–2.71 (29.7%) | **Aligned** |
| 8 | 8 | 325–327 (0.3%) | 4.37–4.58 (1.6%) | 100–100 (0.0%) | 1.48–2.17 (13.8%) | **Aligned** |
| 9 | 8 | 277–286 (1.2%) | 4.06–4.39 (2.7%) | 100–100 (0.0%) | 0.60–2.73 (30.6%) | **Aligned*** |

\*Corner 9's duration CV is inflated by 1.0.6.7 ShakeIt splitting into a short (0.6 s, 37-frame)
piece followed by a separate event — entry speed and peak G still agree tightly (CV 1.2%/2.7%),
so the corner itself is real and aligned; only that one cell's percentile should be discounted for
low n (flagged throughout Task 3).

**Why corner 6 fails, and why it is genuine lap variance, not a segmentation bug:** I inspected the
raw frames for the worst outlier (1.0.6.5 ShakeIt, peak G 1.49 vs 3.0–3.5 elsewhere, peak brake
30.9% vs 100%). The car does slow through the expected speed range (175→117 km/h) but under an
almost brake-free coast-down — brake pressure touches 30% only briefly, then drops to 0% for the
rest of the event. This is a real, lighter brake application that lap, exactly the kind of
lap-to-lap variation the brief warns must never be attributed to code. 1.0.6.8 Raw's corner 6
(entry 161 km/h vs 175–190 elsewhere) is a second, milder outlier, plausibly a residual effect of
driving cautiously right after the mid-lap restart. **Corner 6 is excluded from Tasks 2–4.**

**The "10th event."** Four of eight logs (1.0.6.5 Raw/ShakeIt, 1.0.6.6 ShakeIt, 1.0.6.7 ShakeIt)
produced an extra braking event after corner 9, at entry speeds ranging 158–270 km/h — too
inconsistent (both in presence and in entry speed) to be one repeatable corner. It reads as a
final tight complex where some laps carry speed further before a second brake application and
some don't. It is **not** counted as an aligned corner and is excluded from every table below.

**Result: 8 of 9 candidate corners aligned (89%); corner 6 excluded.**

---

## Task 2 — Per-corner max-grip consistency (1.0.6.5 vs 1.0.6.6, then 1.0.6.7/1.0.6.8)

For each aligned corner I took the frame of peak `Diag.MotionMagnitudeG` within the channel's
window and read the source, ceiling, and mapped output there, plus the window-max of
`Normalized.All`/`Projected.All` as a more timing-robust cross-check (peak Lock/Slip does not
always land on the exact same 16 ms frame as peak car-level G, so single-frame comparisons are
noisier than window-max comparisons; both are reported for transparency, and the finding below
uses window-max as the primary metric).

### 1.0.6.5 vs 1.0.6.6 — do they agree at max grip? (Lock, `Normalized.All` window-max)

| Corner | Raw 1.0.6.5 | Raw 1.0.6.6 | Δ | ShakeIt 1.0.6.5 | ShakeIt 1.0.6.6 | Δ |
|---|---|---|---|---|---|---|
| 1 | 94.0 | 4.1 | −89.9 † | 77.8 | 32.0 | −45.8 † |
| 2 | 99.8 | 81.9 | −17.8 | 100.0 | 100.0 | 0.0 |
| 3 | 80.7 | 80.6 | −0.1 | 99.5 | 84.7 | −14.8 |
| 4 | 83.7 | 83.5 | −0.1 | 94.1 | 100.0 | +5.9 |
| 5 | 80.6 | 73.5 | −7.1 | 90.5 | 82.7 | −7.8 |
| 7 | 41.4 | 70.3 | **+28.9** | 82.6 | 77.0 | −5.7 |
| 8 | 78.0 | 80.5 | +2.5 | 80.4 | 78.9 | −1.5 |
| 9 | 85.9 | 83.1 | −2.8 | 99.4 | 90.4 | −9.0 |
| **mean \|Δ\|, corners 2–9** | | | **8.5** | | | **6.4** |

† Corner 1 is the weakest braking event on the lap (peak G 3.8–3.9, near the low end of the
learner's trigger threshold) and both output values are small/noisy in absolute terms; treat it
as low-confidence rather than a real disagreement.

**Answer: yes, 1.0.6.5 and 1.0.6.6 agree at max grip for Lock, at 6 of 7 non-trivial corners** —
typical spread ~6–9 points on a 0–100 scale, consistent with normal frame-to-frame/lap-to-lap
jitter around a peak that is a genuine physical spike. **One clear outlier: corner 7 Raw
(41.4 vs 70.3, Δ = 28.9).** I checked whether this traces to the ceiling — it does not:
`Diag.Lock.SourceScaleCeiling` at that frame is 72.2 (1.0.6.5) vs 71.0 (1.0.6.6), essentially
identical. The divergence is in the raw source signal itself at that specific corner/lap, which
given the near-identical ceiling is most consistent with ordinary lap-to-lap variation in exactly
where/how hard the driver trail-braked that corner, not a version regression. **This confirms the
owner's architectural claim** (speed-aware logic added in 1.0.6.6 gates the *learning* stage only,
not per-frame `severity`) for Lock.

### 1.0.6.5 vs 1.0.6.6 — Slip (`Normalized.All` window-max, corner-exit window)

| Corner | Raw 1.0.6.5 | Raw 1.0.6.6 | Δ | ShakeIt 1.0.6.5 | ShakeIt 1.0.6.6 | Δ |
|---|---|---|---|---|---|---|
| 1 | 70.0 | 15.2 | −54.8 | 70.0 | 70.0 | 0.0 |
| 2 | 70.0 | 70.0 | 0.0 | 70.0 | 70.0 | 0.0 |
| 3 | 70.0 | 42.0 | −28.0 | 58.6 | 48.3 | −10.3 |
| 4 | 70.0 | 49.3 | −20.7 | 70.0 | 64.3 | −5.7 |
| 5 | 70.0 | 36.4 | −33.6 | 61.9 | 58.2 | −3.6 |
| 7 | 70.0 | 21.9 | **−48.1** | 64.6 | 28.9 | **−35.8** |
| 8 | 70.0 | 29.2 | **−40.8** | 62.4 | 31.7 | **−30.7** |
| 9 | 70.0 | 70.0 | 0.0 | 70.0 | 70.0 | 0.0 |

**Slip does *not* agree between 1.0.6.5 and 1.0.6.6** — 1.0.6.6 Raw is markedly weaker at 5 of 8
corners (deficits 20–55 points), 1.0.6.6 ShakeIt weaker at 3 of 8 (10–36 points). This exactly
matches the owner's own guide note: *"1.0.6.6: Using Raw, generally good as the same as 1.0.6.5.
But for the wheelSlip, still softer and later compared to using ShakeIt."* I traced the Raw-mode
gap to its source: `Diag.Slip.SourceScaleCeiling` is nearly identical between the two versions at
every one of these corners (e.g. corner 7: 64.9 vs 68.8; corner 8: 64.8 vs 68.6 — within 4 points),
which rules out the ceiling. The **raw telemetry-derived rear-group source itself** is genuinely
weaker in 1.0.6.6 at the same corners (corner 1: 97.2 vs 20.2; corner 4: 100.0 vs 63.0; corner 7:
59.4 vs 24.2; corner 8: 58.0 vs 31.7). Per the brief's own rule, a difference this concentrated in
the raw, telemetry-level source — with matching ceilings — is most honestly read as **lap-to-lap
variation in exactly how much throttle-induced wheelspin the driver produced exiting those
corners on that particular run**, not a Slip-pipeline regression in 1.0.6.6. Slip's source is a
throttle-timing-sensitive signal in a way braking G is not, so it is intrinsically noisier across
nominally-identical laps. I flag this rather than either confirm or attribute it to code with more
certainty than the evidence supports.

### 1.0.6.7 / 1.0.6.8 divergence — attribution (Lock)

| Corner | 1.0.6.7 Raw src@peak | 1.0.6.7 Raw ceiling | 1.0.6.7 Raw Norm-max | 1.0.6.8 Raw src@peak | 1.0.6.8 Raw ceiling | 1.0.6.8 Raw Norm-max |
|---|---|---|---|---|---|---|
| 2 | 76.2 | 77.6 | 54.5 | 4.0 | 52.1 | 33.7 |
| 3 | 4.5 | 78.2 | 54.8 | 2.5 | 52.1 | 31.9 |
| 4 | 15.9 | 76.0 | 33.1 | 12.6 | 49.3 | **36.5** |
| 5 | 3.1 | 76.0 | 74.6 | 4.6 | 49.3 | **36.5** |
| 7 | 6.5 | 74.0 | 74.0 | 16.2 | 48.8 | **36.5** |
| 8 | 3.9 | 73.5 | 74.2 | 4.2 | 46.3 | **36.5** |
| 9 | 4.1 | 43.3 | 65.1 | 2.4 | 43.3 | 39.8 |

**This is the crux, and it attributes to both the ceiling and the mapping, not the source.**
`Diag.Lock.SourceScaleCeiling` for 1.0.6.8 sits at 43–52 across every corner, well below 1.0.6.7's
73–78 (corners 2–8) — a genuine, consistent, ~25–30-point ceiling gap that would by itself
compress 1.0.6.8's output range. But the more striking signal is the **window-max output itself:
1.0.6.8 Raw lands on the *exact same value, 36.473552, at corners 4, 5, 7 and 8*** — four
physically different corners (entry speeds 151–320 km/h, peak G 2.9–4.5) producing a bit-identical
output. I confirmed this is not a segmentation artifact by dumping the full per-frame trace inside
each of those windows: the values leading up to the peak are all distinct (43, 79, 90+ unique
values per window), and only the *window maximum* repeatedly lands on 36.473552 — i.e. something
in the mapping is clamping the achievable ceiling of the output for this build to a value near 36,
well short of the 70–100 that other versions reach. This is consistent with guide.txt's own
1.0.6.8 complaint ("the Lock motor not shaking at all") and with the owner's separate request to
build wide flat "platform" regions into the curve around threshold points — a platform that wide
would produce exactly this signature (many different raw inputs landing in the same flat output
band). **Attribution: partly the learned ceiling (real, ~25–30 points lower in 1.0.6.8), and
additionally a mapping-stage ceiling/plateau effect that caps the achievable output well below
100 regardless of input** — the source values themselves (2–16 at peak-G-frame) are not
obviously more depressed in 1.0.6.8 than in 1.0.6.7's own noisy peak-G-frame source readings, so
source is not the primary driver here.

### Does the learned ceiling differ between versions for the same corner?

Yes, clearly, and it is corner- and channel-dependent:

- **Lock**: 1.0.6.5/1.0.6.6/1.0.6.7 cluster at 70–80 for corners 2–8, dropping to 28–44 at corner 9
  (see below) in every version — a genuine within-lap convergence effect (see next paragraph), not
  a cross-version difference. **1.0.6.8 is the outlier**: 43–52 throughout, materially lower than
  the other three versions at every corner. This ceiling gap is real and contributes to (but does
  not fully explain) 1.0.6.8's output collapse.
- **Slip**: 1.0.6.5 vs 1.0.6.6 Raw ceilings are close (within 1–5 points) at corners 2–8, so ceiling
  is *not* what explains 1.0.6.6's weaker Slip output there (see above) — except at corner 9,
  where 1.0.6.5 shows an anomalous 16.8 against 1.0.6.6's 67.6.

**Important caveat that applies to every ceiling number in this report:** each version's
`Parameters.json` was generated **fresh** for that run (confirmed: `LockLearners`/`SlipLearners`
sample counts are in the low hundreds, consistent with a single lap's worth of observations). The
scale learner is *converging live, during the very lap being analysed* — this is why ceilings are
consistently high (70–80) at corners 2–8 and drop to 28–44 at corner 9 in nearly every log: by
corner 9 the learner has accumulated enough peak-G observations to correct its estimate downward.
**Comparing ceiling-by-corner-index across versions therefore conflates two effects: genuine
version behaviour, and "how far into its own cold-start convergence this particular lap happened
to be by this corner."** 1.0.6.8's ceiling gap is large enough (25–30 points, present at every
corner including the earliest ones) that convergence timing alone cannot explain it, but this
caveat should be kept in mind for smaller, corner-specific ceiling deltas elsewhere in this report.

Both channels (WheelLock, WheelSlip) were run through this analysis; full per-corner tables for
both are in `task2b_table.csv`.

---

## Task 3 — Same-lap source comparison (comparison type (a): same-lap, source-vs-source)

Per aligned corner, on every log, `Diag.Source.{chan}.All` (the actually-selected source) is
compared against `Wheel{chan}.Raw.All` (our Layer 3, always computed). On Raw-mode logs these are
the same feed by construction and the diff is ~0 everywhere (used as a sanity check, not reported
as a finding). The interesting rows are the ShakeIt-mode logs.

### Lock — ShakeIt src p90 minus Raw p90, per corner (same-lap comparison)

| Corner | 1.0.6.5 | 1.0.6.6 | 1.0.6.7 | 1.0.6.8 |
|---|---|---|---|---|
| 1 | −73.9 | −78.2 | −8.6 | −80.7 |
| 2 | +6.6 | −2.1 | +4.1 | +5.1 |
| 3 | +3.4 | +0.7 | +0.7 | +3.7 |
| 4 | +21.5 | +13.8 | +3.6 | +2.4 |
| 5 | +26.5 | +15.0 | +6.4 | +1.3 |
| 7 | +35.4 | +12.7 | +33.1 | +2.6 |
| 8 | **+53.1** | **+32.0** | **+39.4** | +8.8 |
| 9 | +9.5 | +2.9 | +6.8 | +1.2 (n=37, flagged low) |

Corner 1 is a genuine outlier (ShakeIt reads 0 there — the low-severity gating edge case noted in
Task 2), excluded from the read below. **Per corner, ShakeIt's Lock source is consistently
*stronger*, not merely "close,"** especially at corners 7–8 (up to +53 points) — the pooled
figure (75.5–80.8 vs 73.3–79.8, near parity) masks this because it averages across all Slowing
frames on the lap, most of which are near-zero and dilute the effect. **This nuances, but does not
contradict, the brief's pooled claim** — Lock agreement is close in the aggregate but the
corner-level picture shows a real and repeatable ShakeIt-stronger bias at the harder-braking
corners.

### Slip — ShakeIt src p90 minus Raw p90, per corner (same-lap comparison)

| Corner | 1.0.6.5 | 1.0.6.6 | 1.0.6.7 | 1.0.6.8 |
|---|---|---|---|---|
| 1 | +6.3 | +1.1 | +3.3 | +5.8 |
| 2 | −18.4 | −26.0 | −21.5 | −14.5 |
| 3 | −1.4 | −15.9 | −25.3 | −24.9 |
| 4 | +0.01 | −7.0 | −12.6 | −6.3 |
| 5 | −0.2 | −23.0 | −6.9 | −2.6 |
| 7 | +1.7 | −1.7 | −4.7 | −2.5 |
| 8 | −2.0 | −6.4 | −2.4 | −2.9 |
| 9 | −7.0 | −23.2 | n/a (event immediately follows) | −7.3 |

**Confirmed at the corner level, not just pooled: Raw's Slip source is consistently stronger than
ShakeIt's at source**, at 6–7 of 8 corners in every version (deficits typically 5–25 points),
matching the brief's pooled figures (Raw 20.4–34.3 vs ShakeIt 17.4–30.4).

### The RearRight ceiling claim (1.0.6.6, "Raw's ceiling is too high")

I could not reproduce the brief's exact pooled figures (ShakeIt RR source p99 52.4→100.0,
Raw 67.1→77.2) from a corner-level reconstruction — likely because those numbers were computed
over a different frame subset (e.g. a full-session percentile rather than a per-corner peak) than
what a corner-aligned analysis can retrace exactly, and I did not chase the exact percentile
recipe further since it is outside this report's remit. **The underlying *mechanism*, however, is
directly observable and confirmed at specific corners**: at 1.0.6.6 corner 7, RearRight source is
*lower* for ShakeIt than Raw (33.8 vs 45.6) yet ShakeIt's normalized RearRight is *higher* (58.3 vs
51.4), because ShakeIt's ceiling at that frame (46.3) is much lower than Raw's (70.9). The same
pattern repeats at corner 8 (ShakeIt source 32.4 < Raw 53.1, but ShakeIt normalized 61.0 ≈ Raw
60.6, again because ShakeIt's ceiling, 42.5, is far below Raw's, 70.1). **Verdict: the mechanism
described — Raw's learned ceiling running higher than ShakeIt's, causing a comparable-or-weaker
raw signal to normalize to a comparable-or-lower output — is confirmed at corners 7 and 8 of
1.0.6.6. It is not confirmed at every corner** (e.g. corner 3 shows the opposite: ShakeIt's lower
source normalizes to a *lower*, not higher, output, because ceilings are close there, 72.2 vs
74.6). Report this as "confirmed in mechanism and at specific corners, not as a lap-wide law."

---

## Task 4 — Slip's 70.0 cap

**Mechanism empirically confirmed**, fit directly from the data rather than assumed from docs.
Using non-floor-bound frames from 1.0.6.5 Raw (n = 321), a least-squares fit of
`Diag.Source.Slip.All ~ wF·Front + wR·Rear` returns **wF = 0.45, wR = 0.55 exactly, R² = 1.000** —
matching the brief's stated front weight of 0.45 precisely. F1 is rear-driven, so `Front ≈ 0` in
essentially every frame, giving a pre-floor blend of `≈0.55 × Rear`; with `Rear ≈ 94` at a strong
slip moment that is `≈52`, matching the brief's "All ~52" figure. The floor,
`result = Max(blend, 0.70 × strongest_wheel)`, then lifts this: with the strongest wheel near 100,
`0.70 × 100 = 70`, and 70 > 52, so the floor wins and `.All` reads 70.0. **Confirmed.**

*(Side note: this measured 0.45/0.55 front/rear split for Slip's `.All` conflicts with
`aggregation-report.md`'s stated Slip `wFront + wRear = 0.65 + 0.35` — see §5.)*

### Per-corner: how often does the floor bind, and what would `.All` read without it?

Using the corner-exit (SpeedingUp) window, restricted to frames with a "real" slip signal
(strongest wheel > 20, to exclude near-zero cruising frames from the denominator):

| Corner | 1.0.6.5 Raw | 1.0.6.5 ShakeIt | 1.0.6.6 Raw | 1.0.6.6 ShakeIt | 1.0.6.7 Raw | 1.0.6.7 ShakeIt | 1.0.6.8 ShakeIt |
|---|---|---|---|---|---|---|---|
| 1 | 100% (n=69) | 100% (n=237) | 100% (n=2, low-n) | 100% (n=233) | 100% (n=92) | 100% (n=244) | 100% (n=238) |
| 2 | 100% (n=332) | 99.6% (n=226) | 100% (n=143) | 100% (n=8, low-n) | 100% (n=104) | 100% (n=5, low-n) | n/a (n=0) |
| 3 | 100% (n=63) | 100% (n=67) | 100% (n=34) | 100% (n=64) | 100% (n=98) | 100% (n=71) | 100% (n=72) |
| 4 | 100% (n=275) | 100% (n=360) | 100% (n=171) | 100% (n=100) | 100% (n=121) | 100% (n=135) | 92.6% (n=122) |
| 5 | 100% (n=78) | 100% (n=107) | 100% (n=32) | 100% (n=58) | 100% (n=25) | 100% (n=11, low-n) | 100% (n=4, low-n) |
| 7 | 100% (n=165) | 100% (n=131) | 100% (n=55) | 100% (n=33) | 100% (n=63) | 100% (n=30) | 100% (n=38) |
| 8 | 100% (n=101) | 100% (n=114) | 100% (n=64) | 100% (n=28, low-n) | 100% (n=29) | 100% (n=3, low-n) | 100% (n=14, low-n) |
| 9 | 100% (n=337) | 99.2% (n=368) | 100% (n=229) | 98.8% (n=269) | 100% (n=184) | — | 100% (n=82) |

**Once slip is actually happening (strongest wheel > 20), the floor is binding essentially 100% of
the time in every corner, in every log.** This means, functionally, whenever the rear tyres are
genuinely spinning under power, the published `.All` value is determined almost entirely by the
floor (`0.70 × strongest wheel`), not by the front/rear blend — the 0.45/0.55 blend only matters in
the sub-threshold region where slip hasn't really started yet. **Without the floor**, `.All` would
simply be the blend (`0.45·Front + 0.55·Rear`), i.e. roughly **55–75% of what a driver currently
sees** at peak moments (e.g. 1.0.6.5 Raw corner 1: blend 51.0 vs actual 70.0; corner 4: blend
55.1 vs actual 70.0). **Without the front-weight attenuation** (i.e. reporting the Rear group alone,
since Front ≈ 0 in this rear-driven car), `.All` would track much closer to the true rear-tyre slip
intensity — e.g. 1.0.6.5 Raw corner 1: Rear 92.6 vs published 70.0; corner 4: Rear 100.0 vs
published 70.0 — the front-weight attenuation alone is costing roughly 20–30 points versus what
the rear tyres are actually doing, independent of the floor.

**Exception to "Slip.All maxes at exactly 70.0 in every log":** this is true for
`WheelSlip.Normalized.All` in 7 of the 8 logs, but **not in 1.0.6.8 Raw**, which reaches **75.3**
(and `Projected.All` reaches 66.2 there, vs 54.5 in every other log). Given the mid-recording
restart documented in §0, and that this coincides with the same log whose Slip ceiling never
leaves its cold default (23.28) after the restart, I treat this as a symptom of the restart/cold
learner rather than a fifth, distinct cap value — but it does mean the brief's "in every log"
premise for the 70.0 cap needs this one caveat.

---

## 5. Contradictions with existing reports / notable flags

1. **`aggregation-report.md`** states Wheel Slip's `wFront + wRear = 0.65 + 0.35`. My direct,
   R²=1.000 regression fit of `Diag.Source.Slip.All` against `Front`/`Rear` on real telemetry gives
   **0.45/0.55** — matching the brief's own assumption, not the report's. Either the shipped
   default changed after that report was written, or the 0.65/0.35 figure in that table refers to
   a different aggregation tier (e.g. the L/R axle blend, not the Front/Rear car-level blend) and
   was mislabeled. I did not chase this further — flagging it as a documentation/runtime mismatch
   worth a follow-up look, not something I could resolve from telemetry alone.
2. **`Diag.Slip.SourceFallbackActive` is 58–68% active in every ShakeIt-mode log, 0% in every
   Raw-mode log** — a much larger and more consistent effect than the brief's cited 1.9–4.7%
   figure, which is Lock-only. No existing report in `docs/` appears to call this out at the
   per-channel level; worth a dedicated look given how much of the ShakeIt Slip pipeline's runtime
   this represents.
3. The brief's specific RearRight p99 figures (52.4/67.1/77.2/100.0) could not be reproduced from
   a corner-aligned reconstruction; the underlying mechanism they describe is confirmed at two of
   four checked corners in 1.0.6.6, not lap-wide (§ Task 3).

---

## 6. Concerns / caveats to weigh when acting on this report

- **1.0.6.8 Raw is not a clean single-lap sample.** Every 1.0.6.8-Raw number in this report comes
  from the second ("lap B") pass after a mid-recording restart; its Slip source is likely still
  cold and should not be treated as representative of steady-state 1.0.6.8 Raw behaviour without a
  cleaner re-capture.
- **Ceiling values are still converging within each single lap** (fresh `Parameters.json` per
  version, low sample counts). Cross-version ceiling comparisons at corner 9 in particular are
  confounded by how far each run's learner had converged by that point in the lap, not purely by
  version behaviour. The 1.0.6.8 ceiling gap (present from the earliest corners, ~25–30 points) is
  large enough to survive this caveat; smaller, single-corner ceiling deltas elsewhere should not
  be over-read.
- **Corner 6 and the ambiguous "10th event" are excluded outright** rather than reported with a
  caveat, per the brief's own instruction — if either is independently important to the owner
  (e.g. corner 6 is a real, named corner they care about), it needs a fresh, dedicated capture
  rather than reuse of these eight logs.
- **The 1.0.6.6-Raw Slip "softness" is attributed to lap variance, not code**, on the strength of
  matching ceilings — but I only have one lap per version-mode combination, so this cannot be
  fully disentangled from a genuine version effect with this dataset alone. A second Raw-mode lap
  on 1.0.6.6 would settle it.
- All percentiles/means reported are pooled *within* a corner's window across that one log; no
  cross-corner averaging was done for the headline max-grip comparisons, per the "same-lap or
  corner-aligned, never mixed" requirement.
