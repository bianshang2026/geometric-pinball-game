#if UNITY_EDITOR_OSX
using System;
using System.Runtime.InteropServices;
using UnityEngine;

using UnityTcp.Editor.Helpers;
namespace Cn.Tuanjie.Codely.Editor
{
    /// <summary>
    /// macOS WKWebView integration wrapper
    /// Provides a platform-agnostic interface for WebView operations
    /// </summary>
    public class MacOSWebViewBridge
    {
#if UNITY_EDITOR_OSX
        #region Native Plugin Imports
        [DllImport("WebViewPlugin")]
        private static extern IntPtr WebView_Create(IntPtr guiViewPtr, string url, float x, float y, float w, float h);
        
        [DllImport("WebViewPlugin")]
        private static extern IntPtr WebView_CreateByWindowTitle(string windowTitle, string url, float x, float y, float w, float h);
        
        [DllImport("WebViewPlugin")]
        private static extern void WebView_Destroy(IntPtr handle);
        
        [DllImport("WebViewPlugin")]
        private static extern void WebView_UpdateFrame(IntPtr handle, float x, float y, float w, float h);
        
        [DllImport("WebViewPlugin")]
        private static extern void WebView_UpdateParentAndFrame(IntPtr handle, IntPtr guiViewPtr, float x, float y, float w, float h);
        
        [DllImport("WebViewPlugin")]
        private static extern void WebView_LoadURL(IntPtr handle, string url);
        
        [DllImport("WebViewPlugin")]
        private static extern void WebView_GoBack(IntPtr handle);
        
        [DllImport("WebViewPlugin")]
        private static extern void WebView_GoForward(IntPtr handle);
        
        [DllImport("WebViewPlugin")]
        private static extern void WebView_Reload(IntPtr handle);
        
        [DllImport("WebViewPlugin")]
        private static extern void WebView_ExecuteJavaScript(IntPtr handle, string script);
        
        [DllImport("WebViewPlugin")]
        private static extern void WebView_SetHidden(IntPtr handle, bool hidden);
        
        [DllImport("WebViewPlugin")]
        private static extern void WebView_ShowInspector(IntPtr handle);

        [DllImport("WebViewPlugin")]
        private static extern void WebView_Focus(IntPtr handle);
        
        [DllImport("WebViewPlugin")]
        private static extern bool WebView_ValidateHandle(IntPtr handle);
        
        [DllImport("WebViewPlugin")]
        private static extern bool WebView_HasValidContent(IntPtr handle);
        
        // Unity Drag Gateway imports (now in WebViewPlugin)
        [DllImport("WebViewPlugin")]
        private static extern IntPtr InstallUnityDragGateway(IntPtr wkWebViewPtr, IntPtr guiRenderViewPtr);
        
        [DllImport("WebViewPlugin")]
        private static extern void UninstallUnityDragGateway(IntPtr gatewayPtr);
        
        [DllImport("WebViewPlugin")]
        private static extern void UpdateDragGatewayFrame(IntPtr gatewayPtr, float x, float y, float w, float h);
        #endregion
#endif

        private IntPtr _webViewHandle = IntPtr.Zero;
        private bool _isInitialized = false;
        private IntPtr _dragGatewayHandle = IntPtr.Zero;

        public bool IsInitialized => _isInitialized;
        public IntPtr Handle => _webViewHandle;


        public bool RestoreFromHandle(IntPtr handle)
        {
#if UNITY_EDITOR_OSX
            if (handle == IntPtr.Zero)
            {
                return false;
            }
            
            try
            {
                // Verify the handle is still valid using the native registry
                if (!WebView_ValidateHandle(handle))
                {
                    return false;
                }
                
                _webViewHandle = handle;
                _isInitialized = true;
                
                return true;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"MacOSWebViewBridge: Exception while restoring handle: {ex.Message}");
                return false;
            }
#else
            return false;
#endif
        }

