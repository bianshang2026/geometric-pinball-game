using System;
using UnityEngine;

#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
using UnityEditor;
using UnityEditor.Media;
#endif

namespace UnityTcp.Editor.Tools
{
    internal interface IMp4FrameEncoder : IDisposable
    {
        bool AddFrame(Texture2D frame);
    }

    internal static class PlatformMp4Encoder
    {
        internal static bool IsSupported
        {
            get
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
                return true;
#else
                return false;
#endif
            }
        }

        internal static string UnsupportedMessage =>
            "Native MP4 recording is supported only on the Windows and macOS Editors. " +
            "The Linux Editor does not ship a Unity MediaEncoder backend.";

        internal static IMp4FrameEncoder Create(
            string outputPath, int width, int height, int fps)
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            return new UnityMediaEncoderMp4Encoder(outputPath, width, height, fps);
#else
            throw new PlatformNotSupportedException(UnsupportedMessage);
#endif
        }
    }

#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
    /// <summary>
    /// Wraps UnityEditor.Media.MediaEncoder, which delegates to the platform
    /// media backend: Media Foundation on Windows, AVFoundation on macOS.
    /// Both produce H.264 in an MP4 container without any external binary.
    /// </summary>
    internal sealed class UnityMediaEncoderMp4Encoder : IMp4FrameEncoder
    {
        private MediaEncoder m_Encoder;

        internal UnityMediaEncoderMp4Encoder(
            string outputPath, int width, int height, int fps)
        {
            var attributes = new VideoTrackAttributes
            {
                frameRate = new MediaRational(fps),
                width = (uint)width,
                height = (uint)height,
                includeAlpha = false,
                bitRateMode = VideoBitrateMode.Medium,
            };
            m_Encoder = new MediaEncoder(outputPath, attributes);
        }

        public bool AddFrame(Texture2D frame)
        {
            if (m_Encoder == null)
                throw new ObjectDisposedException(nameof(UnityMediaEncoderMp4Encoder));
            return m_Encoder.AddFrame(frame);
        }

        public void Dispose()
        {
            MediaEncoder encoder = m_Encoder;
            m_Encoder = null;
            encoder?.Dispose();
        }
    }
#endif
}
