# G-Force Test Effect: drag behaviour, layout, sweep speed (2026-11-20)

Owner requests, 2026-11-20.

## Sweep speed default 1.0 -> 0.6

`GForceSettings._transientGain` and `GForceEngine.TransientGain` default to 0.6. A saved value is kept.
README (en/zh) updated. Engine tests pass unchanged at the new default.

## Layout: one column under the pad

`GForceTestView.xaml` (the ShakeIt G-Force panel and the plugin page): the G-Force pad, then the
Lock/Slip bar, then the Slip Angle bar, each with its caption underneath, in one 320 px column.
Before, the three sat in a WrapPanel (~950 px); in the ~620 px ShakeIt panel the Slip Angle bar wrapped
under the pad, so the pad's "G-Force" caption read as that bar's name and the two bars did not line up.
New screenshot: `docs\images\shakeit-gforce-test(.zh-Hans).png`.

## Drag behaviour (`GBallPad`, `WheelBar` in `Ui\Views\TestControls.cs`)

Before, the ball was released only by the control's own button-up; when the surrounding ShakeIt list
(drag-and-drop) took the press or the capture, that never came and the ball followed the mouse until
the next click.

- Held only while the left button is down (`Core\ShakeItEffects\TestDrag.cs`, `TestDragHold`).
- The press and release are marked handled, so the surrounding list cannot take them.
- The drag ends - once, snapping once - on: the control's button-up (the press captures the mouse, so
  this arrives wherever the cursor is), a mouse move that finds the button already up, a lost capture,
  and as a backstop the window's own button-up or the window losing focus (`WindowReleaseWatch`, watched
  only while held). So a release outside the pad or bar always ends it.
- Outside the control the ball stays at the furthest reachable position (`TestDragMath.PadAxis` / `Bar01`
  clamp).
- Released within one ball radius of the centre the pad ball snaps to the centre; a bar ball within one
  ball radius of the left end snaps to 0 (`TestDragMath.PadSnapsToCentre` / `BarSnap`).

## Tests

`TestDragTests` (hold/let-go, clamp, snap, source pins for both controls and the layout);
`GForceSettingsTests` default updated.

## Follow-up (2026-11-20): ShakeIt row drag, label, unsupported warning

**Ball stopped following the mouse.** SimHub's ShakeIt effect list is a GongSolutions (3.0) drag source.
Its tunnelling handlers see the press first and, after a few pixels of movement, start a row drag that
takes the mouse away from the pad or bar; the lost capture then ended our drag. Both controls now set
Gong's `DragSourceIgnore` on themselves (`TestLook.IgnoreListDrag`). The property inherits, so the ball
and frame are covered, and Gong decides at the press, so moving outside the control afterwards cannot
start a row drag either. Gong ships only inside SimHub, so the property is looked up by name (retried
until found); without it the call does nothing.

**"Use legacy iRacing algorithm" overlapped its toggle.** Shortened to "Legacy iRacing" / "旧版iRacing".

**No "not supported" note in Counter-Strike.** ShakeIt only hands our effects calibrated data when the
game provides car feedback data, so in Counter-Strike the tap never ran, the channel was never
"driving", and the state stayed Set up / idle (no note). The plugin now falls back to ShakeIt's own
check, `WheelSlipEffect.GetIsSupportedOnCurrentGame` on `GameManager.GetFeedbackCapabilities()`, when
the tap has no verdict for the game, and the state machine takes a reported "unsupported" whenever the
game is running, not only while driving. Racing games without feedback data for a moment (loading,
garage, tow) still report supported, so they stay idle as before.

Tests: `TestDragTests` (drag-ignore pin, label), `ShakeItSourceAvailabilityTests` (no-feedback game
unsupported at once, held at menu/pause, cleared on a game switch, supported/unknown stays idle, no game
is not a verdict, plugin fallback pin).

## Follow-up (2026-11-20): alignment

**ShakeIt panels.** Toggles are no longer shifted so their switch lines up with the column: the whole
toggle, ON/OFF text first, now starts where the dropdowns and editors start. The G-Force lateral
dropdown and Test Effect rate editor, which were indented to the switch, start there too. The
`TrackAlignment` helper is gone.

**Settings tabs.** Each tab already had one shared label column (`ChannelAlign` / `SlipAngleAlign`);
the Trigger threshold rows (Slip's brake and throttle now one under the other), the Source dropdown
row, the ShakeIt algorithm rows and the Slip floor row now use it, so every editor, dropdown and
toggle in a tab starts at the same x. The key data and Custom reference editors, centred in their
wider shared editor column (6 px right of the rest), are pinned left. Screenshots regenerated.

Tests: `AlignmentLayoutTests`.

## Follow-up (2026-11-20): unsupported for the selected game

The capability check above only ran, and its "unsupported" was only accepted, while a game was
running, so it never fired with Counter-Strike selected but not running. ShakeIt itself decides from
the SELECTED game: on game load it calls `GameManager.GetFeedbackCapabilities()` and each effect's
`GetIsSupportedOnCurrentGame` on them, which hides Lock/Slip from Add Effect and marks them
unsupported in its list. The plugin now runs that check whenever a game is selected (running or not),
and the state machine takes the verdict for the selected game whether or not it is running; no game
selected is never a verdict, and an effect must still be in the profile (else Not set up).
