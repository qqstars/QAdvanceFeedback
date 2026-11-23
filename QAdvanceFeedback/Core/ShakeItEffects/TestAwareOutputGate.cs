namespace QAdvanceFeedback.Core.ShakeItEffects
{
    /// <summary>
    /// When a native QAdvanceFeedback ShakeIt effect may output: under ShakeIt's own in-game gate (game
    /// running, not paused, not in a menu, not spectating) - exactly like the property-based Custom
    /// Effects - or while that effect's Test Effect runs (docs\shakeit-test-effect-mvvm-design.md). The
    /// effects are "allowed out of game" in ShakeIt only so a test can play; this gate keeps every other
    /// out-of-game moment silent instead of holding the last frame.
    /// </summary>
    public static class TestAwareOutputGate
    {
        public static bool Allows(bool testActive, bool gameRunning, bool gamePaused, bool gameInMenu, bool spectating) =>
            testActive || (gameRunning && !gamePaused && !gameInMenu && !spectating);
    }
}
