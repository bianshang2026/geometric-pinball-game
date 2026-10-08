using System.Linq;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
#if UNITY_2021_2_OR_NEWER
using UnityEditor.SceneManagement;
#else
using UnityEditor.Experimental.SceneManagement;
#endif
using UnityEngine;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Builds the standard Unity selection payload shared by
    /// <see cref="UnityTcp.Editor.Notifier.SelectionChangedNotifier"/> and
    /// <see cref="UnityTcp.Editor.Tools.ManageEditor"/>'s get_selection action,
    /// keeping the two callers in sync without duplicating logic.
    /// </summary>
    public static class SelectionPayloadBuilder
    {
        /// <summary>
        /// Constructs a <see cref="JObject"/> describing the current Unity Editor selection.
        /// The caller is responsible for wrapping the result (e.g. in
        /// <see cref="Response.Success"/> or directly broadcasting it as a notification).
        /// </summary>
        public static JObject Build()
        {
            var assetGUIDs = Selection.assetGUIDs;
            var assetPaths = assetGUIDs?.Select(AssetDatabase.GUIDToAssetPath).ToArray();

            string activeAssetPath = Selection.activeObject != null
                ? AssetDatabase.GetAssetPath(Selection.activeObject)
                : null;
            string activeAsset = string.IsNullOrEmpty(activeAssetPath)
                ? null
                : activeAssetPath.Replace('\\', '/');

            bool isSubAsset = Selection.activeObject != null
                && AssetDatabase.IsSubAsset(Selection.activeObject);
            string activeAssetFileId = string.IsNullOrEmpty(activeAsset)
                ? null
                : Selection.activeObject.FormatLocalFileId();
            string activeAssetGuid = string.IsNullOrEmpty(activeAsset)
                ? null
                : Selection.activeObject.GetAssetGuid();

            var activeGo = Selection.activeGameObject;
            var goSource = activeGo != null && activeGo.scene.IsValid()
                ? PrefabUtility.GetCorrespondingObjectFromSource(activeGo)
                : null;
            string sourcePrefabGuid = goSource != null
                ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(goSource))
                : null;

            var payload = new
            {
                activeObject = Selection.activeObject?.name,
                activeObjectKind = Selection.activeObject.GetKind(),
                activeAsset = activeAsset,
                // Decimal string: Tuanjie fileIDs can exceed JS Number / Int32.
                activeAssetLocalId = activeAssetFileId,
                activeAssetGuid = activeAssetGuid,
                activeSelectedSubAsset = isSubAsset && !string.IsNullOrEmpty(activeAssetFileId)
                    ? (object)new
                    {
                        type = Selection.activeObject.GetType().Name,
                        name = Selection.activeObject.name,
                        fileId = activeAssetFileId,
                    }
                    : null,
                activeGameObject = activeGo?.name,
                activeGameObjectScenePath = GetSceneOrPrefabPath(activeGo),
                // instanceID always refers to the GameObject, never Transform/component.
                // Decimal string: Unity 6.5 EntityId can exceed JS Number / 2^53.
                activeInstanceID = activeGo.FormatStableInstanceId() ?? "0",
                activeGameObjectFileId = activeGo.FormatLocalFileId(),
                activeGameObjectGuid = activeGo.FormatGlobalObjectGuid(),
                // Prefab instance children: fileId is the source-prefab local id,
                // not a scene YAML id. Pair with these to grep the prefab file.
                activeGameObjectIsPrefabInstanceChild = goSource != null,
                activeGameObjectSourcePrefabGuid = string.IsNullOrEmpty(sourcePrefabGuid) ? null : sourcePrefabGuid,
                activeGameObjectSourcePrefabFileId = goSource.FormatLocalFileId(),
                activeTransform = Selection.activeTransform?.name,
#if UNITY_2020_1_OR_NEWER
                count = Selection.count,
#else
                count = Selection.objects?.Length ?? 0,
#endif
                objects = Selection.objects
                    .Where(obj => obj != null)
                    .Select(obj => new
                    {
                        name = obj.name,
                        type = obj.GetType().FullName,
                        instanceID = obj.FormatStableInstanceId(),
                    })
                    .ToList(),
                gameObjects = Selection.gameObjects
                    .Where(go => go != null)
                    .Select(go => new
                    {
                        name = go.name,
                        path = go.GetHierarchyPathWithIndex(),
                        instanceID = go.FormatStableInstanceId(),
                        kind = go.GetKind(),
                        fileId = go.FormatLocalFileId(),
                        guid = go.FormatGlobalObjectGuid(),
                        componentNames = go.GetComponentTypeNames(),
                    })
                    .ToList(),
                assetGUIDs = assetGUIDs,
                assetPaths = assetPaths,
            };

            return JObject.FromObject(payload);
        }

        /// <summary>
        /// Returns the scene or prefab asset path that owns <paramref name="go"/>,
        /// normalised to forward-slashes. Returns <c>null</c> for null input.
        /// </summary>
        public static string GetSceneOrPrefabPath(GameObject go)
        {
            if (go == null) return null;
            if (go.scene.IsValid() && !string.IsNullOrEmpty(go.scene.path))
                return go.scene.path.Replace('\\', '/');
            string stagePath = InstanceIdExtensions.GetPrefabStageAssetPath(
                PrefabStageUtility.GetPrefabStage(go));
            if (!string.IsNullOrEmpty(stagePath))
                return stagePath.Replace('\\', '/');
            string assetPath = AssetDatabase.GetAssetPath(go);
            return string.IsNullOrEmpty(assetPath) ? null : assetPath.Replace('\\', '/');
        }
    }
}
