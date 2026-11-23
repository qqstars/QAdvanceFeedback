# Internal source always learns, every source isolated — design

Date: 2026-11-19. Status: approved by the owner ("G yes to do", 2026-11-19); **implemented 2026-11-19**
as Task G of `docs\shakeit-source-plan.md` (as-built notes in §6). Marked **[call]** = assistant decision, open for the owner to revisit.

## 1. Rule (owner)

- Every source has its own learner, fully isolated: one source's evidence never feeds, seeds or
  borrows from another source's.
- **Our own internal source always learns**, whether it is selected or not.
- **Every other source learns only while it is the active source** (and, for ShakeIt / Viper, only
  while its values are actually arriving — Task C's per-channel Raw fallback).
- Saved learning keeps the full key — **game, car, source, surface** (Slip Angle also **target**) —
  in the runtime parameter file, so nothing is pooled across any of those dimensions.

"Internal source": Wheel Lock / Wheel Slip → Plugin Internal (our Layer 3 Raw); Slip Angle → Plugin
Internal (native slip angle where the game provides it, our computed angle otherwise — two keys,
`SlipAngle.PluginInternal.Native` / `.Fallback`, because their scales differ). G-Force and Clutch Bite
have a single source each, so the rule changes nothing there.

## 2. Today (found 2026-11-19)

- Keys are `game|#|car|#|source|#|surface` (`KeyedGripLearner.MakeKey`, `ColdStartTierResolver.Key`);
  Slip Angle adds the target into the source segment. Borrowing (cold-start tiers) is already
  restricted to the same source.
- **Gap 1 — Lock/Slip internal learning is split.** The Plugin Internal key
  (`RawSourceFallback.RawIdentity`) grows only while Plugin Internal is selected or a channel has fallen
  back to Raw. A separate always-fed copy under `__layer3_raw_fallback__`
  (`NormalizedWheelLockSlipEngine.RawFallbackSourceIdentity`, scale learner + lock anchors) exists only
  for the "configured source went quiet" check and is never read by Plugin Internal.
- **Gap 2 — Custom can share the internal key.** The Plugin Internal key is `SourceIdentity.Compute`
  over our four Raw property names; a Custom source with exactly those four plain rows produces the
  same key (and its Custom cold-start reference would then apply to it).
- **Gap 3 — Slip Angle Plugin Internal does not learn while Custom is selected.**
- By design NOT source-keyed (unchanged): the physical g reference (`LockPhysicalReference` /
  `SlipPhysicalReference`, game+car) and surface support — they learn from the car's own motion, not
  from any source's values.

## 3. Design

### 3.1 Wheel Lock / Wheel Slip
- One internal key per channel: `RawSourceFallback.RawIdentity(isLock)`. Every source-keyed learning
  family (grip learner, scale learner, lock anchors, crossing/kick-in gate state, crash protection,
  maturity) is fed **every frame** for that key from Layer 3 Raw, through exactly the same gates,
  thresholds and surface blend used when Plugin Internal is the active source — so its result is
  identical whether it was selected or not.
- When Plugin Internal is the active source (or a channel has fallen back to Raw), the active path and
  the always-on path are the same learner and it is fed **once** per frame.
- The active non-internal source (ShakeIt / Viper / Custom) is fed only under its own key, only while
  active and delivering values.
- The "configured source went quiet" check compares against the internal key (no separate hidden key).
- `__layer3_raw_fallback__` is retired: **migration on load** — for each (game, car, surface) where
  the internal key has no evidence, the hidden key's state becomes the internal key's state; where
  both exist the internal key's data is kept **[call]**; the hidden entries are then dropped from the
  saved file (no other key is touched).
- **Custom matching Raw**: a Custom channel whose four rows are exactly our Raw properties (plain) is
  treated as Plugin Internal — learns under the internal key, Custom cold-start reference not applied
  **[call]** (the alternative, prefixing every Custom key, would reset every existing Custom user's
  learning).

### 3.2 Slip Angle
- Plugin Internal learns every frame under its own key (Native or Fallback, whichever the frame's raw
  angle is), with the same gates as when selected (trigger, drift, landing, crash protection,
  surface, target Front/Rear/All).
- Custom learns only while selected, under its own composite key; the internal keys are reserved
  literals no Custom identity can produce.

### 3.3 Saved file
- Every saved entry keeps its full key (game, car, source, surface; Slip Angle target). No new pooled
  or source-less entries are written. Existing entries load unchanged apart from the one-time
  `__layer3_raw_fallback__` merge above.

### 3.4 Shared (non-source-keyed) state follows car motion + the internal source (owner, 2026-11-19)
- The physical g reference (game+car) admits speed-bucket frames with the **internal** source's
  at-limit confidence, and the Slip Angle surface classifier is stepped once per frame from the
  **Plugin Internal** angle and the internal key's curves — whatever source is selected. Both are
  observed exactly once per frame.
- Why: the internal learning must not depend on which source is active; the internal source is
  always present, so these references keep maturing on every frame and stay identical across source
  switches (previously a flat/quantised external source could leave the speed buckets cold, and a
  Custom angle that went missing froze the surface classification).
- Accepted consequence (owner: "we are good for that"): ShakeIt / Viper / Custom output can change
  slightly — the speed-aware peak (and so the active source's S75/S90 anchors and four-range curve)
  for sources with a flat/quantised/stepping-down series, and Custom Slip Angle's surface bucket
  (now the internal classification; games that report surface are unaffected).
- On any change of a channel's active source identity (including into and out of the Raw fallback),
  the shared crossing gate is reset, the shared at-limit basis cleared and the previous source's
  Lock-anchor run closed, so no source's pre-switch trend or snapshot can teach another key; per-key
  counters are kept.

## 4. Threading / cost
Runs on the data thread inside the existing guards; never throws. Cost: one extra learner update per
channel per frame while a non-internal source is active (negligible).

## 5. Tests
Internal key grows while ShakeIt / Viper / Custom is active; its learned state is identical with and
without being selected (same frame sequence); a non-active source never grows; internal fed once per
frame when selected or fallen back; save → load round-trip keeps every key's own data and full key
shape (game/car/source/surface, target); migration of the hidden key (internal empty → adopted; both
present → internal kept; hidden dropped); Custom-matching-Raw uses the internal key and no Custom
cold start; Slip Angle Plugin Internal grows while Custom is active, Custom doesn't grow while
Plugin Internal is active; no change for G-Force / Clutch Bite.

## 6. As built (2026-11-19)

- **How the internal key is fed.** One integrated pass per channel, not a second engine call: at every
  point where the active path learns (or makes a state-changing read) for its own key, an "internal
  mirror" does the same step for `PluginInternalSourceIdentity.For(isLock)` (equal to
  `RawSourceFallback.RawIdentity`) with Layer 3 Raw as its input, its own crossing gate and its own
  at-limit basis history. When the active identity IS the internal one (Plugin Internal, Custom matching
  Raw, or the ShakeIt / Viper Raw-fallback route) there is no mirror and the key is fed once.
  `__layer3_raw_fallback__` survives only as `InternalSourceKeyMigration.RetiredHiddenSourceIdentity`,
  recognised by the one-time load migration (evidence: scale = finite cold ceiling > 0; anchors = any
  level > 0; grip = samples > 0).
- **Same-type cross-channel borrow.** Slip's cold-start borrow from Lock (`KeyedScaleLearner`'s
  cross-channel fallback) pairs only the same source type (`Core\Normalized\CrossChannelSourcePairing.cs`):
  Slip internal ↔ Lock internal, always, whatever Lock has selected; Slip ShakeIt ↔ the active Lock
  identity only when it is ShakeIt too, and Viper ↔ Viper likewise (paired by type, so each channel keeps
  its own algorithm fingerprint); Custom, mixed or unknown identities — no borrow. **Output change for
  mixed setups:** for example Slip ShakeIt with Lock on Viper / Plugin Internal / Custom (or with Lock on
  its Raw fallback route) no longer borrows; while cold, such a Slip channel starts from its own Tier-1 /
  borrowed-tier anchor instead of one pulled toward Lock's evidence. Matched setups are unchanged. The
  ceilings persisted by `ExportAll` follow the same rule.
