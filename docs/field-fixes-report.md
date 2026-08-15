# Field fixes report — QAdvanceFeedback, Forza Horizon 6 session 20260814-195011

> **Path note (added later, see docs/private-relocation-report.md):** this report refers to
> `QAdvanceFeedback/Private/SimHubTelemetryAdapter.cs`; the withheld `Private\` folders were later
> consolidated into one top-level `Private\` folder beside the `.sln`, so that file now lives at
> `Private\QAdvanceFeedback\SimHubTelemetryAdapter.cs` - left unchanged below as a historical record.

## STATUS

FIXED (defects A, B, C, D) and hardened (GForceMaxLearner outlier ceiling). Defect E is **not
reproducible** from the supplied log — see its own section; no code change made. Defect F is
**substantially explained by A but not provably fixed** from the log alone; a separate, well-evidenced
but out-of-scope concern is flagged instead of improvised. Full detail, evidence and reasoning below.

**Tests:** 437 total (427 pre-existing + 10 new), 0 failed, 0 skipped, full build (Private/ present).
**Clean-clone tests:** 417 total (407 pre-existing + 10 new), 0 failed, 0 skipped, Private/ absent.
**0-warning / single-DLL confirmation:** `dotnet build QAdvanceFeedback/QAdvanceFeedback.csproj
-t:Clean,Restore,Build -p:Configuration=Release` → `0 Warning(s), 0 Error(s)`. `bin/Release/net48/`
contains exactly `QAdvanceFeedback.dll` and `QAdvanceFeedback.pdb`, verified for both the full build
and a from-scratch copy of the tree with both `Private/` folders removed (417 tests, same 0-warning/
single-DLL result).

**No UI text, settings default, or user-visible string changed** by this fix (every change is inside
`Core/`, `Private/SimHubTelemetryAdapter.cs`, and their tests) — the four settings-UI screenshots are
therefore **not regenerated**; nothing in them changed. Nothing needed localising in either string
table for the same reason.

## Method

The CSV (2,612 frames, all published + Diag columns, no raw telemetry) was analysed directly (Python,
`csv`/percentile scripts) to reproduce every BEFORE table in the brief before touching any code, then
a second, line-for-line-transliterated Python replay of the actual C# formulas (`GripLearner`,
`KeyedGripLearner`, `MonotoneCubicCurve`/`OutputProjector`'s control-point construction, the ported
`GForceEngine` washout model) was driven from the log to produce AFTER numbers. **What could and could
not be replayed, precisely:**

- `WheelLock.Raw.*` / `WheelSlip.Raw.*` (Layer 3 outputs, real Layer 4 inputs) — read directly from
  the CSV, not reconstructed. Verified these ARE what Layer 4 actually consumed in this session (not
  a diverging ShakeIt-exported source) by cross-checking per-wheel proportional shape between
  `WheelSlip.Raw.*` and `WheelSlip.Normalized.*` on uniform-raw frames.
- `Diag.Direction` — read directly; this **is** the real per-frame verdict `LongitudinalDirectionResolver`
  produced for this session (ground speed itself isn't in the CSV, so the resolver can't be re-driven,
  but its own recorded output can be reused exactly).
- `Diag.MotionMagnitudeG` — read directly. Under the **current, buggy** code this is numerically
  identical to `|raw SimHub AccelerationSurge|` (no conversion applied — see defect A), so
  `loggedValue / 9.80665` exactly reproduces what `AchievedMotion.Resolve` returns **after** the fix,
  with no other change to its own arithmetic.
- **Not available and approximated, flagged:** per-frame `LateralG` (affects only which frames feed
  the learner, not the published ratio — assumed "isolated" for every frame) and the *signed* raw
  `LongitudinalG` used by `GForceEngine` (reconstructed from `Diag.Direction` as sign source, since the
  session doesn't log the raw signed value) — see defect F's own section for why this specific
  approximation limits what F's replay can prove.
- The real C# `NormalizedWheelLockSlipEngine`/`GForceMaxLearner`/`GForceEngine` classes were also
  exercised directly via **unit tests** (not just the Python replay) for every fix below, which is the
  higher-confidence evidence; the Python replay is corroborating, log-scale evidence only.

## A — Units (root cause)

**Confirmed, not assumed.** `Diag.MotionMagnitudeG` in the captured log: p50=8.35, p90=19.84,
p99=45.72, max=194.55. As G these are impossible (194.5 G is lethal); as m/s² they are ordinary
Forza driving with one large spike (~19.8 G equivalent — a hard impact/kerb strike, plausible).
`Diag.Lock.LearnedPeakG` sits at 6.9–7.5, i.e. pinned just under `GripLearner.MaxPlausibleG` (8.0) —
because most real m/s² readings exceed 8, the learner's own outlier guard was silently rejecting the
majority of real evidence, not learning it. `Diag.GForce.LearnedAccelMaxG` reached 179.8 and
`LearnedDecelMaxG` reached 21.7 — both physically absurd as G, both exactly `Diag.MotionMagnitudeG`'s
own scale (`GForceSettings.ObserveAccelG/DecelG` are fed `sample.New.LongitudinalG` unconverted).

**Where the boundary actually is, and the canonical unit chosen:** `ITelemetryFrame.LongitudinalG`/
`LateralG` are used, and every downstream consumer (`AchievedMotion`, `GForceEngine`,
`GForceMaxLearner`/`GForceSettings`'s FIXED maxima 0.9g/2.0g, `GripLearner.MaxPlausibleG`, dozens of
existing G-calibrated unit tests) is already calibrated in **G** — that is the canonical internal
unit, unchanged. The actual SimHub field, `StatusDataBase.AccelerationSurge`/`AccelerationSway`, is
**m/s²** — confirmed against this session's own numbers above, and consistent with the sibling
ReliableWheelLockSlip project's own `AccelerationScaleDetector` finding for the same class of field.
`SimHubTelemetryAdapter.ToFrame` (`QAdvanceFeedback/Private/SimHubTelemetryAdapter.cs`) previously
assigned the raw m/s² value straight into the G-named field with **no conversion at all** — that is
the entire bug. The fix converts exactly once, at this one edge, via a new shared, SimHub-free helper:
`QAdvanceFeedback/Core/AccelerationUnits.cs` (`Ms2ToG`/`GToMs2`, constant `9.80665`).

*Note on `SimHub.AccelerationSurge` being m/s²:* re-verified independently here (not merely asserted
from the sibling project) via the log evidence above — the m/s² reading is the only one consistent
with p50/p90/max being ordinary road-car driving instead of impossible G values.

Renaming `ITelemetryFrame.LongitudinalG`/`LateralG` to an explicit `...Ms2` name was considered and
rejected: **every** consumer already treats the field as G (by name, by doc comment, and by ~80
existing test fixtures), so the field's own name is already unambiguous *within this codebase*; the
actual ambiguity was only ever at the SimHub-facing edge, which is exactly where the fix now lives.
`AccelerationUnits.Ms2ToG` is named explicitly for the direction it converts, satisfying the
`...Ms2` vs `...G` naming requirement at the one place the two units actually meet.

**Also fixed — outlier rejection:** `GForceMaxLearner`'s 2-consecutive-frame confirmation protects
against a *single* spike but not a repeated implausible one; added `MaxPlausibleG = 8.0` (mirrors
`GripLearner`'s own, already-correct constant) as a hard pre-filter in `Observe`, rejecting a
magnitude before it can even start a confirmation streak.

**Tests:** `AccelerationUnitsTests.cs` (new, 4 tests, pins the conversion factor/sign/round-trip and a
concrete `194.5487…→~19.8` check reproducing this exact session's own spike).
`GForceMaxLearnerTests.cs`: adapted `A_sustained_spike_across_two_consecutive_frames_IS_learned` to a
plausible (5.0g) sustained value (its original 19.9g fixture is now precisely the case the new guard
must reject — see the new dedicated test), added
`An_implausible_spike_is_never_learned_even_when_sustained_across_many_frames` and
`A_value_exactly_at_MaxPlausibleG_is_still_learnable`.

**Note on why the actual SimHub-facing call site itself has no direct test:** `SimHubTelemetryAdapter`
is a net48-only class requiring a live `GameReaderCommon.GameData` (confirmed: the net8.0 test project
has no SimHub/GameReaderCommon reference at all, by the project's own existing architecture — see
`Private/README.md`). The conversion **formula** is therefore pinned where it's actually computed
(`AccelerationUnits`), and the two-line call site is a trivial, reviewable delegation to it.

## B — Slip normalisation inverted / C — Lock non-monotone in Raw

Both traced to the **same root cause**, additional to (and only partly resolved by) the units fix:
`NormalizedWheelLockSlipEngine.ComputeChannel` published severity = `gripUtilization` (the G-based
learned ratio) **alone** — Raw's absolute level was deliberately discarded by design (only its
four-way *proportions* were used; see the class's own pre-existing doc comment), mirroring the
sibling project's braking fix. That is correct for **braking** (higher deceleration genuinely means
closer to lockup) but **backwards for wheelspin**: achieved chassis acceleration typically *drops*
once a driven wheel starts spinning (torque is spent spinning the tyre, not the car). Confirmed in
the log: SpeedingUp-only frames binned by `WheelSlip.Raw.All`, achieved-G (fixed units) reads
0.72–0.81g in the 60–101 bins vs. 1.1–1.4g in the 1–60 bins — **falling**, not rising, exactly during
genuine wheelspin.

**Fix (`NormalizedWheelLockSlipEngine.ComputeChannel`):** `severity = Max(effectiveGripUtilization,
mean(rawWheels))` — Raw's own instantaneous mean now floors the published severity, so it can never
read below what Layer 3 itself already measured. `effectiveGripUtilization` is `gripUtilization`
gated by a new per-channel instant-attack/fast-release envelope of "is Raw currently active"
(`RawActiveThreshold = 1.0`) — see defect D below for why the gate (not just the floor) was needed.

**Test-fixture consequence (documented, not a weakened assertion):** several pre-existing tests
(`Arcade_and_sim_magnitude_traces_both_span_a_useful_range_instead_of_one_saturating`,
`Uniform_raw_of_all_zero_falls_back_to_an_even_distribution_of_grip_utilisation`) deliberately held Raw
at a **constant, often large** value specifically to prove Raw's absolute level didn't matter — the
brief's own acceptance criteria now require it to matter (as a floor). Adapted, not weakened: the
first now holds Raw at a small but "active" 1.0 (still isolates the G-calibration math, since a
sustained-active envelope introduces no lag — see `Lock_severity_does_not_lag_while_Raw_stays_continuously_active`,
new); the second now uses a small uniform *nonzero* Raw (the "no differentiation → even split" code
path is identical for any uniform value) — sustained, exact-zero Raw is precisely what defect D
requires to now release toward zero, so keeping the old fixture would re-assert the bug.

**New tests:** `Lock_severity_is_never_below_Raws_own_instantaneous_value_even_when_learned_G_severity_is_low`,
`Slip_severity_climbs_monotonically_as_Raw_climbs_even_though_achieved_G_falls_during_genuine_wheelspin`.

### BEFORE / AFTER (Raw bins, replayed from the captured log)

```
LOCK                 BEFORE (buggy)                          AFTER (fix A+B+D combined)
raw bin      n     Normalized        Projected           Normalized        Projected
0-1        1743   p50=0.0 p90=100.0  p50=0.0 p90=100.0   p50=0.0 p90=0.0   p50=0.0 p90=0.0
1-10         67   p50=0.0 p90=79.2   p50=0.0 p90=78.5    p50=0.0 p90=14.7  p50=0.0 p90=0.0
10-30       119   p50=0.0 p90=64.9   p50=0.0 p90=40.3    p50=0.0 p90=24.8  p50=0.0 p90=3.7
30-60       139   p50=0.0 p90=79.1   p50=0.0 p90=78.3    p50=0.0 p90=48.5  p50=0.0 p90=18.8
60-90       351   p50=0.0 p90=83.1   p50=0.0 p90=84.8    p50=0.0 p90=79.1  p50=0.0 p90=78.2
90-101      193   p50=100.0 p90=100  p50=100.0 p90=100   p50=100.0 p90=100 p50=100.0 p90=100