        /// <summary>
        /// Check if the WebView has valid content loaded (not blank/white screen)
        /// </summary>
        public bool HasValidContent()
        {
#if UNITY_EDITOR_OSX
            if (_webViewHandle == IntPtr.Zero || !_isInitialized)
            {
                return false;
            }
            
            try
            {
                return WebView_HasValidContent(_webViewHandle);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"MacOSWebViewBridge: Exception checking content validity: {ex.Message}");
                return false;
            }
#else
            return false;
#endif
        }

        /// <summary>
        /// Create and initialize the WebView using window title (recommended)
        /// </summary>
        public bool CreateByWindowTitle(string windowTitle, string url, float x, float y, float w, float h)
        {
#if UNITY_EDITOR_OSX
            try
            {
                if (_isInitialized)
                {
                    CodelyLogger.LogWarning("MacOSWebViewBridge: WebView already initialized");
                    return false;
                }

                if (string.IsNullOrEmpty(windowTitle))
                {
                    CodelyLogger.LogError("MacOSWebViewBridge: Invalid window title");
                    return false;
                }

                _webViewHandle = WebView_CreateByWindowTitle(windowTitle, url, x, y, w, h);
                
                if (_webViewHandle != IntPtr.Zero)
                {
                    _isInitialized = true;
                    return true;
                }
                else
                {
                    CodelyLogger.LogError($"MacOSWebViewBridge: Failed to create WebView for window '{windowTitle}' (returned null handle)");
                    return false;
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"MacOSWebViewBridge: Exception creating WebView: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
#else
            CodelyLogger.LogWarning("MacOSWebViewBridge: WebView is only supported on macOS");
            return false;
#endif
        }

        /// <summary>
        /// Create and initialize the WebView
        /// </summary>
        public bool Create(IntPtr guiViewHandle, string url, float x, float y, float w, float h)
        {
#if UNITY_EDITOR_OSX
            try
            {
                if (_isInitialized)
                {
                    CodelyLogger.LogWarning("MacOSWebViewBridge: WebView already initialized");
                    return false;
                }

                if (guiViewHandle == IntPtr.Zero)
                {
                    CodelyLogger.LogError("MacOSWebViewBridge: Invalid GUIView handle");
                    return false;
                }

                _webViewHandle = WebView_Create(guiViewHandle, url, x, y, w, h);
                
                if (_webViewHandle != IntPtr.Zero)
                {
                    _isInitialized = true;
                    return true;
                }
                else
                {
                    CodelyLogger.LogError("MacOSWebViewBridge: Failed to create WebView (returned null handle)");
                    return false;
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"MacOSWebViewBridge: Exception creating WebView: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
#else
            CodelyLogger.LogWarning("MacOSWebViewBridge: WebView is only supported on macOS");
            return false;
#endif
        }

        /// <summary>
        /// Destroy the WebView
        /// </summary>
        public void Destroy()
        {
#if UNITY_EDITOR_OSX
            // Uninstall drag gateway first
            UninstallDragGateway();
            
            if (_webViewHandle != IntPtr.Zero)
            {
                try
                {
                    WebView_Destroy(_webViewHandle);
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"MacOSWebViewBridge: Error destroying WebView: {ex.Message}");
                }
                finally
                {
                    _webViewHandle = IntPtr.Zero;
                    _isInitialized = false;
                }
            }
#endif
        }

        /// <summary>
        /// Update WebView frame (position and size)
        /// </summary>
        public void UpdateFrame(float x, float y, float w, float h)
        {
#if UNITY_EDITOR_OSX
            if (_webViewHandle != IntPtr.Zero)
            {
                try
                {
                    WebView_UpdateFrame(_webViewHandle, x, y, w, h);
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"MacOSWebViewBridge: Error updating frame: {ex.Message}");
                }
            }
#endif
        }

        /// <summary>
        /// Update parent view and frame (when window/tab changes)
        /// </summary>
        public void UpdateParentAndFrame(IntPtr guiViewPtr, float x, float y, float w, float h)
        {
#if UNITY_EDITOR_OSX
            if (_webViewHandle != IntPtr.Zero)
            {
                try
                {
                    WebView_UpdateParentAndFrame(_webViewHandle, guiViewPtr, x, y, w, h);
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"MacOSWebViewBridge: Error updating parent and frame: {ex.Message}");
                }
            }
#endif
        }

        /// <summary>
        /// Load a URL
        /// </summary>
        public void LoadURL(string url)
        {
#if UNITY_EDITOR_OSX
            if (_webViewHandle != IntPtr.Zero && !string.IsNullOrEmpty(url))
            {
                try
                {
                    WebView_LoadURL(_webViewHandle, url);
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"MacOSWebViewBridge: Error loading URL: {ex.Message}");
                }
            }
#endif
        }

        /// <summary>
        /// Navigate back
        /// </summary>
        public void GoBack()
        {
#if UNITY_EDITOR_OSX
            if (_webViewHandle != IntPtr.Zero)
            {
                try
                {
                    WebView_GoBack(_webViewHandle);
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"MacOSWebViewBridge: Error going back: {ex.Message}");
                }
            }
#endif
        }

        /// <summary>
        /// Navigate forward
        /// </summary>
        public void GoForward()
        {
#if UNITY_EDITOR_OSX
            if (_webViewHandle != IntPtr.Zero)
            {
                try
                {
                    WebView_GoForward(_webViewHandle);
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"MacOSWebViewBridge: Error going forward: {ex.Message}");
                }
            }
#endif
        }

        /// <summary>
        /// Reload the page
        /// </summary>
        public void Reload()
        {
#if UNITY_EDITOR_OSX
            if (_webViewHandle != IntPtr.Zero)
            {
                try
                {
                    WebView_Reload(_webViewHandle);
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"MacOSWebViewBridge: Error reloading: {ex.Message}");
                }
            }
