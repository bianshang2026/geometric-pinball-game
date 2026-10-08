using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Helper class for main thread operations
    /// </summary>
    public static class MainThreadHelper
    {
        private static int mainThreadId;

        /// <summary>
        /// Initialize the main thread ID for safe thread checks
        /// Call this from the main thread during static constructor
        /// </summary>
        public static void InitializeMainThreadId()
        {
            try { mainThreadId = Thread.CurrentThread.ManagedThreadId; } catch { mainThreadId = 0; }
        }

        /// <summary>
        /// True when called from the Unity main thread. False when the main thread id was never
        /// initialized — callers that need a "safe on main thread" fast path should treat that
        /// as "not the main thread".
        /// </summary>
        public static bool IsMainThread
        {
            get
            {
                try { return mainThreadId != 0 && Thread.CurrentThread.ManagedThreadId == mainThreadId; }
                catch { return false; }
            }
        }

        /// <summary>
        /// Invoke the given function on the Unity main thread and wait up to timeoutMs for the result.
        /// Returns null on timeout or error; caller should provide a fallback error response.
        /// </summary>
        public static object InvokeOnMainThreadWithTimeout(Func<object> func, int timeoutMs)
        {
            if (func == null) return null;
            try
            {
                // If mainThreadId is unknown, assume we're on main thread to avoid blocking the editor.
                if (mainThreadId == 0)
                {
                    try { return func(); }
                    catch (Exception ex) { throw new InvalidOperationException($"Main thread handler error: {ex.Message}", ex); }
                }
                // If we are already on the main thread, execute directly to avoid deadlocks
                try
                {
                    if (Thread.CurrentThread.ManagedThreadId == mainThreadId)
                    {
                        return func();
                    }
                }
                catch { }

                object result = null;
                Exception captured = null;
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        result = func();
                    }
                    catch (Exception ex)
                    {
                        captured = ex;
                    }
                    finally
                    {
                        try { tcs.TrySetResult(true); } catch { }
                    }
                };

                // Wait for completion with timeout (Editor thread will pump delayCall)
                bool completed = tcs.Task.Wait(timeoutMs);
                if (!completed)
                {
                    return null; // timeout
                }
                if (captured != null)
                {
                    throw new InvalidOperationException($"Main thread handler error: {captured.Message}", captured);
                }
                return result;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to invoke on main thread: {ex.Message}", ex);
            }
        }
    }
}
