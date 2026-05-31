using System;
using System.Collections.Concurrent;
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
    /// Usage:
    ///   MainThreadDispatcher.Enqueue(() => { /* main-thread-only code */ });
    ///
    /// The dispatcher auto-registers its hook on first use and does not need
    /// explicit initialisation.
    /// </summary>
    public static class MainThreadDispatcher
    {
        private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        private static volatile bool _hooked;
        private static readonly object _hookLock = new object();

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

        // --- private ------------------------------------------------------------

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