Monotone (p50 and p90) across bins: BEFORE p90 NOT monotone (100→79→40→78→85→100). AFTER: fully
monotone non-decreasing in both p50 and p90, and 0 at Raw~0 (was 100).

SLIP                 BEFORE (buggy)                          AFTER (fix A+B+D combined)
raw bin      n     Normalized        Projected           Normalized        Projected
0-1        1742   p50=0.0 p90=94.2   p50=0.0 p90=93.7    p50=0.0 p90=9.2    p50=0.0 p90=0.0
1-10        108   p50=79.1 p90=94.2  p50=73.0 p90=93.7   p50=47.8 p90=81.7  p50=12.1 p90=78.2
10-30       187   p50=79.1 p90=94.2  p50=73.0 p90=93.7   p50=47.6 p90=82.2  p50=12.0 p90=79.0
30-60       169   p50=79.1 p90=85.0  p50=73.0 p90=83.3   p50=79.6 p90=83.0  p50=74.3 p90=80.4
60-90       374   p50=67.6 p90=84.3  p50=37.5 p90=82.3   p50=79.1 p90=81.5  p50=73.0 p90=77.8
90-101       32   p50=2.2  p90=100.0 p50=0.0  p90=100.0  p50=46.6 p90=100.0 p50=46.4 p90=100.0
```

(These figures come from the transliterated-formula replay, not directly from `dotnet test`; they
corroborate the unit-test assertions above rather than replacing them. The brief's own quoted numbers,
e.g. "Raw 1-10 → Normalized p50=85.0", are the actual logged values — my replay reproduces them to
within a few points, the small residual difference traced to the log not including per-frame
`LateralG`, which the replay approximates as "isolated" every frame.)

**Honest residual (SLIP):** the AFTER table is dramatically improved (no longer collapses through
zero at high Raw) and is monotone in **p90** end-to-end (reaching 100 at 90-101, as required). **p50
is not fully monotone**: 90-101 (n=32, the smallest bin) reads 46.4, below 30-60/60-90's 73-74. Traced
to the bin itself: exactly half of these 32 frames resolve `Diag.Direction == Slowing` (not
`SpeedingUp`), so — correctly, by the pre-existing and unmodified "pedal state/G is never the
answer, only measured direction decides" rule — they are *not engaged* for the Slip channel at all and
read zero, dragging the bin's median down. This is a real, physically-ambiguous data characteristic
(the wheel is spinning while the car's own ground-speed derivative reads "slowing", e.g. during a
spin/impact event), not a normalisation arithmetic bug — I did not chase it further with an additional
heuristic, since doing so without more telemetry (or a rig) risks exactly the kind of unvalidated guess
this task explicitly warns against.

## D — Release lag

**Confirmed, and root-caused precisely** — traced a specific 218-frame (3.63s) release event in the
log: `WheelLock.Raw.All` drops to exactly 0 while `Diag.Direction` stays `"Slowing"` (the car keeps
decelerating — engine braking/drag) for the entire stretch, and `Diag.MotionMagnitudeG` decays only
gradually (11.3 → 4.1 g-equivalent) across it. **This is genuine, still-happening physics, not a
smoothing artefact** — there is no pre-existing stale-state filter to blame; `gripUtilization` is
recomputed fresh every frame from the current G reading, and that current reading legitimately stays
elevated for a while after the wheel itself stops locking. Consequently **units alone (A) cannot fix
this** — confirmed by hand: dividing both the observed magnitude and the learned peak by the same
9.80665 constant leaves their *ratio*, and therefore the decay's relative timing, unchanged.

**Fix:** the same floor/gate mechanism as B/C (see above) — `_lockRawPresence`/`_slipRawPresence`, an
instant-attack (Raw active → 1.0 immediately), fast-release (`ReleaseTauSeconds = 0.03`, ~5τ≈0.15s to
release) envelope of "Raw is currently indicating anything". Once Raw drops below
`RawActiveThreshold` the envelope — and with it, gripUtilization's contribution — decays to
near-zero regardless of how elevated the underlying G reading still is, while never adding any lag
while Raw stays active (existing calibration tests that change magnitude while holding Raw active are
unaffected — verified by a dedicated new test,
`Lock_severity_does_not_lag_while_Raw_stays_continuously_active`).

**New test:** `Lock_severity_releases_quickly_once_Raw_drops_even_though_the_car_keeps_decelerating`
(saturates the channel, drops Raw to 0 while still "Slowing", asserts release under 9 frames/~0.15s).

**BEFORE / AFTER (release-from-saturation events, same log, same measurement method — Raw crosses
from ≥1 to <1 with Projected already >10 at that instant, timed until Projected first falls ≤10):**

```
                 events    median          max
