#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
using System;

namespace Cn.Tuanjie.Codely.Editor
{
    public readonly struct WindowHostStabilityDecision
    {
        public WindowHostStabilityDecision(bool shouldApply, IntPtr nextStableHandle, IntPtr nextPendingHandle)
        {
            ShouldApply = shouldApply;
            NextStableHandle = nextStableHandle;
            NextPendingHandle = nextPendingHandle;
        }

        public bool ShouldApply { get; }
        public IntPtr NextStableHandle { get; }
        public IntPtr NextPendingHandle { get; }
    }

    public static class WindowHostStabilityPolicy
    {
        public static WindowHostStabilityDecision Evaluate(IntPtr currentHandle, IntPtr stableHandle, IntPtr pendingHandle)
        {
            if (currentHandle == IntPtr.Zero)
            {
                return new WindowHostStabilityDecision(
                    shouldApply: false,
                    nextStableHandle: stableHandle,
                    nextPendingHandle: IntPtr.Zero);
            }

            if (stableHandle == IntPtr.Zero)
            {
                if (pendingHandle == currentHandle)
                {
                    return new WindowHostStabilityDecision(
                        shouldApply: true,
                        nextStableHandle: currentHandle,
                        nextPendingHandle: IntPtr.Zero);
                }

                return new WindowHostStabilityDecision(
                    shouldApply: false,
                    nextStableHandle: IntPtr.Zero,
                    nextPendingHandle: currentHandle);
            }

            if (currentHandle == stableHandle)
            {
                return new WindowHostStabilityDecision(
                    shouldApply: true,
                    nextStableHandle: stableHandle,
                    nextPendingHandle: IntPtr.Zero);
            }

            if (pendingHandle == currentHandle)
            {
                return new WindowHostStabilityDecision(
                    shouldApply: true,
                    nextStableHandle: currentHandle,
                    nextPendingHandle: IntPtr.Zero);
            }

            return new WindowHostStabilityDecision(
                shouldApply: false,
                nextStableHandle: stableHandle,
                nextPendingHandle: currentHandle);
        }
    }
}
#endif
