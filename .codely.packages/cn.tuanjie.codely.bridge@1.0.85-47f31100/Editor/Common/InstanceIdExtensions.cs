using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
#if UNITY_2021_2_OR_NEWER
using UnityEditor.SceneManagement;
#else
using UnityEditor.Experimental.SceneManagement;
#endif

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Central compatibility surface for Unity's instance-id → <c>EntityId</c>
    /// migration (Unity 6000.5 / Unity 6.5).
    ///
    /// Unity 6.5 deprecated every <see cref="int"/>-based identity API with an
    /// error-level obsolete attribute (CS0619) in favour of 64-bit <c>EntityId</c>
    /// overloads that do not exist on earlier versions:
    ///   Object.GetInstanceID()                    -> Object.GetEntityId()
    ///   Selection.activeInstanceID                -> Selection.activeEntityId
    ///   EditorUtility.InstanceIDToObject(int)     -> EditorUtility.EntityIdToObject(EntityId)
    ///   AssetDatabase.GetAssetPath(int)           -> AssetDatabase.GetAssetPath(Object)
    ///
    /// These helpers speak <see cref="long"/> so the full 64-bit EntityId value is
    /// carried losslessly and can be reconstructed exactly (no 32-bit truncation).
    /// On pre-6.5 Unity the legacy <c>int</c> instance id widens implicitly to
    /// <c>long</c>, so call sites see a single consistent type across versions. The
    /// only version-specific code lives in the two conversion helpers at the bottom.
    /// </summary>
    public static class InstanceIdExtensions
    {
        // ---- producers: Object / Selection -> long id -------------------------

        /// <summary>
        /// Returns the object's stable instance id as a <see cref="long"/>, or 0 if
        /// the object is null/destroyed. Prefer this over <c>GetInstanceID()</c>.
        /// </summary>
        public static long GetStableInstanceId(this Object obj)
        {
            if (obj == null) return 0;
#if UNITY_6000_5_OR_NEWER
            return ToLong(obj.GetEntityId());
#else
#pragma warning disable CS0618
            return obj.GetInstanceID();
#pragma warning restore CS0618
#endif
        }

        /// <summary>Active selection instance id, or 0 if nothing is selected.</summary>
        public static long ActiveSelectionInstanceId()
        {
#if UNITY_6000_5_OR_NEWER
            return ToLong(Selection.activeEntityId);
#else
#pragma warning disable CS0618
            return Selection.activeInstanceID;
#pragma warning restore CS0618
#endif
        }

        // ---- consumers: long id -> Object / path ------------------------------

        /// <summary>Resolves an instance id to its <see cref="Object"/>, or null.</summary>
        public static Object InstanceIdToObject(long instanceId)
        {
#if UNITY_6000_5_OR_NEWER
            return EditorUtility.EntityIdToObject(ToEntityId(instanceId));
#else
#pragma warning disable CS0618
            return EditorUtility.InstanceIDToObject((int)instanceId);
#pragma warning restore CS0618
#endif
        }

        /// <summary>
        /// Resolves an instance id to its <see cref="Object"/>, or null.
        /// Same lookup as <see cref="InstanceIdToObject"/>; kept for existing call sites.
        /// </summary>
        public static Object ObjectFromInstanceId(long instanceId)
            => InstanceIdToObject(instanceId);

        /// <summary>
        /// Persistent YAML fileID. Uses <see cref="GlobalObjectId.targetObjectId"/>
        /// (ulong) so Tuanjie does not truncate values above Int32
        /// (e.g. 60588083669363838 must not become -2068059010).
        /// Returns 0 if the object is not yet written to disk.
        /// </summary>
        public static long GetLocalFileId(this Object obj)
        {
            if (obj == null) return 0;
            var gid = GlobalObjectId.GetGlobalObjectIdSlow(obj);
            return unchecked((long)gid.targetObjectId);
        }

        /// <summary>
        /// Decimal string of <see cref="GetLocalFileId"/>, or null when unknown.
        /// Wire this rather than a JSON number: values above 2^53 are not safe
        /// in JavaScript.
        /// </summary>
        public static string FormatLocalFileId(this Object obj)
        {
            if (obj == null) return null;
            ulong id = GlobalObjectId.GetGlobalObjectIdSlow(obj).targetObjectId;
            return id == 0 ? null : id.ToString();
        }

        /// <summary>
        /// Decimal string of <see cref="GetStableInstanceId"/>, or null when 0.
        /// Wire this rather than a JSON number: Unity 6.5 EntityId values
        /// can exceed JavaScript's 2^53-safe integer range.
        /// </summary>
        public static string FormatStableInstanceId(this Object obj)
        {
            if (obj == null) return null;
            long id = obj.GetStableInstanceId();
            return id == 0 ? null : id.ToString();
        }

        /// <summary>
        /// Selection kind for CLI routing:
        /// staged / sceneInstance / subAsset / projectAsset.
        /// </summary>
        public static string GetKind(this Object obj)
        {
            if (obj == null) return null;
            if (obj is GameObject go)
            {
                if (PrefabStageUtility.GetPrefabStage(go) != null)
                    return "staged";
                if (go.scene.IsValid() && !string.IsNullOrEmpty(go.scene.path))
                    return "sceneInstance";
            }
            if (AssetDatabase.IsSubAsset(obj))
                return "subAsset";
            if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(obj)))
                return "projectAsset";
            return null;
        }

        /// <summary>
        /// Prefab asset path for a stage. Unity 2019 exposes
        /// <c>prefabAssetPath</c>; 2020.1+ renamed it to <c>assetPath</c>.
        /// </summary>
        public static string GetPrefabStageAssetPath(PrefabStage stage)
        {
            if (stage == null) return null;
#if UNITY_2020_1_OR_NEWER
            return stage.assetPath;
#else
            return stage.prefabAssetPath;
#endif
        }

        /// <summary>
        /// GUID from <see cref="GlobalObjectId.assetGUID"/> so staged prefab
        /// objects still resolve to the source prefab, not an empty asset path.
        /// </summary>
        public static string FormatGlobalObjectGuid(this Object obj)
        {
            if (obj == null) return null;
            string guid = GlobalObjectId.GetGlobalObjectIdSlow(obj).assetGUID.ToString();
            if (!IsEmptyGuid(guid)) return guid;

            // Staged / scene objects sometimes yield an all-zero assetGUID.
            // Fall back to the prefab or scene asset path.
            string path = null;
            if (obj is GameObject go)
            {
                var stage = PrefabStageUtility.GetPrefabStage(go);
                string stagePath = GetPrefabStageAssetPath(stage);
                if (!string.IsNullOrEmpty(stagePath))
                    path = stagePath;
                else if (go.scene.IsValid() && !string.IsNullOrEmpty(go.scene.path))
                    path = go.scene.path;
            }
            if (string.IsNullOrEmpty(path))
                path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) return null;
            guid = AssetDatabase.AssetPathToGUID(path);
            return IsEmptyGuid(guid) ? null : guid;
        }

        private static bool IsEmptyGuid(string guid)
        {
            return string.IsNullOrEmpty(guid) || guid == "00000000000000000000000000000000";
        }

        /// <summary>
        /// Hierarchy path with same-name ordinal only when a parent has more than
        /// one child of the same name (e.g. Cube, Sphere, Cube → second Cube is Cube[1]).
        /// The index is among same-name siblings, not Transform.GetSiblingIndex().
        /// </summary>
        public static string GetHierarchyPathWithIndex(this GameObject go)
        {
            if (go == null) return "";
            var segments = new List<string>();
            var t = go.transform;
            while (t != null)
            {
                var parent = t.parent;
                string seg = t.name;
                if (parent != null && HasSameNameSibling(parent, t))
                    seg = $"{t.name}[{GetSameNameOrdinal(parent, t)}]";
                segments.Insert(0, seg);
                t = parent;
            }
            return string.Join("/", segments);
        }

        /// <summary>
        /// 0-based index of <paramref name="self"/> among siblings that share its name.
        /// </summary>
        public static int GetSameNameOrdinal(Transform parent, Transform self)
        {
            int ordinal = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name != self.name) continue;
                if (child == self) return ordinal;
                ordinal++;
            }
            return ordinal;
        }

        /// <summary>
        /// Parses a path segment produced by <see cref="GetHierarchyPathWithIndex"/>.
        /// <paramref name="ordinal"/> is -1 when the name is unique (no suffix).
        /// </summary>
        public static bool TryParseHierarchySegment(string segment, out string name, out int ordinal)
        {
            name = segment ?? "";
            ordinal = -1;
            if (string.IsNullOrEmpty(segment)) return false;
            int close = segment.LastIndexOf(']');
            int open = segment.LastIndexOf('[');
            if (open > 0 && close == segment.Length - 1 && close > open + 1)
            {
                int n;
                if (int.TryParse(segment.Substring(open + 1, close - open - 1), out n) && n >= 0)
                {
                    name = segment.Substring(0, open);
                    ordinal = n;
                }
            }
            return true;
        }

        public static string[] GetComponentTypeNames(this GameObject go)
        {
            if (go == null) return new string[0];
            var comps = go.GetComponents<Component>();
            var names = new List<string>(comps.Length);
            for (int i = 0; i < comps.Length; i++)
            {
                if (comps[i] != null)
                    names.Add(comps[i].GetType().FullName);
            }
            return names.ToArray();
        }

        private static bool HasSameNameSibling(Transform parent, Transform self)
        {
            int count = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                if (parent.GetChild(i).name != self.name) continue;
                count++;
                if (count > 1) return true;
            }
            return false;
        }

        /// <summary>Asset GUID from path, or null if the object is not an asset.</summary>
        public static string GetAssetGuid(this Object obj)
        {
            if (obj == null) return null;
            string guid = obj.FormatGlobalObjectGuid();
            if (!string.IsNullOrEmpty(guid)) return guid;
            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) return null;
            guid = AssetDatabase.AssetPathToGUID(path);
            return string.IsNullOrEmpty(guid) ? null : guid;
        }

        /// <summary>Asset path for an instance id, or "" if it is not an asset.</summary>
        public static string AssetPathFromInstanceId(long instanceId)
        {
            var obj = InstanceIdToObject(instanceId);
            return obj != null ? AssetDatabase.GetAssetPath(obj) : "";
        }

#if UNITY_6000_5_OR_NEWER
        // ---- the ONLY version-specific conversions ----------------------------
        // Both are pure 64-bit bit reinterpretations of EntityId.ToULong (the
        // canonical numeric form of an EntityId, which this project already uses),
        // so ToEntityId is an exact inverse of ToLong with no loss of information.

        /// <summary>Reinterprets an <c>EntityId</c> as a 64-bit <see cref="long"/>.</summary>
        internal static long ToLong(EntityId entityId)
            => unchecked((long)EntityId.ToULong(entityId));

        /// <summary>
        /// Exact inverse of <see cref="ToLong"/>: rebuilds the <c>EntityId</c> from
        /// its 64-bit numeric form via <c>EntityId.FromULong</c> (the counterpart of
        /// <c>EntityId.ToULong</c>), so an id survives a round-trip through storage or
        /// the wire without loss.
        /// </summary>
        internal static EntityId ToEntityId(long instanceId)
            => EntityId.FromULong(unchecked((ulong)instanceId));
#endif
    }
}
