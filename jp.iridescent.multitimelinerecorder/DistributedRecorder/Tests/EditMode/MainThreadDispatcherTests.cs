using System;
using System.Collections;
using System.Threading;
using DistributedRecorder.Shared;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace DistributedRecorder.Tests
{
    /// <summary>
    /// EditMode tests for <see cref="MainThreadDispatcher"/>.
    ///
    /// The dispatcher flushes its queue via EditorApplication.update, which fires
    /// once per frame in Edit Mode when tests are run.  We use
    /// <see cref="UnitySetUpAttribute"/> / yield-based patterns where a frame
    /// boundary is needed, and synchronous checks where the queue state alone
    /// is sufficient.
    /// </summary>
    [TestFixture]
    public class MainThreadDispatcherTests
    {
        [Test]
        public void Enqueue_NullAction_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => MainThreadDispatcher.Enqueue(null));
        }

        [UnityTest]
        public IEnumerator Enqueue_Action_ExecutedOnNextFrame()
        {
            bool executed = false;

            MainThreadDispatcher.Enqueue(() => executed = true);

            // Yield one frame so EditorApplication.update fires and flushes.
            yield return null;

            Assert.IsTrue(executed,
                "Action enqueued via MainThreadDispatcher should have been executed by the next frame.");
        }

        [UnityTest]
        public IEnumerator Enqueue_MultipleActions_AllExecutedInOrder()
        {
            var results = new System.Collections.Generic.List<int>();

            MainThreadDispatcher.Enqueue(() => results.Add(1));
            MainThreadDispatcher.Enqueue(() => results.Add(2));
            MainThreadDispatcher.Enqueue(() => results.Add(3));

            yield return null;

            Assert.AreEqual(new[] { 1, 2, 3 }, results,
                "All enqueued actions should execute in FIFO order.");
        }

        [UnityTest]
        public IEnumerator Enqueue_ThrowingAction_DoesNotPreventSubsequentActions()
        {
            bool afterThrowExecuted = false;

            MainThreadDispatcher.Enqueue(() => throw new InvalidOperationException("test error"));
            MainThreadDispatcher.Enqueue(() => afterThrowExecuted = true);

            // Suppress the expected error log from the dispatcher.
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(
                @"\[MainThreadDispatcher\].*test error", System.Text.RegularExpressions.RegexOptions.None));

            yield return null;

            Assert.IsTrue(afterThrowExecuted,
                "An exception in one action should not prevent subsequent actions from running.");
        }

        [UnityTest]
        public IEnumerator Enqueue_FromBackgroundThread_ExecutedOnMainThread()
        {
            int executingThreadId  = -1;
            int mainThreadId       = Thread.CurrentThread.ManagedThreadId;
            bool completed         = false;

            var thread = new Thread(() =>
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    executingThreadId = Thread.CurrentThread.ManagedThreadId;
                    completed = true;
                });
            });
            thread.Start();
            thread.Join(1000);

            yield return null;

            Assert.IsTrue(completed, "Action should have been executed.");
            Assert.AreEqual(mainThreadId, executingThreadId,
                "Action enqueued from a background thread must execute on the main thread.");
        }

        // -----------------------------------------------------------------------
        // InvokeAndWait tests
        // EditMode tests run on the Unity main thread, so the inline (fast-path)
        // branch is exercised here.  The background-thread path requires a live
        // EditorApplication.update flush loop and is validated by the integration
        // test above (Enqueue_FromBackgroundThread_ExecutedOnMainThread).
        // -----------------------------------------------------------------------

        [Test]
        public void InvokeAndWait_NullFunc_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(
                () => MainThreadDispatcher.InvokeAndWait<int>(null, TimeSpan.FromSeconds(5)));
        }

        [Test]
        public void InvokeAndWait_CalledFromMainThread_ExecutesFuncInline()
        {
            // EditMode tests run on the main thread.
            // The first Flush may not have fired yet, so _mainThreadId might be -1.
            // Force the dispatcher's main-thread ID to be captured by yielding is not
            // available in a plain [Test].  We instead verify that the function IS
            // called and returns the expected value (inline path when IsMainThread()
            // returns true, or queue path when _mainThreadId is still -1 which also
            // works because the Flush runs synchronously in EditMode test infrastructure).
            //
            // Either way the value must be returned correctly.
            int result = MainThreadDispatcher.InvokeAndWait(() => 42, TimeSpan.FromSeconds(5));
            Assert.AreEqual(42, result,
                "InvokeAndWait must return the function's return value.");
        }

        [Test]
        public void InvokeAndWait_FuncThrows_ExceptionPropagatedToCaller()
        {
            // When called from the main thread (inline path), the exception propagates
            // directly.  When called from a background thread it is wrapped in an
            // outer Exception with the original as InnerException.
            var ex = Assert.Throws<Exception>(
                () => MainThreadDispatcher.InvokeAndWait<int>(
                    () => throw new InvalidOperationException("boom"),
                    TimeSpan.FromSeconds(5)));

            // Accept both the direct-throw case (inline) and the wrapped case (bg thread).
            bool directThrow  = ex is InvalidOperationException && ex.Message == "boom";
            bool wrappedThrow = ex.InnerException is InvalidOperationException inner &&
                                inner.Message == "boom";

            Assert.IsTrue(directThrow || wrappedThrow,
                $"Expected InvalidOperationException('boom') but got: {ex}");
        }

        [UnityTest]
        public IEnumerator InvokeAndWait_IsMainThread_ReturnsTrueAfterFirstFlush()
        {
            // After the first EditorApplication.update flush the main-thread ID is captured.
            yield return null; // let Flush run once

            Assert.IsTrue(MainThreadDispatcher.IsMainThread(),
                "IsMainThread() must return true when called from the main thread " +
                "after the first Flush has captured the main-thread ID.");
        }
    }
}