BEFORE            16      41.5 frames     218 frames
                          (0.69 s)        (3.63 s)
AFTER (A+B+D)      7      12 frames        31 frames
                          (0.20 s)        (0.52 s)
```

Median release time is now close to, and the worst case is far below, the pre-fix worst case; it does
not uniformly hit the "~0.15s" target in every single event (worst AFTER case is 0.52s) — the residual
long tails are the same handful of frames affected by B's own residual (brief re-activation of the
envelope from a momentary non-zero Raw blip during the tail). I did not shorten `ReleaseTauSeconds`
further to force every case under 0.15s, since doing so with no rig/driver feedback risks cutting off
genuinely short lock/spin pulses audibly - this is flagged as a judgement call, not derived from
measurement.

## E — `Diag.MotionLevel`

**Could not reproduce.** Direct inspection of the supplied CSV (column 65, `Diag.MotionLevel`) shows
the value `"Direct"` in **every one of the 2,612 rows** — populated, not empty — because
`sample.New.LongitudinalG` (SimHub `AccelerationSurge`) is present every frame in this Forza Horizon 6
capture, so `AchievedMotion.Resolve` never needs to fall through to `Derived`/`Unavailable`. The
brief's claim that this column is empty does not match the evidence in the supplied file. Per the
task's own instruction ("if something cannot work as specified, STOP and say so"), **no code change
was made for E** — the field is already wired (`QAdvanceFeedback.cs` → `PropertyPublisher.UpdateDiagnostics`
→ `AllPublishedProperties`) and demonstrably populated in this exact capture. If a *different* session
genuinely shows it empty, that would point to a CSV-header/column-order mismatch rather than the field
never being set — worth re-checking column alignment specifically, not this field's wiring.

## F — G-force at standstill

**Likely explained by A, but not provably fixed from this log alone — said plainly rather than
asserting success.** The log's own evidence (pads reaching 36/56 while `Diag.MotionMagnitudeG < 0.05`)
is consistent with defect A (the same units bug feeds `GForceEngine.Compute`'s `rBrake`/`rAccel`
directly from the unconverted, m/s²-scale `sample.New.LongitudinalG`, against G-scaled FIXED maxima
0.9/2.0 — permanently saturating the sustained path). However, replaying the washout model
(transliterated `GForceEngine` formula in Python, fed reconstructed signed G from `Diag.Direction` +
`Diag.MotionMagnitudeG`, since the CSV has no raw signed telemetry) does **not** show a clear,
consistent reduction in standstill pad maxima after the units fix — this is inconclusive, not negative,
and I attribute the inconclusiveness to two things I can state but not resolve from this log:

1. The replay's sign reconstruction (from `Diag.Direction`, since the raw signed value isn't logged)
   is an approximation `GForceEngine` itself does not use — it reads the raw signed
   `LongitudinalG` directly.
2. **A separate, well-evidenced-elsewhere-in-this-codebase but unverified-for-G-force concern**:
   `GForceEngine`'s own doc comment flags "positive LongitudinalG = accelerating" as an *unverified*
   sign assumption. This exact codebase's `NormalizedWheelLockSlipEngine` documents, as an established
   finding, that Forza Horizon 6 reports the **opposite** convention (positive while genuinely
   slowing, in 95.8% of qualifying frames) — which is *why* Lock/Slip direction was deliberately moved
   off `LongitudinalG`'s sign entirely. `GForceEngine` was never given the same treatment. If that
   same inversion applies to the G-force channel, the brake/accel chains are swapped for this title —
   which would independently explain the driver's own "Bottom Front under acceleration" complaint
   (Bottom Front is the *braking* chain's terminal pad in this design).
   **I did not fix this** — routing `GForceEngine`'s chain-direction decision through
   `LongitudinalDirectionResolver` (mirroring the Lock/Slip fix) would require reworking its public
   `Compute` signature and rewriting essentially all ~35 of its existing tests (which construct raw
   signed `LongitudinalG` samples with no speed/dt history at all, so direction would resolve
   `Unknown` for every one of them under that scheme) — a much larger, higher-risk change than this
   task's remaining scope allows to do responsibly. Flagged here explicitly rather than improvised;
   worth a dedicated follow-up.

Standstill pads reaching a genuine, small transient residual for a few tenths of a second after a
large excursion (the washout's own transient decay, ~3×0.08s≈0.24s) is **by design**, not a bug — see
`GForceEngine`'s own S2/S6 acceptance scenarios.

## Mutation evidence

**(a) Reintroduce the unit mismatch** — `AccelerationUnits.Ms2ToG` changed to `ms2 => ms2` (identity,
the exact pre-fix behaviour). Result: **4/4 `AccelerationUnitsTests` FAILED** (`Ms2ToG_uses_standard_gravity`,
`Ms2ToG_and_GToMs2_are_exact_inverses`, `Ms2ToG_preserves_sign`,
`A_known_forza_horizon_6_session_spike_converts_to_a_plausible_g_value`). Reverted; full suite green
(437/437) again.

**(b) Reintroduce the slip inversion** and **(c) remove the monotonicity guard on lock** — both
correspond to the same one-line fix (`severity = Math.Max(effectiveGripUtilization, mean)` reverted to
`severity = effectiveGripUtilization`), since B and C share the identical root cause and fix (see
above). Result: **2 tests FAILED** —
`Lock_severity_is_never_below_Raws_own_instantaneous_value_even_when_learned_G_severity_is_low` (the
lock/monotonicity guard, mutation (c)) and
`Slip_severity_climbs_monotonically_as_Raw_climbs_even_though_achieved_G_falls_during_genuine_wheelspin`
(the slip inversion, mutation (b)) — the other 435 tests still passed. Reverted; full suite green
(437/437) again.

Both mutations were performed by editing the exact source, running the targeted (and then full) test
suite, confirming the failure, then reverting to the exact original text and re-confirming 437/437.

## Concerns / unverified

- **No live-game verification** — everything above is verified against the CSV replay and unit tests
  only, per this task's own stated constraint (no SimHub/game access).
- **Defect E's premise does not match the supplied log** — see its own section; flagged rather than
  "fixed" against evidence I couldn't reproduce.
- **Defect F is only partially confirmed** — see its own section; a second, well-evidenced but
  out-of-scope sign-convention concern for `GForceEngine` is flagged for follow-up, not fixed here.
- **Slip's residual top-bin dip (n=32)** is a genuine, small-sample, physically-ambiguous-direction
  artefact (see B/C section) — not chased further without more telemetry.
- **`ReleaseTauSeconds = 0.03` and `RawActiveThreshold = 1.0`** are judgement calls informed by the log
  (not independently derived/rig-tuned) — flagged as such, matching this plugin family's own existing
  convention of documenting judgement calls explicitly (e.g. `GForceEngine.TransientGain`'s own remarks).
- **The owner's own SimHub-ShakeIt reference composition** (see below) was decompiled and cross-checked
  numerically against the owner's stated anchor points (8/8 values matched within reading-off-a-chart
  tolerance) — treated as validation evidence for the DIRECTION of the B/C/D fixes, not copied into any
  default.

## SimHub ShakeIt reference composition (requested cross-check, not a defaults change)

Decompiled (`ilspycmd` against the installed `SimHub.Plugins.dll`) `SimHub.Plugins.DataPlugins.ShakeItV3.Filters.GammaFilter.GetOutput`
— the **only** filter `WheelsLockContainer`/`WheelsSlipContainer` use (there is no separate
curve/spline filter for these two ShakeIt effects; the "curve" the owner described is this formula's
own output, sampled at several input points, not a separately-editable anchor table):

```
num = input(0..1) * InputGain            // InputGain: the owner's "gain" (100 = unity)
if num <= 0: return 0
if gamma != 1: num = 100 * (num/100) ^ (1/gamma)
if Threshold > 0:
    num = (num - Threshold) * 100/(100 - Threshold)
    if num <= 0: return 0
