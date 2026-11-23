# Learning maturity indicator and SMax evidence persistence - report (2026-11-21)

## Why

Under Auto the key data values move on their own, but nothing said how far each value had actually been
learned. The owner asked for an LED next to each learned value showing its maturity, working the same after a
SimHub restart. That needed one more fix: SMax's evidence was not saved, so every restart started SMax's
learning again from zero (only its last value was kept as a reference). With frequent car and surface changes
and a hand-over that takes over a minute of driving, it could never finish.

## What the maturity means (one rate per value, 0..1)

| Value | Maturity | Survives a restart |
|---|---|---|
| Lock / Slip SMax | `KeyedScaleLearner.CeilingHandoverConfidence`: the share of the ceiling that is this key's own at-limit evidence (the weight the ceiling itself blends with) | yes, since this change (evidence saved) |
| Lock S90 / S75 | the lower of SMax's maturity and that anchor's physical-ratio maturity (taught corners, 5 for full). The values shown are EXACTLY the ones the output builds its curve from (`NormalizedWheelLockSlipEngine.TryResolveLockAnchors`; owner, 2026-11-22), so always SMax > S90 > S75: a per-corner ratio of 0.98 or more is never learned and a saved one is ignored (it had shown S90 54.6 / S75 54.1 over SMax 52.5) | yes (corner counts were already saved) |
| Slip S90 / S75 | SMax's (they are derived from it) | yes |
| Slip Angle Max, Best, Min (before the peak is confirmed) | one confidence for all three: the fast estimate's evidence (weight / 10) x stability (Max steady over the last 5 corners), or consistent crossings / 3, at most 0.99; green once evidence is full and Max steady (latched); tooltip "Learning - 63% confident (peak not confirmed yet)" - see `slip-angle-fast-learning-report.md` | yes (exactly, see `parameters-file-format-report.md`) |
| Slip Angle Max (confirmed) | 1 | yes |
| Slip Angle Best / Min (confirmed) | curve coverage (window quality / 0.5) | yes |
| Slip Angle, borrowed window (owner, 2026-11-22) | the LED is half-and-half while the key has nothing of its own and starts from a window `SlipAngleBorrowRules` allows: left half this key's own (Red), right half the lender's confidence; tooltip "Borrowed from this car on another surface" / "...another car in this game". A borrowed k NEVER colours the LED - it adds the tooltip line "Front / rear balance (k) borrowed from this car on another surface" | - |
| G-Force Acc / Dec / Lat | none - a 2-minute sliding window with no count or minimum: no indicator | - |

The rate is always the same exact (game, car, source, surface) key the shown value comes from.

### Borrowed values (owner, 2026-11-21: "follow your design pattern")

A value this key has not learned itself still shows how far it can be trusted, by where it came from - its share
(`KeyDataPanelModel`), with its own tooltip:

| Origin | Share | Tooltip |
|---|---|---|
| Learned in an earlier session (saved ceiling, no evidence yet) | 50% | "Learned in an earlier session - confirming as you drive" |
| Borrowed from this car on another surface (Tier 4) | 50% | "Borrowed from this car on another surface" |
| Borrowed from another car in this game (Tier 3) | 30% | "Borrowed from another car in this game" |
| Borrowed from another game (Tier 2) | 10% | "Borrowed from another game" |
| Slip borrowing Wheel Lock's learning | 30% | "Borrowed from Wheel Lock's learning" |

