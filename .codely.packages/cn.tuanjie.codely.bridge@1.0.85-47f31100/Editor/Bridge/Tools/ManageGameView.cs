using System;
using System.Collections.Generic;
using System.Reflection;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Manages the Game view resolution (get, set, list).
    /// Uses reflection to access Unity's internal GameView and GameViewSizes APIs,
    /// which are not part of the public editor scripting surface.
    ///
    /// Actions:
    ///   get_resolution   – Return the currently selected Game view resolution.
    ///   set_resolution   – Select (or create) a fixed resolution. Params: width, height (1-8192).
    ///   list_resolutions – List every resolution available in the current size group.
    /// </summary>
    public static class ManageGameView
    {
        // ---- Cached reflection handles ----
        private static bool _reflectionInitialized;
        private static string _reflectionError;

        private static Type _gameViewType;
        private static Type _gameViewSizesType;
        private static Type _gameViewSizeType;
        private static Type _gameViewSizeGroupType;
        private static Type _gameViewSizeTypeEnum;

        private static MethodInfo _getMainGameView;
        private static PropertyInfo _selectedSizeIndex;
        private static PropertyInfo _sizesInstance;
        private static PropertyInfo _currentGroupType;
        private static PropertyInfo _currentGroupProp;     // Unity 6000.x: GameViewSizes.currentGroup
        private static MethodInfo _getGroup;
        private static MethodInfo _getTotalCount;
        private static MethodInfo _getGameViewSize;
        private static MethodInfo _addCustomSize;
        private static MethodInfo _saveToHardDisk;

        private static PropertyInfo _sizeWidth;
        private static PropertyInfo _sizeHeight;
        private static PropertyInfo _sizeBaseText;
        private static PropertyInfo _sizeSizeType;

        private static object _fixedResolutionValue;

        private static readonly Dictionary<string, Func<JObject, object>> ActionHandlers =
            new Dictionary<string, Func<JObject, object>>
            {
                { "get_resolution", _ => GetResolution() },
                { "set_resolution", SetResolution },
                { "list_resolutions", _ => ListResolutions() },
            };

        public static object HandleCommand(JObject @params)
        {
            if (!TryInitReflection(out var error))
                return Response.Error(error);
            return ActionRouter.Route(@params, ActionHandlers);
        }

        // ==================== Reflection setup ====================

        private static bool TryInitReflection(out string error)
        {
            error = null;

            if (_reflectionInitialized)
            {
                error = _reflectionError;
                return _reflectionError == null;
            }

            _reflectionInitialized = true;

            try
            {
                _gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
                _gameViewSizesType = Type.GetType("UnityEditor.GameViewSizes,UnityEditor");
                _gameViewSizeType = Type.GetType("UnityEditor.GameViewSize,UnityEditor");
                _gameViewSizeGroupType = Type.GetType("UnityEditor.GameViewSizeGroup,UnityEditor");
                _gameViewSizeTypeEnum = Type.GetType("UnityEditor.GameViewSizeType,UnityEditor");

                if (_gameViewType == null || _gameViewSizesType == null ||
                    _gameViewSizeType == null || _gameViewSizeGroupType == null ||
                    _gameViewSizeTypeEnum == null)
                {
                    _reflectionError = "GameView internals not available in this Unity version.";
                    error = _reflectionError;
                    return false;
                }

                var allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

                // GameView
                _getMainGameView = _gameViewType.GetMethod("GetMainGameView", allFlags)
                    ?? _gameViewType.GetMethod("GetMainPlayModeView", allFlags);
                _selectedSizeIndex = _gameViewType.GetProperty("selectedSizeIndex", allFlags);

                // GameViewSizes - Unity 6000.x uses ScriptableSingleton, check base type too
                _sizesInstance = _gameViewSizesType.GetProperty("instance", allFlags);
                if (_sizesInstance == null)
                {
                    // ScriptableSingleton<T>.instance is on the base generic type
                    var baseType = _gameViewSizesType.BaseType;
                    while (baseType != null && _sizesInstance == null)
                    {
                        _sizesInstance = baseType.GetProperty("instance", allFlags);
                        baseType = baseType.BaseType;
                    }
                }
                _currentGroupType = _gameViewSizesType.GetProperty("currentGroupType", allFlags);
                _getGroup = _gameViewSizesType.GetMethod("GetGroup", allFlags);
                // Unity 6000.x: direct currentGroup property
                _currentGroupProp = _gameViewSizesType.GetProperty("currentGroup", allFlags);
                _saveToHardDisk = _gameViewSizesType.GetMethod("SaveToHDD",
                    BindingFlags.Public | BindingFlags.Instance);

                // GameViewSizeGroup
                _getTotalCount = _gameViewSizeGroupType.GetMethod("GetTotalCount",
                    BindingFlags.Public | BindingFlags.Instance);
                _getGameViewSize = _gameViewSizeGroupType.GetMethod("GetGameViewSize",
                    BindingFlags.Public | BindingFlags.Instance);
                _addCustomSize = _gameViewSizeGroupType.GetMethod("AddCustomSize",
                    BindingFlags.Public | BindingFlags.Instance);

                // GameViewSize properties
                _sizeWidth = _gameViewSizeType.GetProperty("width", BindingFlags.Public | BindingFlags.Instance);
                _sizeHeight = _gameViewSizeType.GetProperty("height", BindingFlags.Public | BindingFlags.Instance);
                _sizeBaseText = _gameViewSizeType.GetProperty("baseText", BindingFlags.Public | BindingFlags.Instance);
                _sizeSizeType = _gameViewSizeType.GetProperty("sizeType", BindingFlags.Public | BindingFlags.Instance);

                // FixedResolution enum value
                _fixedResolutionValue = Enum.Parse(_gameViewSizeTypeEnum, "FixedResolution");

                // Validate critical members
                var missing = new List<string>();
                if (_sizesInstance == null) missing.Add("GameViewSizes.instance");
                if (_getGroup == null && _currentGroupProp == null) missing.Add("GameViewSizes.GetGroup/currentGroup");
                if (_getTotalCount == null) missing.Add("GameViewSizeGroup.GetTotalCount");
                if (_getGameViewSize == null) missing.Add("GameViewSizeGroup.GetGameViewSize");
                if (_sizeWidth == null) missing.Add("GameViewSize.width");
                if (_sizeHeight == null) missing.Add("GameViewSize.height");
                if (_selectedSizeIndex == null) missing.Add("GameView.selectedSizeIndex");
                if (missing.Count > 0)
                {
                    _reflectionError = $"Missing GameView internal members: {string.Join(", ", missing)}. This Unity version may not be supported.";
                    error = _reflectionError;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _reflectionError = $"Failed to initialize GameView reflection: {ex.Message}";
                error = _reflectionError;
                return false;
            }
        }

        // ==================== Actions ====================

        private static object GetResolution()
        {
            var gameView = GetMainGameView(true);
            var group = GetCurrentSizeGroup();

            if (group == null)
                return Response.Error("Could not access Game view size group.");

            int selectedIndex = gameView != null ? GetSelectedSizeIndex(gameView) : -1;
            int width = 0;
            int height = 0;
            string name = "";
            string sizeType = "";

            if (selectedIndex >= 0 && selectedIndex < GetTotalCount(group))
            {
                var size = GetGameViewSize(group, selectedIndex);
                width = GetSizeWidth(size);
                height = GetSizeHeight(size);
                name = GetSizeBaseText(size);
                sizeType = GetSizeType(size);
            }
            else if (gameView != null)
            {
                // Fallback: read targetSize
                var targetSizeProp = gameView.GetType().GetProperty("targetSize",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (targetSizeProp != null)
                {
                    var targetSize = (Vector2)targetSizeProp.GetValue(gameView);
                    width = (int)targetSize.x;
                    height = (int)targetSize.y;
                    name = "Unknown";
                    sizeType = "Unknown";
                }
            }

            // Last-resort fallback: ask the editor for the main Game view render size. This
            // works even when the window instance / selected-size index can't be resolved
            // (simulator windows, or Tuanjie's separate editor module assemblies).
            if (width <= 0 || height <= 0)
            {
                Vector2 mainSize = Handles.GetMainGameViewSize();
                width = (int)mainSize.x;
                height = (int)mainSize.y;
                if (string.IsNullOrEmpty(name)) name = "Unknown";
                if (string.IsNullOrEmpty(sizeType)) sizeType = "Unknown";
            }

            if (width <= 0 || height <= 0)
                return Response.Error("No Game view window found. Open the Game view in the Unity Editor first.");

            return Response.Success(
                $"Current Game view resolution: {width}x{height}.",
                new
                {
                    action = "get_resolution",
                    width,
                    height,
                    name,
                    selectedIndex,
                    sizeType
                });
        }

        private static object SetResolution(JObject @params)
        {
            int width = @params["width"]?.ToObject<int?>() ?? 0;
            int height = @params["height"]?.ToObject<int?>() ?? 0;

            if (width <= 0 || height <= 0)
                return Response.Error("Parameters 'width' and 'height' are required and must be positive integers.");

            if (width > 8192 || height > 8192)
                return Response.Error($"Resolution {width}x{height} exceeds maximum allowed (8192x8192).");

            var gameView = GetMainGameView(true);
            if (gameView == null)
                return Response.Error("No Game view window found. Open the Game view in the Unity Editor first.");

            var group = GetCurrentSizeGroup();
            if (group == null)
                return Response.Error("Could not access Game view size group.");

            int foundIndex = FindResolution(group, width, height);
            bool wasAdded = false;
            string label = $"Codely {width}x{height}";

            if (foundIndex < 0)
            {
                // Add custom resolution
                foundIndex = AddCustomResolution(group, width, height, label);
                wasAdded = true;
            }
            else
            {
                var size = GetGameViewSize(group, foundIndex);
                label = GetSizeBaseText(size);
                if (string.IsNullOrEmpty(label))
                    label = $"{width}x{height}";
            }

            SetSelectedSizeIndex(gameView, foundIndex);

            // Repaint to apply the change
            ((EditorWindow)gameView).Repaint();

            return Response.Success(
                $"Game view resolution set to {width}x{height}.",
                new
                {
                    action = "set_resolution",
                    width,
                    height,
                    selectedIndex = foundIndex,
                    wasAdded,
                    label
                });
        }

        private static object ListResolutions()
        {
            var group = GetCurrentSizeGroup();
            if (group == null)
                return Response.Error("Could not access Game view size group.");

            int totalCount = GetTotalCount(group);
            var resolutions = new List<object>();

            for (int i = 0; i < totalCount; i++)
            {
                var size = GetGameViewSize(group, i);
                resolutions.Add(new
                {
                    index = i,
                    width = GetSizeWidth(size),
                    height = GetSizeHeight(size),
                    name = GetSizeBaseText(size),
                    sizeType = GetSizeType(size)
                });
            }

            var gameView = GetMainGameView(false);
            int currentIndex = gameView != null ? GetSelectedSizeIndex(gameView) : -1;

            return Response.Success(
                $"Found {totalCount} Game view resolution(s).",
                new
                {
                    action = "list_resolutions",
                    resolutions,
                    count = totalCount,
                    currentIndex
                });
        }

        // ==================== Reflection helpers ====================

        private static object GetSizesInstance()
        {
            return _sizesInstance.GetValue(null);
        }

        private static object GetCurrentSizeGroup()
        {
            var instance = GetSizesInstance();
            if (instance == null) return null;

            // Unity 6000.x: direct currentGroup property
            if (_currentGroupProp != null)
                return _currentGroupProp.GetValue(instance);

            // Older Unity: GetGroup(currentGroupType)
            if (_currentGroupType != null && _getGroup != null)
            {
                var groupType = _currentGroupType.GetValue(null);
                return _getGroup.Invoke(instance, new[] { (object)(int)groupType });
            }

            return null;
        }

        private static object GetMainGameView(bool createIfMissing)
        {
            // 1. Built-in helper (GetMainGameView / GetMainPlayModeView).
            if (_getMainGameView != null)
            {
                var mainView = _getMainGameView.Invoke(null, null);
                if (mainView != null)
                    return mainView;
            }

            // 2. Enumerate every open EditorWindow and pick a Game view by walking the type
            //    hierarchy (mirrors ManageScreenshot.GetPlayModeViewWindows). Needed on Tuanjie
            //    / newer editors where the play windows live in separate module assemblies, so
            //    Type.GetType(...,UnityEditor) and FindObjectsOfTypeAll by the resolved type
            //    can miss the actual open window.
            var found = FindGameViewWindow();
            if (found != null)
                return found;

            // 3. Resources lookup by the resolved type (includes subclasses).
            var views = Resources.FindObjectsOfTypeAll(_gameViewType);
            if (views != null && views.Length > 0)
                return views[0];

            if (!createIfMissing)
                return null;

            // 4. Nothing open — create one so we can read/set the resolution.
            return EditorWindow.GetWindow(_gameViewType);
        }

        // Finds an open Game view among all loaded EditorWindows by walking each window's
        // base-type chain. A window whose hierarchy contains "GameView" is preferred (it
        // exposes selectedSizeIndex + the size group); any other PlayModeView (e.g. a Device
        // or HMI Simulator) is kept only as a fallback. Discovery is by type NAME rather than a
        // resolved Type because the simulators live in separately-compiled editor module
        // assemblies whose assembly-qualified names vary across Unity/Tuanjie versions.
        private static EditorWindow FindGameViewWindow()
        {
            EditorWindow[] all = Resources.FindObjectsOfTypeAll<EditorWindow>();
            EditorWindow playModeFallback = null;

            foreach (EditorWindow w in all)
            {
                if (w == null) continue;
                Type t = w.GetType();
                if (TypeChainContains(t, "GameView"))
                    return w;
                if (playModeFallback == null && TypeChainContains(t, "PlayModeView"))
                    playModeFallback = w;
            }

            return playModeFallback;
        }

        // True when any type in the chain (excluding System.Object) has the given simple name.
        private static bool TypeChainContains(Type t, string simpleName)
        {
            for (; t != null && t != typeof(object); t = t.BaseType)
                if (t.Name == simpleName)
                    return true;
            return false;
        }

        private static int GetSelectedSizeIndex(object gameView)
        {
            var prop = SelectedSizeIndexProp(gameView);
            if (prop == null || gameView == null) return -1;
            try { return (int)prop.GetValue(gameView); }
            catch { return -1; }
        }

        private static void SetSelectedSizeIndex(object gameView, int index)
        {
            var prop = SelectedSizeIndexProp(gameView);
            if (prop != null && gameView != null)
                prop.SetValue(gameView, index);
        }

        // Resolves the selectedSizeIndex property against the window's actual runtime type so a
        // GameView subclass (or a play window from another assembly) doesn't trip a
        // "does not match target type" reflection error from the cached _gameViewType handle.
        private static PropertyInfo SelectedSizeIndexProp(object gameView)
        {
            if (gameView == null) return _selectedSizeIndex;
            var t = gameView.GetType();
            if (_selectedSizeIndex != null && _selectedSizeIndex.DeclaringType != null
                && _selectedSizeIndex.DeclaringType.IsAssignableFrom(t))
                return _selectedSizeIndex;
            return t.GetProperty("selectedSizeIndex",
                       BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                   ?? _selectedSizeIndex;
        }

        private static int GetTotalCount(object group)
        {
            return (int)_getTotalCount.Invoke(group, null);
        }

        private static object GetGameViewSize(object group, int index)
        {
            return _getGameViewSize.Invoke(group, new object[] { index });
        }

        private static int GetSizeWidth(object size)
        {
            return (int)_sizeWidth.GetValue(size);
        }

        private static int GetSizeHeight(object size)
        {
            return (int)_sizeHeight.GetValue(size);
        }

        private static string GetSizeBaseText(object size)
        {
            return _sizeBaseText != null ? (string)_sizeBaseText.GetValue(size) : "";
        }

        private static string GetSizeType(object size)
        {
            if (_sizeSizeType == null) return "Unknown";
            var val = _sizeSizeType.GetValue(size);
            return val.ToString();
        }

        private static int FindResolution(object group, int width, int height)
        {
            int totalCount = GetTotalCount(group);

            for (int i = 0; i < totalCount; i++)
            {
                var size = GetGameViewSize(group, i);
                if (GetSizeType(size) == "FixedResolution" &&
                    GetSizeWidth(size) == width &&
                    GetSizeHeight(size) == height)
                {
                    return i;
                }
            }

            return -1;
        }

        private static int AddCustomResolution(object group, int width, int height, string label)
        {
            // Create GameViewSize via reflection constructor
            var ctor = _gameViewSizeType.GetConstructor(new[]
            {
                _gameViewSizeTypeEnum, typeof(int), typeof(int), typeof(string)
            });

            object newSize;
            if (ctor != null)
            {
                newSize = ctor.Invoke(new[] { _fixedResolutionValue, width, height, label });
            }
            else
            {
                // Fallback: parameterless constructor + set properties
                newSize = Activator.CreateInstance(_gameViewSizeType);
                _sizeSizeType?.SetValue(newSize, _fixedResolutionValue);
                _sizeWidth.SetValue(newSize, width);
                _sizeHeight.SetValue(newSize, height);
                _sizeBaseText?.SetValue(newSize, label);
            }

            _addCustomSize.Invoke(group, new[] { newSize });

            // Save to disk
            if (_saveToHardDisk != null)
            {
                var instance = GetSizesInstance();
                _saveToHardDisk.Invoke(instance, null);
            }

            return GetTotalCount(group) - 1;
        }
    }
}
