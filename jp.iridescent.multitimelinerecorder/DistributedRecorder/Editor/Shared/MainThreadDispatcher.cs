using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEditor;

namespace DistributedRecorder.Shared
{
    /// <summary>
    /// Dispatches <see cref="Action"/> delegates to the Unity main thread via
    /// <see cref="EditorApplication.update"/>.
    ///
    /// Background threads (e.g. the HttpListener ThreadPool) enqueue work with
    /// <see cref="Enqueue"/>.  The update hook drains the queue each frame so
    /// Unity Editor-only APIs (AssetDatabase, EditorSceneManager, RecorderController
    /// etc.) are always called from the main thread.
    ///
    /// For background threads that need a synchronous return value, use
    /// <see cref="InvokeAndWait{T}"/> which blocks the calling thread until the
    /// main thread executes the function and returns its result.
    ///
    /// Usage:
    ///   MainThreadDispatcher.Enqueue(() => { /* main-thread-only code */ });
    ///   string hash = MainThreadDispatcher.InvokeAndWait(() => AssetDatabase.GetDependencies(...), TimeSpan.FromSeconds(15));
    ///
    /// The dispatcher auto-registers its hook on first use and does not need
    /// explicit initialisation.
    /// </summary>
    public static class MainThreadDispatcher
    {
        private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        private static volatile bool _hooked;
        private static readonly object _hookLock = new object();

        // Captured once on first Flush call (which always runs on the main thread).
        // Volatile so background threads see the written value immediately.
        private static volatile int _mainThreadId = -1;

        /// <summary>
        /// Enqueues <paramref name="action"/> for execution on the main thread.
        /// Safe to call from any thread.
        /// </summary>
        public static void Enqueue(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            EnsureHooked();
            _queue.Enqueue(action);
        }

        /// <summary>
        /// Executes <paramref name="func"/> on the Unity main thread and returns
        /// its result synchronously to the calling thread.
        ///
        /// <list type="bullet">
        ///   <item><description>
        ///     If the caller is already on the main thread, <paramref name="func"/>
        ///     is invoked directly (no enqueue, no wait).
        ///   </description></item>
        ///   <item><description>
        ///     If the caller is on a background thread, the function is enqueued and
        ///     the caller blocks until either the function completes or
        ///     <paramref name="timeout"/> elapses.
        ///   </description></item>
        /// </list>
        ///
        /// Any exception thrown by <paramref name="func"/> is re-thrown on the
        /// calling thread (wrapped in <see cref="Exception"/> with the original as
        /// <see cref="Exception.InnerException"/> when crossing thread boundaries).
        /// </summary>
        /// <typeparam name="T">Return type of the function.</typeparam>
        /// <param name="func">Function to execute on the main thread.</param>
        /// <param name="timeout">
        /// Maximum wait time when called from a background thread.
        /// Throws <see cref="TimeoutException"/> if the main thread does not
        /// execute the function within this duration.
        /// </param>
        /// <returns>The value returned by <paramref name="func"/>.</returns>
        /// <exception cref="ArgumentNullException">When <paramref name="func"/> is null.</exception>
        /// <exception cref="TimeoutException">
        /// When called from a background thread and the main thread did not flush
        /// within <paramref name="timeout"/>.
        /// </exception>
        public static T InvokeAndWait<T>(Func<T> func, TimeSpan timeout)
        {
            if (func == null) throw new ArgumentNullException(nameof(func));

            // Fast path: already on the main thread – invoke directly.
            if (IsMainThread())
                return func();

            // Background-thread path: enqueue and block until done.
            T result = default;
            Exception thrownException = null;
            using var mre = new ManualResetEventSlim(initialState: false);

            EnsureHooked();
            _queue.Enqueue(() =>
            {
                try
                {
                    result = func();
                }
                catch (Exception ex)
                {
                    thrownException = ex;
                }
                finally
                {
                    mre.Set();
                }
            });

            if (!mre.Wait(timeout))
                throw new TimeoutException(
                    $"[MainThreadDispatcher] InvokeAndWait timed out after {timeout.TotalSeconds:F1}s. " +
                    "The main thread did not flush the queue in time.");

            if (thrownException != null)
                throw new Exception(
                    $"[MainThreadDispatcher] Exception during main-thread invocation: {thrownException.Message}",
                    thrownException);

            return result;
        }

        // --- private ------------------------------------------------------------

        /// <summary>
        /// Returns <c>true</c> when called from the Unity main thread.
        ///
        /// The main thread ID is captured the first time <see cref="Flush"/> runs
        /// (which is always on the main thread via EditorApplication.update).
        /// Before the first Flush, the ID is unknown (-1) so we conservatively
        /// treat the caller as a background thread.
        /// </summary>
        internal static bool IsMainThread()
            => _mainThreadId != -1 && Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        private static void EnsureHooked()
        {
            if (_hooked) return;
            lock (_hookLock)
            {
                if (_hooked) return;
                EditorApplication.update += Flush;
                _hooked = true;
            }
        }

        private static void Flush()
        {
            // Capture the main thread ID on first invocation.
            // Flush always runs on the Unity main thread via EditorApplication.update.
            if (_mainThreadId == -1)
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;

            // Drain everything that was queued up to this frame.
            while (_queue.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // Log but do not let a single action failure kill the flush loop.
                    UnityEngine.Debug.LogError(
                        $"[MainThreadDispatcher] Exception in queued action: {ex}");
                }
            }
        }
    }
}