#endif
        }

        /// <summary>
        /// Execute JavaScript in the WebView
        /// </summary>
        public void ExecuteJavaScript(string script)
        {
#if UNITY_EDITOR_OSX
            if (_webViewHandle != IntPtr.Zero && !string.IsNullOrEmpty(script))
            {
                try
                {
                    WebView_ExecuteJavaScript(_webViewHandle, script);
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"MacOSWebViewBridge: Error executing JavaScript: {ex.Message}");
                }
            }
#endif
        }

        /// <summary>
        /// Set WebView visibility (hide/show)
        /// </summary>
        public void SetHidden(bool hidden)
        {
#if UNITY_EDITOR_OSX
            if (_webViewHandle != IntPtr.Zero)
            {
                try
                {
                    WebView_SetHidden(_webViewHandle, hidden);
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"MacOSWebViewBridge: Error setting visibility: {ex.Message}");
                }
            }
#endif
        }

        /// <summary>
        /// Show Web Inspector for debugging
        /// </summary>
        public void ShowInspector()
        {
#if UNITY_EDITOR_OSX
            if (_webViewHandle != IntPtr.Zero)
            {
                try
                {
                    WebView_ShowInspector(_webViewHandle);
                    CodelyLogger.Log("MacOSWebViewBridge: Opening Web Inspector...");
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"MacOSWebViewBridge: Error showing inspector: {ex.Message}");
                }
            }
            else
            {
                CodelyLogger.LogWarning("MacOSWebViewBridge: Cannot show inspector - WebView not initialized");
            }
#endif
        }

        /// <summary>
        /// Move keyboard focus to the embedded WKWebView.
        /// </summary>
        public void Focus()
        {
#if UNITY_EDITOR_OSX
            if (_webViewHandle != IntPtr.Zero)
            {
                try
                {
                    WebView_Focus(_webViewHandle);
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"MacOSWebViewBridge: Error focusing WebView: {ex.Message}");
                }
            }
#endif
        }

        /// <summary>
        /// Install Unity Drag Gateway to enable GameObject drag and drop
        /// Must be called after WebView is created and guiRenderView is available
        /// </summary>
        public bool InstallDragGateway(IntPtr guiRenderViewPtr)
        {
#if UNITY_EDITOR_OSX
            if (_webViewHandle == IntPtr.Zero)
            {
                CodelyLogger.LogError("MacOSWebViewBridge: Cannot install drag gateway - WebView not initialized");
                return false;
            }

            if (guiRenderViewPtr == IntPtr.Zero)
            {
                CodelyLogger.LogError("MacOSWebViewBridge: Cannot install drag gateway - GUIRenderView is null");
                return false;
            }

            if (_dragGatewayHandle != IntPtr.Zero)
            {
                CodelyLogger.LogWarning("MacOSWebViewBridge: Drag gateway already installed");
                return true;
            }

            try
            {
                _dragGatewayHandle = InstallUnityDragGateway(_webViewHandle, guiRenderViewPtr);
                
                if (_dragGatewayHandle != IntPtr.Zero)
                {
                    CodelyLogger.Log("MacOSWebViewBridge: Drag gateway installed successfully");
                    return true;
                }
                else
                {
                    CodelyLogger.LogError("MacOSWebViewBridge: Failed to install drag gateway");
                    return false;
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"MacOSWebViewBridge: Exception installing drag gateway: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
#else
            return false;
#endif
        }

        /// <summary>
        /// Uninstall Unity Drag Gateway
        /// </summary>
        public void UninstallDragGateway()
        {
#if UNITY_EDITOR_OSX
            if (_dragGatewayHandle != IntPtr.Zero)
            {
                try
                {
                    UninstallUnityDragGateway(_dragGatewayHandle);
                    CodelyLogger.Log("MacOSWebViewBridge: Drag gateway uninstalled");
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"MacOSWebViewBridge: Error uninstalling drag gateway: {ex.Message}");
                }
                finally
                {
                    _dragGatewayHandle = IntPtr.Zero;
                }
            }
#endif
        }

        /// <summary>
        /// Update drag gateway frame to match WebView frame
        /// Should be called whenever WebView frame changes
        /// </summary>
        public void UpdateDragGatewayFrame(float x, float y, float w, float h)
        {
#if UNITY_EDITOR_OSX
            if (_dragGatewayHandle != IntPtr.Zero)
            {
                try
                {
                    UpdateDragGatewayFrame(_dragGatewayHandle, x, y, w, h);
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"MacOSWebViewBridge: Error updating drag gateway frame: {ex.Message}");
                }
            }
#endif
        }
    }
}
#endif
