#if UNITY_EDITOR
namespace TJGenerators.Utils
{
    /// <summary>
    /// Resolves Seedance / video generator mode from UI selection and available inputs.
    /// Reference image is optional: reference_image without images falls back to text_to_video;
    /// text_to_video with images upgrades to reference_image.
    /// </summary>
    public static class VideoModeResolver
    {
        /// <summary>
        /// Resolve the effective mode for submission.
        /// first_frame / first_last_frame are returned unchanged; caller validates image counts.
        /// </summary>
        public static string Resolve(string selectedMode, bool hasImage, bool hasReferenceVideo = false)
        {
            if (selectedMode == "multimodal")
            {
                if (hasReferenceVideo)
                    return "multimodal";
                return hasImage ? "reference_image" : "text_to_video";
            }

            if (selectedMode == "first_frame" || selectedMode == "first_last_frame")
                return selectedMode;

            // null, reference_image, text_to_video (and any other soft mode): resolve by image presence
            return hasImage ? "reference_image" : "text_to_video";
        }
    }
}
#endif
