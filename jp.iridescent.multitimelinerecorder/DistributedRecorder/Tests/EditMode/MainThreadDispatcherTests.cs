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
    }
}