The shown rate is the larger of the key's own hand-over and its origin's share (times the source's maturity).
Lock S90 / S75 are capped by their own ratio maturity.

## The indicator

- `Ui/Views/LearningLed.xaml`: a circle with a radial gradient (bright core, main colour, dark edge), a thin
  dark rim and a soft white glint. Its diameter is 42% of the row height (the text box beside it), centred.
- Colours (owner's revised schema, 2026-11-21): **Red only at 0%**, Orange above 0 and below 25%, Yellow 25-50%,
  Azure 50-75%, Blue 75-100%, Green at 100%. A boundary goes to the higher colour. The owner's text read
  "Orange >0 <50", which overlaps Yellow; it is taken as below 25% (said so at the time). The percent is rounded
  down and a partial rate stays within 1-99%, so 99.6% is Blue "99%".
- **Borrowed - a cold start only (owner, 2026-11-22):** while this game / car / source has NO real data at all, the
  left half Red and the right half the colour of the borrowed share, labelled where it came from (it replaces the
  earlier purple left half). From the first real data on - in this session, or reloaded from the parameters file after
  a restart - it is the key's own learning, still going: its own maturity colour and the normal "Learning - x%
  confident" label, at least 1% (Orange), never a borrowed share. Before, a key with a few own samples and its own
  confidence still 0% kept the borrowed 30% and showed a full Yellow LED labelled "borrowed".
- Tooltip: "Not learned yet - using the starting value" (0), "Learning - 63% confident", "Fully learned";
  Chinese 「尚未学习，使用初始值」「学习中，置信度63%」「已完全学习」.
- Placement: settings page (Lock / Slip key data, Slip Angle Max/Best/Min) left of the box; ShakeIt panels
  (Lock / Slip, Slip Angle) right of the box. Collapsed - no space - under Manual and on hidden rows
  (S90/S75 under Max-Grip-Only).

## MVVM

- `LearningIndicator` (immutable: `IsVisible`, `Level`, `Percent`, `ToolTip`, `IsBorrowed`, `OwnLevel`) from the one
  pure rule `LearningIndicatorRules.From(rate, visible[, tooltip[, borrowed[, ownRate]]])`; the left half is a second
  ellipse under a left-half opacity mask in `LearningLed.xaml`, bound one way to `IsBorrowed` and `OwnLevel`
  (`KeyDataMaturity.Borrowed` / `OwnSMax`).
- `KeyDataPageViewModel` / `KeyDataViewModel` expose `SMaxIndicator`, `S90Indicator`, `S75Indicator`;
  `SlipAngleTabViewModel` / `SlipAngleEffectViewModel` expose `MaxIndicator`, `BestIndicator`, `MinIndicator`.
  The view binds one way (`DataContext="{Binding SMaxIndicator}"`), no code-behind logic.
- The rates come through the optional `ILearningMaturityHost` (the plugin's page and panel hosts), read with
  the learned values themselves: at once on an Auto toggle, a source change and (settings page) a game change,
  and once a second under Auto. The plugin reads the Lock/Slip rates the way it reads the learned values; a read
  that collides with the data thread returns the last answer. Slip Angle rates are read under its lock.

## SMax evidence persistence (runtime file v14, summary format v15)

- New sections `LockScaleEvidence` / `SlipScaleEvidence` (`ScaleEvidenceState` per key, surface keys included):
  the at-limit distribution, the general distribution and the at-limit spread (count, mean, M2), plus the key's
  borrowing-tier latches (surface, tier, reference key, reference ceiling).
- **Single values (v15, owner: "use single values instead arrays"):** each distribution is saved as its counts,
  totals and 16 percentiles (P5, P10, P20, P30, P40, P50, P60, P70, P75, P80, P85, P90, P93, P95, P97, P99) instead
  of a bucket list. On load it is rebuilt smoothly (each gap's weight spread over 8 sub-buckets, plus tails) with a
  thin bucket at each percentile so every saved percentile reads back exactly; new samples then move it as before.
  An older file's bucket lists still load and are saved as summaries from then on.
- Loaded right after the saved ceilings, so a restart continues each key where it stopped: the same ceiling, the
  same maturity, and new driving adds to the saved evidence.
- **Restart = continue:** a key whose evidence was restored publishes its learned ceiling directly - no cold blend
  starting over. (A key from an older file without evidence keeps the cold blend.)
- **Exact on the menu:** Plugin Internal Slip finds its Plugin Internal Lock pairing at startup, before any frame, so
  its "Borrowed from Wheel Lock's learning" share is right straight after a restart.
- **Matured evidence is kept** (owner left it to us): it is bounded by its own decay and the bucket cap below, and
  dropping it would put every light back to red after each restart.
- **No overflow:** counts are `long` on disk and clamped to the in-memory caps (1,000,000) on load; weights are
  stored as equivalent recent samples (at most ~20,000 by the decay); the decay scale is rebuilt on load.
- **Load / save cost:** a summary is about 20 numbers per distribution; an older file with more than 1024 buckets
  in one distribution, or any malformed value, is rejected for that distribution only. The save runs every 300 frames (with the ShakeIt
  calibration) and at End, and re-snapshots only the keys driven since the last save; untouched keys reuse
  their snapshot object. Snapshots are never mutated after export, so the background writer is safe.
- An older file has no evidence: SMax starts from its saved ceiling at maturity 0, as before.

## Follow-up (same day, owner's F1 / FH6 test)

- **Indicator fix.** F1 showed Max 33% while Best / Min read "not learned" and all three still showed the
  starting 5/8/10: Max counted confirmation progress while Best / Min counted curve coverage, but none of the
  three values moves until the peak is confirmed. Now, before confirmation, all three show the same progress
  and say it is still the starting value (table above).
- **Unit-aware key data heading** (`SettingsHub.SlipAngleKeyDataHeaderKey`, bound as `KeyDataHeader` on the tab
  and the ShakeIt panel). Every source is turned into degrees, from different things:

  | Case | Heading |
  |---|---|
  | degrees (table or `ByAngle`) / no game | Key data points (Raw.All, degrees) |
  | radians stated by the table | ... degrees, converted from radians |
  | nothing states the unit (being checked) | ... degrees, read as radians while the unit is checked |
  | checked as radians (`AngleUsingRadians`) | ... degrees, converted from radians |
  | slip ratio 0-1 (`AngleUsingRatio1`, e.g. the Forza games) | ... degrees from slip ratio 0-1: angle = 20% x value x 100 |
  | slip ratio 0-100 | ... degrees from slip ratio 0-100: angle = 20% x value |
  | car-angle fallback | Key data points (car body angle, degrees) |
  | Custom source | Key data points (Raw.All from your source, read as degrees) |

- **New log columns:** `Diag.SlipAngle.Crossings`, `Diag.SlipAngle.PeakConfirmed`, `Diag.SlipAngle.CurvePeakG`,
  `Diag.SlipAngle.Identity` (the selected key, active surface bucket) - so a log shows why a window did or did
  not confirm.

## Restart comparison (28 logs, save and restore half-way, current build)

Restores identical 28 / 28. Final values identical in 15, within 2.6 points in 9. Outliers: F1 222049 Lock 18.9 vs
46.6 (the end-of-log collapse - 1.1.0 also drops to 15.1), 1.0.6.6 Raw Slip 23.0 vs 32.8, a wet Raw Slip 44.3 vs
49.8, Lock S75 up to 4.8.

## Game switch in one session: the frozen Lock LED, and the display following the key (2026-11-22)

Owner-reported: same source, FH6 learned some data, then F1 (never run) in the same session. After a full lap Slip
and Slip Angle moved on, but the Wheel Lock SMax never changed and its LED stayed Orange 1%; restarting SimHub
showed the learned SMax at 100%. Separately, on a fresh install the plugin settings pages stopped showing after the
game switch (the ShakeIt Effects pages were fine).

- **Cause.** The settings page and the ShakeIt panels read the Lock / Slip learners from the UI thread while the data
  thread writes them, with no synchronisation. A read that walks a histogram the frame is filling throws "collection
  was modified". Reproduced: while Lock learns F1, **0 of 193,931** reads succeeded. The value read failed every time
  (the SMax never moved); the maturity read fell back to its first cached answer (the 1% that was true at F1's first
  sample). Lock is hit hardest because it writes on almost every frame; Slip writes less often (crossing gate), so
  some of its reads got through; Slip Angle never had the problem - its reads already share the data thread's lock.
  Restarting "fixed" it because the reloaded learning is read before any frame writes.
- **Fix.** Every frame (`DataUpdate`) and every UI read of the learners (`GetLearnedKeyDataForDisplay`,
  `GetLearnedKeyDataMaturityForDisplay`, `ManualGateReadyForDisplay`, the shutdown saves) take one engine lock. The
  UI only posts to its dispatcher asynchronously, so a frame never waits on the UI thread while holding it; a UI
  read waits at most one frame. The stale-answer cache is gone. Learning is unchanged.
- **The settings pages.** Each failed read was caught by the page's guard and logged ("settings UI action failed"),
  refreshing the health list, about once a second per section while Lock learned. That is the plugin's only fault
  found on this path and it is removed; whether it is what stopped SimHub's other plugin pages is not provable
  without SimHub's log of that session.
- **The display follows the key at once (owner).** A game or car change moves the key data display to the new key
  immediately - at the menu, paused, before any learnable frame - so a key with no real data shows its borrowed half
  LED and "Borrowed from ..." straight away, instead of the previous game's own values until the first driven frame.
  A car not yet known (a new game at its menu) is shown as no car. Display only: no learner is touched.
  (`LearnedDisplayThreadSafetyTests`.) Slip Angle follows the same rule (owner, 2026-11-22;
  `SlipAngleKeyDataContext.Follow`): on the Sealed surface, with the borrowed window and half LED its borrowing rules
  allow, else the shipped start; a new game also forgets the previous game's identity tier until its own is
  confirmed. (`KeyedSlipAngleLearnerTests.At_the_menu_a_car_switch_shows_the_new_keys_borrowed_or_shipped_start_at_once`.)

## Tests

`LearningIndicatorTests` (percent / colour bands / tooltips / Chinese texts, the four view models: Auto only,
hidden rows, Auto toggle and game change at once, nothing known), `ScaleEvidencePersistenceTests` (restart
mid-learning keeps ceiling and maturity, learning continues after a restart, per car and surface, a v13 file,
snapshot reuse, 256-bucket cap and percentile accuracy, export speed with 40 keys, counts beyond int clamped,
malformed and oversized data rejected, v14 Json round trip, older file, the summary reads back every saved
percentile exactly, a restored percentile keeps moving, old bucket lists load, restart = continue, an older file
keeps the cold blend, startup pairing), `KeyDataMaturityOriginTests` (every origin's share and tooltip, the half LED -
Red | borrowed colour - only with no real data; the first real data, and learning reloaded after a restart, are
its own: normal colour and label), `SlipAngleWindowLearnerTests` (Max maturity by consistent crossings, survives a restart,
keyed Best/Min never above Max).
