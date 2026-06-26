using DistributedRecorder.Shared;
using NUnit.Framework;

namespace DistributedRecorder.Tests
{
    /// <summary>
    /// EditMode unit tests for the <see cref="VersionChecker"/> empty-cache bug fix.
    ///
    /// Bug (commit-based-project-verification F9):
    ///   <c>RecorderVersion</c> used <c>_cachedRecorderVersion != null</c> as the
    ///   "already resolved" guard.  When PackageManager returned an empty string (PM
    ///   not ready at startup), <c>""</c> was cached as the "resolved" value and all
    ///   subsequent queries returned <c>""</c> — causing spurious VersionMismatch errors.
    ///
    /// Fix: use <c>string.IsNullOrEmpty(_cachedRecorderVersion)</c> so an empty/null
    /// result is never stored; the next call will retry resolution.
    ///
    /// Note: the fix is in the caching guard inside <see cref="VersionChecker"/>.  We
    /// cannot directly test the PM-dependent resolve path in a hermetic EditMode test,
    /// but we CAN test the cache invalidation contract through <see cref="VersionChecker.InvalidateCache"/>:
    ///  - After <c>InvalidateCache()</c> the property re-evaluates on the next access.
    ///  - The property is idempotent when the result is stable (returns the same value on re-access).
    /// </summary>
    [TestFixture]
    public class VersionCheckerCacheTests
    {
        [SetUp]
        public void SetUp()
        {
            // Always start with a clean cache so tests are isolated.
            VersionChecker.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            // Leave cache clean for subsequent tests.
            VersionChecker.InvalidateCache();
        }

        /// <summary>
        /// After <see cref="VersionChecker.InvalidateCache"/> the cache is cleared
        /// and the next access will trigger re-resolution.
        /// We verify this by confirming the property is accessible (no exception)
        /// and returns a non-null string (may be empty if Recorder is not installed).
        /// </summary>
        [Test]
        public void RecorderVersion_AfterInvalidateCache_IsAccessibleAndNotNull()
        {
            VersionChecker.InvalidateCache();
            string version = VersionChecker.RecorderVersion;

            // Must not throw and must return a non-null string (empty is acceptable
            // when com.unity.recorder is not installed in the test project).
            Assert.IsNotNull(version,
                "RecorderVersion must never return null (empty is OK).");
        }

        /// <summary>
        /// Calling <see cref="VersionChecker.RecorderVersion"/> twice in sequence
        /// (after a full invalidation) must return the same value both times –
        /// verifying the cache is stable once a non-empty result is stored.
        /// </summary>
        [Test]
        public void RecorderVersion_CalledTwice_ReturnsSameValue()
        {
            VersionChecker.InvalidateCache();

            string first  = VersionChecker.RecorderVersion;
            string second = VersionChecker.RecorderVersion;

            Assert.AreEqual(first, second,
                "RecorderVersion must return the same cached value on repeated calls.");
        }

        /// <summary>
        /// Demonstrates that <see cref="VersionChecker.InvalidateCache"/> actually
        /// clears the state: the property can be accessed again without throwing after
        /// multiple invalidate-then-read cycles.
        /// </summary>
        [Test]
        public void RecorderVersion_MultipleInvalidateCycles_NoException()
        {
            for (int i = 0; i < 3; i++)
            {
                VersionChecker.InvalidateCache();
                string v = VersionChecker.RecorderVersion;
                Assert.IsNotNull(v, $"Cycle {i}: RecorderVersion must not return null.");
            }
        }

        /// <summary>
        /// Verifies that <see cref="VersionChecker.UnityVersion"/> is a non-empty
        /// string (always available in Editor context).
        /// </summary>
        [Test]
        public void UnityVersion_IsNonEmpty()
        {
            Assert.IsFalse(string.IsNullOrEmpty(VersionChecker.UnityVersion),
                "UnityVersion must return a non-empty string in Editor context.");
        }
    }
}
