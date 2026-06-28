using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using DistributedRecorder.Worker;

namespace DistributedRecorder.Tests.Worker
{
    /// <summary>
    /// Hermetic EditMode unit tests for <see cref="PlayModeReloadGuard"/>.
    ///
    /// Covers plan.md §F-2 (EditorSettings 退避/復元の純ロジック):
    ///   - Enable → sets DisableDomainReload.
    ///   - Restore → returns to the original saved value (exact bit match).
    ///   - Round-trip idempotency for various initial options values.
    ///   - Enable when already active is a no-op (guard flag check).
    ///   - Restore when not active is a no-op.
    ///   - Sanity-restore path (IsActive true before RunWithConfig).
    ///   - Pure-function helpers ApplyDisableDomainReload and RestoreIsIdempotent.
    ///
    /// All tests restore EditorSettings in [TearDown] to ensure they are hermetic.
    /// </summary>
    [TestFixture]
    public class PlayModeReloadGuardTests
    {
        // Saved originals so TearDown can always restore even if a test throws
        private bool _originalEnabled;
        private EnterPlayModeOptions _originalOptions;

        [SetUp]
        public void SetUp()
        {
            // Capture real Editor state before each test
            _originalEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _originalOptions  = EditorSettings.enterPlayModeOptions;

            // Ensure guard is cleared before each test
            EditorPrefs.DeleteKey(PlayModeReloadGuard.KeyGuardActive);
            EditorPrefs.DeleteKey(PlayModeReloadGuard.KeyOptionsEnabled);
            EditorPrefs.DeleteKey(PlayModeReloadGuard.KeyOptions);
        }

        [TearDown]
        public void TearDown()
        {
            // Always restore real Editor state and clear guard
            EditorSettings.enterPlayModeOptionsEnabled = _originalEnabled;
            EditorSettings.enterPlayModeOptions         = _originalOptions;

            EditorPrefs.DeleteKey(PlayModeReloadGuard.KeyGuardActive);
            EditorPrefs.DeleteKey(PlayModeReloadGuard.KeyOptionsEnabled);
            EditorPrefs.DeleteKey(PlayModeReloadGuard.KeyOptions);
        }

        // ------------------------------------------------------------------
        // Tests: Enable adds DisableDomainReload
        // ------------------------------------------------------------------

        [Test]
        public void Enable_SetsDisableDomainReloadBit()
        {
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions        = EnterPlayModeOptions.None;

            PlayModeReloadGuard.Enable();

            bool hasBit = (EditorSettings.enterPlayModeOptions
                           & EnterPlayModeOptions.DisableDomainReload) != 0;
            Assert.IsTrue(hasBit,
                "Enable() must add DisableDomainReload to enterPlayModeOptions.");
        }

        [Test]
        public void Enable_SetsOptionsEnabled()
        {
            EditorSettings.enterPlayModeOptionsEnabled = false;
            EditorSettings.enterPlayModeOptions        = EnterPlayModeOptions.None;

            PlayModeReloadGuard.Enable();

            Assert.IsTrue(EditorSettings.enterPlayModeOptionsEnabled,
                "Enable() must set enterPlayModeOptionsEnabled = true.");
        }

        [Test]
        public void Enable_SetsGuardActiveFlag()
        {
            PlayModeReloadGuard.Enable();

            Assert.IsTrue(PlayModeReloadGuard.IsActive,
                "Enable() must set the guard-active flag in EditorPrefs.");

            // Cleanup so TearDown does not double-restore
            PlayModeReloadGuard.Restore();
        }

        // ------------------------------------------------------------------
        // Tests: Restore returns to the original value
        // ------------------------------------------------------------------

        [Test]
        [TestCase(EnterPlayModeOptions.None,              false)]
        [TestCase(EnterPlayModeOptions.DisableSceneReload, true)]
        [TestCase(EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload, true)]
        public void Restore_ReturnsToOriginalOptions(EnterPlayModeOptions initial, bool initialEnabled)
        {
            EditorSettings.enterPlayModeOptionsEnabled = initialEnabled;
            EditorSettings.enterPlayModeOptions        = initial;

            PlayModeReloadGuard.Enable();
            PlayModeReloadGuard.Restore();

            Assert.AreEqual(initial, EditorSettings.enterPlayModeOptions,
                $"Restore() must return enterPlayModeOptions to {initial}.");
            Assert.AreEqual(initialEnabled, EditorSettings.enterPlayModeOptionsEnabled,
                $"Restore() must return enterPlayModeOptionsEnabled to {initialEnabled}.");
        }

        [Test]
        public void Restore_ClearsGuardFlag()
        {
            PlayModeReloadGuard.Enable();
            PlayModeReloadGuard.Restore();

            Assert.IsFalse(PlayModeReloadGuard.IsActive,
                "Restore() must clear the guard-active flag.");
        }

        // ------------------------------------------------------------------
        // Tests: Round-trip idempotency (boundary: DisableDomainReload already set)
        // ------------------------------------------------------------------

        [Test]
        public void RoundTrip_WhenDisableDomainReloadAlreadySet_OriginalIsPreserved()
        {
            // If the user already had DisableDomainReload enabled, Enable() should
            // OR-assign (no change) and Restore() should bring it back to the same.
            var initial = EnterPlayModeOptions.DisableDomainReload;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions        = initial;

            PlayModeReloadGuard.Enable();
            PlayModeReloadGuard.Restore();

            Assert.AreEqual(initial, EditorSettings.enterPlayModeOptions,
                "Round-trip must preserve DisableDomainReload when it was already set.");
        }

