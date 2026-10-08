#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
namespace Cn.Tuanjie.Codely.Editor
{
    public static class WindowResourcePreservationPolicy
    {
        public static bool ShouldPreserveSharedResources(bool detachMode, int liveWindowCount)
        {
            return detachMode || liveWindowCount > 1;
        }
    }
}
#endif