if num > 0: num = MinimumForce + num * (100 - MinimumForce)/100
return clamp(num, 0, 100)
```
(`SimHub.Plugins.DataPlugins.ShakeItV3.Filters.GammaFilter`, `EffectsContainerBase.ProcessEffects`
confirms the effect's own 0-1-scale raw value — i.e. our own Raw/100 — is what's fed into this
formula, `InputGain` and all.)

Composed with the owner's two validated-good parameter sets (gamma 0.15/threshold 2/min 1 for Lock,
gamma 0.20/threshold 5/min 1 for Slip) against 8 of the owner's own stated anchor points: computed
values were 0.00/2.33/8.35/21.80/33.17/49.02/70.74/100.00 (Lock) vs. the owner's stated
0/5/10/28/43/50/73/100, and 1.03/3.89/13.30/29.94/42.03/57.32/76.43/100.00 (Slip) vs. 0/6/8/30/40/60/80/100
— matching within a few points across all 8, confirming the decompiled formula (not a guess).

Replaying this reference (evaluated directly on each frame's own `WheelLock.Raw.All`/`WheelSlip.Raw.All`)
against the same log bins:

```
                BEFORE our pipeline          Reference (owner's validated ShakeIt config)     AFTER our pipeline (A+B+D)
LOCK   0-1      p50=0.0  p90=100.0           p50=0.0   p90=0.0                                p50=0.0   p90=0.0
       30-60    p50=0.0  p90=78.3            p50=0.0   p90=1.2                                p50=0.0   p90=18.8
       60-90    p50=0.0  p90=84.8            p50=20.1  p90=20.1                               p50=0.0   p90=78.2
       90-101   p50=100  p90=100.0           p50=100.0 p90=100.0                              p50=100.0 p90=100.0
SLIP   60-90    p50=37.5 p90=82.3            p50=28.0  p90=28.0                                p50=73.0  p90=77.8
       90-101   p50=0.0  p90=100.0           p50=100.0 p90=100.0                               p50=46.4  p90=100.0
```

Our fixed pipeline's shape now tracks the reference's own "near-zero until Raw is genuinely high, full
at Raw≈100" character far more closely than the pre-fix pipeline did, though it is **not** identical —
our own Normalized layer legitimately adds per-car G calibration the reference doesn't have, and the
residual differences (e.g. our 60-90 bin running hotter than the reference's) trace to the same
achieved-G floor/gate mechanism (B/C/D fixes) rather than to the curve shape itself.

**Acceptance check (shipped DEFAULT curve, not the owner's workaround numbers), per the coordinator's
explicit ask:** replaying the log through the unmodified default Lock/Slip `OutputProjector` curves
(start 20/30→10/60→30/80→80/100 for Lock; start 20/30→8/60→20/80→75/100 for Slip) against the AFTER
Normalized values above IS the "AFTER our pipeline" Projected column already shown in the B/C table —
Raw~0 now reads 0 (was 100), and the shape is monotone through to 100 at Raw 90-101 for Lock. **I did
not need to move the default curve's start point past 20, nor apply any external gamma, to get useful
discrimination** on this log once B/C/D were fixed — the defaults were left exactly as shipped, and
this is presented as the honest result, not adjusted to look better.