        // ------------------------------------------------------------------
        // Tests: Enable idempotency (double-Enable is a no-op)
        // ------------------------------------------------------------------

        [Test]
        public void Enable_WhenAlreadyActive_IsNoOp()
        {
            EditorSettings.enterPlayModeOptionsEnabled = false;
            EditorSettings.enterPlayModeOptions        = EnterPlayModeOptions.None;

            PlayModeReloadGuard.Enable();

            // Change the options after Enable to simulate an interleaved modification
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableSceneReload;

            // Second Enable should be ignored (no-op)
            LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex(
                    @"\[PlayModeReloadGuard\] Enable called while already active"));
            PlayModeReloadGuard.Enable();

            // Options should still be DisableSceneReload (not overwritten by second Enable)
            Assert.AreEqual(EnterPlayModeOptions.DisableSceneReload,
                EditorSettings.enterPlayModeOptions,
                "Second Enable() must not overwrite options changed after first Enable().");

            // Cleanup
            PlayModeReloadGuard.Restore();
        }

        // ------------------------------------------------------------------
        // Tests: Restore when not active is a no-op
        // ------------------------------------------------------------------

        [Test]
        public void Restore_WhenNotActive_IsNoOp()
        {
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions        = EnterPlayModeOptions.DisableSceneReload;

            // Guard is not active — Restore should leave options unchanged
            PlayModeReloadGuard.Restore();

            Assert.AreEqual(EnterPlayModeOptions.DisableSceneReload,
                EditorSettings.enterPlayModeOptions,
                "Restore() must not modify options when guard is not active.");
        }

        // ------------------------------------------------------------------
        // Tests: sanity-restore (crash remnant detection)
        // ------------------------------------------------------------------

        [Test]
        public void IsActive_TrueAfterEnable_FalseAfterRestore()
        {
            Assert.IsFalse(PlayModeReloadGuard.IsActive, "Guard must start inactive.");
            PlayModeReloadGuard.Enable();
            Assert.IsTrue(PlayModeReloadGuard.IsActive,  "Guard must be active after Enable().");
            PlayModeReloadGuard.Restore();
            Assert.IsFalse(PlayModeReloadGuard.IsActive, "Guard must be inactive after Restore().");
        }

        [Test]
        public void IsActive_TrueWhenEditorPrefsKeySet_SimulatesCrashRemnant()
        {
            // Simulate what happens after a crash: the EditorPrefs key is still set
            // but the current session has not called Enable().
            EditorPrefs.SetBool(PlayModeReloadGuard.KeyGuardActive, true);
            EditorPrefs.SetBool(PlayModeReloadGuard.KeyOptionsEnabled, false);
            EditorPrefs.SetInt (PlayModeReloadGuard.KeyOptions, 0);

            Assert.IsTrue(PlayModeReloadGuard.IsActive,
                "IsActive must return true when EditorPrefs guard key is set (crash remnant).");

            // Cleanup
            PlayModeReloadGuard.Restore();
        }

        // ------------------------------------------------------------------
        // Tests: pure-function helpers
        // ------------------------------------------------------------------

        [Test]
        [TestCase(EnterPlayModeOptions.None)]
        [TestCase(EnterPlayModeOptions.DisableSceneReload)]
        [TestCase(EnterPlayModeOptions.DisableDomainReload)]
        public void ApplyDisableDomainReload_AlwaysSetsBit(EnterPlayModeOptions input)
        {
            var result = PlayModeReloadGuard.ApplyDisableDomainReload(input);

            bool hasBit = (result & EnterPlayModeOptions.DisableDomainReload) != 0;
            Assert.IsTrue(hasBit,
                $"ApplyDisableDomainReload({input}) must always produce DisableDomainReload bit.");
        }

        [Test]
        [TestCase(EnterPlayModeOptions.None)]
        [TestCase(EnterPlayModeOptions.DisableSceneReload)]
        [TestCase(EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload)]
        public void RestoreIsIdempotent_AlwaysTrue(EnterPlayModeOptions original)
        {
            bool result = PlayModeReloadGuard.RestoreIsIdempotent(original);
            Assert.IsTrue(result,
                $"RestoreIsIdempotent must return true for any original value ({original}).");
        }

        // ------------------------------------------------------------------
        // Tests: DisableSceneReload is NOT added by Enable
        // ------------------------------------------------------------------

        [Test]
        public void Enable_DoesNotAddDisableSceneReload()
        {
            // Snapshot the options BEFORE Enable() so we can detect what was added.
            // Using the current actual EditorSettings value (whatever batchmode initialized it
            // to) rather than forcing it to None avoids platform-specific initialization issues.
            EnterPlayModeOptions before = EditorSettings.enterPlayModeOptions;

            PlayModeReloadGuard.Enable();

            EnterPlayModeOptions after = EditorSettings.enterPlayModeOptions;

            // Enable() must not add DisableSceneReload: the SceneReload bit must not
            // change from its pre-Enable state (either both absent or both present).
            bool sceneBitBefore = (before & EnterPlayModeOptions.DisableSceneReload) != 0;
            bool sceneBitAfter  = (after  & EnterPlayModeOptions.DisableSceneReload) != 0;

            Assert.AreEqual(sceneBitBefore, sceneBitAfter,
                "Enable() must NOT add DisableSceneReload (scene objects must be recreated " +
                "per-job to prevent sticky state leaking into job N+1). " +
                $"before={before}, after={after}");

            PlayModeReloadGuard.Restore();
        }
    }
}
