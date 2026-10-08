using System;
using System.Collections.Generic;
using System.Linq;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Notifier
{
    /// <summary>
    /// Broadcasts the active scene's hierarchy to notifier clients whenever it
    /// changes. The emitted payload mirrors the structure returned by
    /// <see cref="ManageScene"/>'s get_hierarchy action (including the
    /// roots_only fallback for large scenes).
    /// </summary>
    // [InitializeOnLoad] // disable this notifier
    public static class SceneHierarchyNotifier
    {
        private const string EventType = "scene_hierarchy_changed";

        // Matches ManageScene.GetSceneHierarchy cap.
        private const int LargeSceneThreshold = 500;

        // Debounce window: every event extends the deadline; flush only after this much idle time.
        // Keeps flush rate sane during bursts of edits (drag, multi-select rename, scripted spawns).
        private const double DebounceSeconds = 0.25;

        private static double _flushDeadline;
        private static bool _tickRegistered;

        static SceneHierarchyNotifier()
        {
            EditorApplication.hierarchyChanged       += OnHierarchyChanged;
            EditorSceneManager.sceneOpened           += OnSceneOpened;
            EditorSceneManager.sceneClosed           += OnSceneClosed;
            EditorSceneManager.sceneSaved            += OnSceneSaved;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
        }

        // ---- Event hooks (debounced) -------------------------------------- //

        private static void OnHierarchyChanged()                                => ScheduleFlush();
        private static void OnSceneOpened(Scene s, OpenSceneMode mode)          => ScheduleFlush();
        private static void OnSceneClosed(Scene s)                              => ScheduleFlush();
        private static void OnSceneSaved(Scene s)                               => ScheduleFlush();
        private static void OnActiveSceneChanged(Scene previous, Scene current) => ScheduleFlush();

        private static void ScheduleFlush()
        {
            _flushDeadline = EditorApplication.timeSinceStartup + DebounceSeconds;
            if (_tickRegistered) return;
            _tickRegistered = true;
            EditorApplication.update += DebounceTick;
        }

        private static void DebounceTick()
        {
            if (EditorApplication.timeSinceStartup < _flushDeadline) return;

            EditorApplication.update -= DebounceTick;
            _tickRegistered = false;
            Flush();
        }

        private static void Flush()
        {
            try
            {
                if (!UnityTcpBridge.IsRunning) return;

                JObject payload = BuildHierarchyPayload();
                if (payload == null) return;

                UnityTcpBridge.NotifyAll(EventType, payload);
            }
            catch (Exception e)
            {
                try { CodelyLogger.LogError($"[SceneHierarchyNotifier] flush failed: {e.Message}"); } catch { }
            }
        }

        // ---- Payload builder (shape mirrors ManageScene.GetSceneHierarchy) -- //

        private static JObject BuildHierarchyPayload()
        {
            Scene activeScene = EditorSceneManager.GetActiveScene();
            if (!activeScene.IsValid() || !activeScene.isLoaded)
            {
                return JObject.FromObject(new
                {
                    sceneName        = string.Empty,
                    scenePath        = string.Empty,
                    partial          = false,
                    mode             = "empty",
                    totalObjectCount = 0,
                    rootCount        = 0,
                    hierarchy        = new List<object>(),
                });
            }

            GameObject[] rootObjects = activeScene.GetRootGameObjects();

            int totalObjectCount = 0;
            foreach (var rootObj in rootObjects)
            {
                totalObjectCount += CountGameObjectsRecursive(rootObj);
            }

            // Large scene: mirror ManageScene's roots_only fallback.
            if (totalObjectCount > LargeSceneThreshold)
            {
                var roots = rootObjects
                    .Where(go => go != null)
                    .Select(go => new Dictionary<string, object>
                    {
                        { "name", go.name },
                        { "instanceID", go.GetStableInstanceId() },
                        { "activeSelf", go.activeSelf },
                        { "activeInHierarchy", go.activeInHierarchy },
                        { "tag", go.tag },
                        { "layer", go.layer },
                        { "isStatic", go.isStatic },
                        { "childCount", go.transform != null ? go.transform.childCount : 0 },
                        { "children", new List<object>() },
                    })
                    .ToList();

                return JObject.FromObject(new
                {
                    sceneName        = activeScene.name,
                    scenePath        = activeScene.path,
                    partial          = true,
                    mode             = "roots_only",
                    totalObjectCount = totalObjectCount,
                    rootCount        = roots.Count,
                    roots            = roots,
                    hints = new[]
                    {
                        "Use unity_gameobject action='list_children' on a specific root GameObject to expand its subtree with a depth limit.",
                        "Use unity_gameobject action='find' with searchMethod='by_path' to locate a specific object (e.g. 'Root/Child') before listing children.",
                        "If you only need a specific area, pick a root from this list and then drill down incrementally (depth=1..N)."
                    }
                });
            }

            var hierarchy = rootObjects
                .Where(go => go != null)
                .Select(go => BuildGameObjectTree(go))
                .ToList();

            return JObject.FromObject(new
            {
                sceneName        = activeScene.name,
                scenePath        = activeScene.path,
                partial          = false,
                mode             = "full",
                totalObjectCount = totalObjectCount,
                rootCount        = hierarchy.Count,
                hierarchy        = hierarchy,
            });
        }

        // ---- Helpers (kept in sync with ManageScene's private equivalents) -- //

        private static int CountGameObjectsRecursive(GameObject go)
        {
            if (go == null) return 0;
            int count = 0;
            var stack = new Stack<Transform>();
            if (go.transform != null) stack.Push(go.transform);

            while (stack.Count > 0)
            {
                var tr = stack.Pop();
                if (tr == null) continue;
                count++;
                int cc = tr.childCount;
                for (int i = 0; i < cc; i++)
                {
                    var child = tr.GetChild(i);
                    if (child != null) stack.Push(child);
                }
            }
            return count;
        }

        // Iterative DFS that builds the tree without recursion, so deeply nested hierarchies
        // (e.g. UI layouts hundreds of levels deep) don't blow the managed stack regardless
        // of total object count.
        private static object BuildGameObjectTree(GameObject root)
        {
            if (root == null) return null;
            var rootTransform = root.transform;
            if (rootTransform == null) return null;

            var nodes = new Dictionary<Transform, Dictionary<string, object>>();
            var stack = new Stack<Transform>();
            stack.Push(rootTransform);

            while (stack.Count > 0)
            {
                var tr = stack.Pop();
                if (tr == null || nodes.ContainsKey(tr)) continue;
                nodes[tr] = BuildGameObjectNode(tr.gameObject);
                int cc = tr.childCount;
                for (int i = 0; i < cc; i++)
                {
                    var child = tr.GetChild(i);
                    if (child != null) stack.Push(child);
                }
            }

            // Second pass: link children in original sibling order.
            foreach (var kv in nodes)
            {
                var tr = kv.Key;
                var children = (List<object>)kv.Value["children"];
                int cc = tr.childCount;
                for (int i = 0; i < cc; i++)
                {
                    var child = tr.GetChild(i);
                    if (child != null && nodes.TryGetValue(child, out var childNode))
                        children.Add(childNode);
                }
            }

            return nodes[rootTransform];
        }

        private static Dictionary<string, object> BuildGameObjectNode(GameObject go)
        {
            var tr = go.transform;
            return new Dictionary<string, object>
            {
                { "name", go.name },
                { "activeSelf", go.activeSelf },
                { "activeInHierarchy", go.activeInHierarchy },
                { "tag", go.tag },
                { "layer", go.layer },
                { "isStatic", go.isStatic },
                { "instanceID", go.GetStableInstanceId() },
                {
                    "transform",
                    new
                    {
                        position = new
                        {
                            x = tr.localPosition.x,
                            y = tr.localPosition.y,
                            z = tr.localPosition.z,
                        },
                        rotation = new
                        {
                            x = tr.localRotation.eulerAngles.x,
                            y = tr.localRotation.eulerAngles.y,
                            z = tr.localRotation.eulerAngles.z,
                        },
                        scale = new
                        {
                            x = tr.localScale.x,
                            y = tr.localScale.y,
                            z = tr.localScale.z,
                        },
                    }
                },
                { "children", new List<object>() },
            };
        }
    }
}