- **Identity-change resets.** On any change of a channel's active identity (including into and out of
  the Raw fallback) the shared crossing gate, the shared at-limit basis and the smoothed fallback
  divergence are cleared, and the previous source's Lock-anchor run is closed **under that source's own
  game/car** (a car and source change on the same frame closes it under the previous car). The run is
  **not** closed when the previous identity is the internal key: that key's run continues on the
  internal pass. A switch never resets the internal key's own gate/basis. Per-key counters are kept.
- **Thread-safe pairing.** The pairing inputs (active Lock identity, both internal identities) live in an
  immutable snapshot that only the data thread replaces (`Volatile.Write`) when an input changes; each
  snapshot owns its own concurrent answer cache, so a UI-thread read can never see a stale pairing after
  a switch.
- **Grip-learner persistence fix.** The save snapshot (`RuntimeCache.CloneLearners`) dropped five
  `GripLearnerState` fields documented as persisted: `RaiseCandidateG`, `RaiseCandidateHits`,
  `LowerCandidateG`, `LowerCandidateHits` and `QuietStreak`. They are now saved (grip learners and
  physical references). Old files load as before; learners now resume warm after a restart.
- **Custom matching a known source's properties.** A Custom channel whose four plain rows are exactly
  our Raw properties is Plugin Internal (no Custom cold start). Likewise, a Custom channel whose four
  plain rows are exactly the legacy ShakeIt export property names produces the ShakeIt identity string,
  so it shares the ShakeIt key (its learning and key data). Generally, a Custom channel that reproduces
  a shipped preset never applies the Custom cold-start reference: `ApplyCustomColdStart` skips it when
  `RawSourceFallback.MatchesShippedPreset(identity, isLock)` is true. That covers our Raw identity, the
  canonical ShakeIt identity (compared with the fingerprint stripped) and the shipped Viper NCalc
  preset; a scripted row's identity is a hash of its text, so the same four scripts reproduce it. So a
  Custom cold-start SMax never reaches the Raw, ShakeIt or Viper key.
- **Slip Angle.** Plugin Internal's angle is computed every frame whatever is selected; the shared
  surface classifier is stepped once from it, Plugin Internal learns into that blend, and Custom learns
  (only while selected) into the same blend under its own key.
- **Known limitation (pre-existing).** The UI thread's `PublishedCeiling` read still walks
  `KeyedScaleLearner`'s own (non-thread-safe) dictionaries while the data thread writes them. This predates
  this work.
- Tests: `InternalSourceLearningTests`, `InternalSourceKeyMigrationTests`,
  `InternalSourcePersistenceRoundTripTests`, `CrossChannelSourcePairingTests`,
  `SlipAngle\SlipAngleInternalSourceLearningTests`.
