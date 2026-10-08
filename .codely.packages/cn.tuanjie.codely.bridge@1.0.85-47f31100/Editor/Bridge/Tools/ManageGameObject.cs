using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Codely.Newtonsoft.Json; // Added for JsonSerializationException
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation; // For CompilationPipeline
#if UNITY_2021_2_OR_NEWER
using UnityEditor.SceneManagement;
#else
using UnityEditor.Experimental.SceneManagement;
#endif
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityTcp.Editor.Helpers; // For Response class
using UnityTcp.Editor.Serialization;

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Handles GameObject manipulation within the current scene (CRUD, find, components).
    /// </summary>
    public static partial class ManageGameObject
    {
        private const int MaxBatchOps = 10;

        // Shared JsonSerializer to avoid per-call allocation overhead
        private static readonly JsonSerializer InputSerializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            Converters = new List<JsonConverter>
            {
                new Vector3Converter(),
                new Vector2Converter(), 
                new QuaternionConverter(),
                new ColorConverter(),
                new RectConverter(),
                new BoundsConverter(),
                new UnityEngineObjectConverter()
            }
        });
        
        // --- Main Handler ---

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return Response.Error("Parameters cannot be null.");
            }

            if (!ActionRouter.TryResolve(
                    @params,
                    ActionHandlers.Keys,
                    out var action,
                    out var resolveError))
            {
                return resolveError;
            }
            @params["action"] = action;

            // Normalize public aliases to canonical server actions / params (keep parity with TS client)

            // Back-compat alias: componentType (camelCase) -> component_type (snake_case)
            if (@params["component_type"] == null && @params["componentType"] != null)
            {
                @params["component_type"] = @params["componentType"];
            }

            // Back-compat structured targetRef -> target + searchMethod
            if (@params["target"] == null && @params["targetRef"] is JObject targetRef)
            {
                if (targetRef["id"] != null)
                {
                    @params["target"] = targetRef["id"];
                    @params["searchMethod"] = "by_id";
                }
                else if (targetRef["hierarchy_path"] != null)
                {
                    @params["target"] = targetRef["hierarchy_path"];
                    @params["searchMethod"] = "by_path";
                }
                else if (targetRef["name"] != null)
                {
                    @params["target"] = targetRef["name"];
                    @params["searchMethod"] = "by_name";
                }
            }
            // Parameters used by various actions
            JToken targetToken = @params["target"]; // Can be string (name/path) or int (instanceID)
            string searchMethod = @params["searchMethod"]?.ToString().ToLower();

            // Get common parameters (consolidated)
            string name = @params["name"]?.ToString();
            string tag = @params["tag"]?.ToString();
            string layer = @params["layer"]?.ToString();
            JToken parentToken = @params["parent"];

            // --- Add parameter for controlling non-public field inclusion ---
            bool includeNonPublicSerialized = @params["includeNonPublicSerialized"]?.ToObject<bool>() ?? true; // Default to true
            // --- End add parameter ---

            // --- Prefab Redirection Check ---
            string targetPath =
                targetToken?.Type == JTokenType.String ? targetToken.ToString() : null;
            if (
                !string.IsNullOrEmpty(targetPath)
                && targetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
            )
            {
                // Allow 'create' (instantiate), 'find' (?), 'get_components' (?)
                if (action == "modify" || action == "set_component_property")
                {
                    CodelyLogger.Log(
                        $"[ManageGameObject->ManageAsset] Redirecting action '{action}' for prefab '{targetPath}' to ManageAsset."
                    );
                    // Prepare params for ManageAsset.ModifyAsset
                    JObject assetParams = new JObject();
                    assetParams["action"] = "modify"; // ManageAsset uses "modify"
                    assetParams["path"] = targetPath;

                    // Extract properties.
                    // For 'set_component_property', combine componentName and componentProperties.
                    // For 'modify', directly use componentProperties.
                    JObject properties = null;
                    if (action == "set_component_property")
                    {
                        string compName = @params["componentName"]?.ToString();
                        JObject compProps = @params["componentProperties"]?[compName] as JObject; // Handle potential nesting
                        if (string.IsNullOrEmpty(compName))
                            return Response.Error(
                                "Missing 'componentName' for 'set_component_property' on prefab."
                            );
                        if (compProps == null)
                            return Response.Error(
                                $"Missing or invalid 'componentProperties' for component '{compName}' for 'set_component_property' on prefab."
                            );

                        properties = new JObject();
                        properties[compName] = compProps;
                    }
                    else // action == "modify"
                    {
                        properties = @params["componentProperties"] as JObject;
                        if (properties == null)
                            return Response.Error(
                                "Missing 'componentProperties' for 'modify' action on prefab."
                            );
                    }

                    assetParams["properties"] = properties;

                    // Call ManageAsset handler
                    return ManageAsset.HandleCommand(assetParams);
                }
                else if (
                    action == "delete"
                    || action == "remove_component"
                    || action == "get_components"
                ) // Added get_components here too
                {
                    // Explicitly block other modifications on the prefab asset itself via manage_gameobject
                    return Response.Error(
                        $"Action '{action}' on a prefab asset ('{targetPath}') should be performed using the 'manage_asset' command."
                    );
                }
                // Allow 'create' (instantiation) and 'find' to proceed, although finding a prefab asset by path might be less common via manage_gameobject.
                // No specific handling needed here, the code below will run.
            }
            // --- End Prefab Redirection Check ---

            return ActionRouter.Route(@params, ActionHandlers);
        }

        private static readonly Dictionary<string, Func<JObject, object>> ActionHandlers =
            new Dictionary<string, Func<JObject, object>>
            {
                { "create_batch", HandleCreateBatch },
                { "edit_batch", HandleEditBatch },
                { "ensure_component", p => EnsureComponent(p, p["target"], SearchMethodOf(p)) },
                { "ensure_renderer_material", p => EnsureRendererMaterial(p, p["target"], SearchMethodOf(p)) },
                { "ensure_mesh_collider_mesh", p => EnsureMeshColliderMesh(p, p["target"], SearchMethodOf(p)) },
                { "ensure_prefab_default_sprite", EnsurePrefabDefaultSprite },
                { "create", CreateGameObject },
                { "modify", p => ModifyGameObject(p, p["target"], SearchMethodOf(p)) },
                { "delete", p => DeleteGameObject(p["target"], SearchMethodOf(p)) },
                { "find", p => FindGameObjects(p, p["target"], SearchMethodOf(p)) },
                { "list_children", p => ListChildren(p, p["target"], SearchMethodOf(p)) },
                { "get_components", GetComponentsAction },
                { "remove_component", p => RemoveComponentFromTarget(p, p["target"], SearchMethodOf(p)) },
                { "set_component_property", p => SetComponentPropertyOnTarget(p, p["target"], SearchMethodOf(p)) },
            };

        private static string SearchMethodOf(JObject p)
            => p["searchMethod"]?.ToString()?.ToLower();

        private static object GetComponentsAction(JObject p)
        {
            JToken targetToken = p["target"];
            string getCompTarget = targetToken?.ToString();
            if (getCompTarget == null)
                return Response.Error("'target' parameter required for get_components.");
            bool includeNonPublicSerialized =
                p["includeNonPublicSerialized"]?.ToObject<bool>() ?? true;
            return GetComponentsFromTarget(
                getCompTarget, SearchMethodOf(p), includeNonPublicSerialized);
        }

        // --- Action Implementations ---

        private static object CreateGameObject(JObject @params)
        {
            string name = @params["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
            {
                return Response.Error("'name' parameter is required for 'create' action.");
            }

            // Get prefab creation parameters
            bool saveAsPrefab = @params["saveAsPrefab"]?.ToObject<bool>() ?? false;
            string prefabPath = @params["prefabPath"]?.ToString();
            string tag = @params["tag"]?.ToString(); // Get tag for creation
            string primitiveType = @params["primitiveType"]?.ToString(); // Keep primitiveType check
            GameObject newGo = null; // Initialize as null

            // Guardrail: primitiveType creates MeshRenderer/MeshFilter primitives and conflicts with SpriteRenderer.
            // Keep parity with TS client validation.
            if (!string.IsNullOrEmpty(primitiveType) && @params["componentsToAdd"] is JArray cta)
            {
                foreach (var compToken in cta)
                {
                    if (compToken?.Type == JTokenType.String
                        && string.Equals(compToken.ToString(), "SpriteRenderer", StringComparison.OrdinalIgnoreCase))
                    {
                        return Response.Error(
                            "Cannot add 'SpriteRenderer' when creating a primitiveType GameObject. Create an empty GameObject (omit primitiveType) and add SpriteRenderer instead."
                        );
                    }
                }
            }

            // --- Try Instantiating Prefab First ---
            string originalPrefabPath = prefabPath; // Keep original for messages
            if (!string.IsNullOrEmpty(prefabPath))
            {
                // If no extension, search for the prefab by name
                if (
                    !prefabPath.Contains("/")
                    && !prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                )
                {
                    string prefabNameOnly = prefabPath;
                    CodelyLogger.Log(
                        $"[ManageGameObject.Create] Searching for prefab named: '{prefabNameOnly}'"
                    );
                    string[] guids = AssetDatabase.FindAssets($"t:Prefab {prefabNameOnly}");
                    if (guids.Length == 0)
                    {
                        return Response.Error(
                            $"Prefab named '{prefabNameOnly}' not found anywhere in the project."
                        );
                    }
                    else if (guids.Length > 1)
                    {
                        string foundPaths = string.Join(
                            ", ",
                            guids.Select(g => AssetDatabase.GUIDToAssetPath(g))
                        );
                        return Response.Error(
                            $"Multiple prefabs found matching name '{prefabNameOnly}': {foundPaths}. Please provide a more specific path."
                        );
                    }
                    else // Exactly one found
                    {
                        prefabPath = AssetDatabase.GUIDToAssetPath(guids[0]); // Update prefabPath with the full path
                        CodelyLogger.Log(
                            $"[ManageGameObject.Create] Found unique prefab at path: '{prefabPath}'"
                        );
                    }
                }
                else if (!prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    // If it looks like a path but doesn't end with .prefab, assume user forgot it and append it.
                    CodelyLogger.LogWarning(
                        $"[ManageGameObject.Create] Provided prefabPath '{prefabPath}' does not end with .prefab. Assuming it's missing and appending."
                    );
                    prefabPath += ".prefab";
                    // Note: This path might still not exist, AssetDatabase.LoadAssetAtPath will handle that.
                }
                // The logic above now handles finding or assuming the .prefab extension.

                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefabAsset != null)
                {
                    try
                    {
                        // Instantiate the prefab, initially place it at the root
                        // Parent will be set later if specified
                        newGo = PrefabUtility.InstantiatePrefab(prefabAsset) as GameObject;

                        if (newGo == null)
                        {
                            // This might happen if the asset exists but isn't a valid GameObject prefab somehow
                            CodelyLogger.LogError(
                                $"[ManageGameObject.Create] Failed to instantiate prefab at '{prefabPath}', asset might be corrupted or not a GameObject."
                            );
                            return Response.Error(
                                $"Failed to instantiate prefab at '{prefabPath}'."
                            );
                        }
                        // Name the instance based on the 'name' parameter, not the prefab's default name
                        if (!string.IsNullOrEmpty(name))
                        {
                            newGo.name = name;
                        }
                        // Register Undo for prefab instantiation
                        Undo.RegisterCreatedObjectUndo(
                            newGo,
                            $"Instantiate Prefab '{prefabAsset.name}' as '{newGo.name}'"
                        );
                        CodelyLogger.Log(
                            $"[ManageGameObject.Create] Instantiated prefab '{prefabAsset.name}' from path '{prefabPath}' as '{newGo.name}'."
                        );
                    }
                    catch (Exception e)
                    {
                        return Response.Error(
                            $"Error instantiating prefab '{prefabPath}': {e.Message}"
                        );
                    }
                }
                else
                {
                    // Only return error if prefabPath was specified but not found.
                    // If prefabPath was empty/null, we proceed to create primitive/empty.
                    CodelyLogger.LogWarning(
                        $"[ManageGameObject.Create] Prefab asset not found at path: '{prefabPath}'. Will proceed to create new object if specified."
                    );
                    // Do not return error here, allow fallback to primitive/empty creation
                }
            }

            // --- Fallback: Create Primitive or Empty GameObject ---
            bool createdNewObject = false; // Flag to track if we created (not instantiated)
            if (newGo == null) // Only proceed if prefab instantiation didn't happen
            {
                if (!string.IsNullOrEmpty(primitiveType))
                {
                    try
                    {
                        PrimitiveType type = (PrimitiveType)
                            Enum.Parse(typeof(PrimitiveType), primitiveType, true);
                        newGo = GameObject.CreatePrimitive(type);
                        // Set name *after* creation for primitives
                        if (!string.IsNullOrEmpty(name))
                        {
                            newGo.name = name;
                        }
                        else
                        {
                            UnityEngine.Object.DestroyImmediate(newGo); // cleanup leak
                            return Response.Error(
                                "'name' parameter is required when creating a primitive."
                            );
                        }
                        createdNewObject = true;
                    }
                    catch (ArgumentException)
                    {
                        return Response.Error(
                            $"Invalid primitive type: '{primitiveType}'. Valid types: {string.Join(", ", Enum.GetNames(typeof(PrimitiveType)))}"
                        );
                    }
                    catch (Exception e)
                    {
                        return Response.Error(
                            $"Failed to create primitive '{primitiveType}': {e.Message}"
                        );
                    }
                }
                else // Create empty GameObject
                {
                    if (string.IsNullOrEmpty(name))
                    {
                        return Response.Error(
                            "'name' parameter is required for 'create' action when not instantiating a prefab or creating a primitive."
                        );
                    }
                    newGo = new GameObject(name);
                    createdNewObject = true;
                }
                // Record creation for Undo *only* if we created a new object
                if (createdNewObject)
                {
                    Undo.RegisterCreatedObjectUndo(newGo, $"Create GameObject '{newGo.name}'");
                }
            }
            // --- Common Setup (Parent, Transform, Tag, Components) - Applied AFTER object exists ---
            if (newGo == null)
            {
                // Should theoretically not happen if logic above is correct, but safety check.
                return Response.Error("Failed to create or instantiate the GameObject.");
            }

            // Record potential changes to the existing prefab instance or the new GO
            // Record transform separately in case parent changes affect it
            Undo.RecordObject(newGo.transform, "Set GameObject Transform");
            Undo.RecordObject(newGo, "Set GameObject Properties");

            // Set Parent
            JToken parentToken = @params["parent"];
            if (parentToken != null)
            {
                GameObject parentGo = FindObjectInternal(parentToken, "by_id_or_name_or_path"); // Flexible parent finding
                if (parentGo == null)
                {
                    UnityEngine.Object.DestroyImmediate(newGo); // Clean up created object
                    return Response.Error($"Parent specified ('{parentToken}') but not found.");
                }
                newGo.transform.SetParent(parentGo.transform, true); // worldPositionStays = true
            }

            // Set Transform
            Vector3? position = ParseVector3(@params["position"] as JArray);
            Vector3? rotation = ParseVector3(@params["rotation"] as JArray);
            Vector3? scale = ParseVector3(@params["scale"] as JArray);

            if (position.HasValue)
                newGo.transform.localPosition = position.Value;
            if (rotation.HasValue)
                newGo.transform.localEulerAngles = rotation.Value;
            if (scale.HasValue)
                newGo.transform.localScale = scale.Value;

            // Set Tag (added for create action)
            if (!string.IsNullOrEmpty(tag))
            {
                // Similar logic as in ModifyGameObject for setting/creating tags
                string tagToSet = string.IsNullOrEmpty(tag) ? "Untagged" : tag;
                try
                {
                    newGo.tag = tagToSet;
                }
                catch (UnityException ex)
                {
                    if (ex.Message.Contains("is not defined"))
                    {
                        CodelyLogger.LogWarning(
                            $"[ManageGameObject.Create] Tag '{tagToSet}' not found. Attempting to create it."
                        );
                        try
                        {
                            InternalEditorUtility.AddTag(tagToSet);
                            newGo.tag = tagToSet; // Retry
                            CodelyLogger.Log(
                                $"[ManageGameObject.Create] Tag '{tagToSet}' created and assigned successfully."
                            );
                        }
                        catch (Exception innerEx)
                        {
                            UnityEngine.Object.DestroyImmediate(newGo); // Clean up
                            return Response.Error(
                                $"Failed to create or assign tag '{tagToSet}' during creation: {innerEx.Message}."
                            );
                        }
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(newGo); // Clean up
                        return Response.Error(
                            $"Failed to set tag to '{tagToSet}' during creation: {ex.Message}."
                        );
                    }
                }
            }

            // Set Layer (new for create action)
            string layerName = @params["layer"]?.ToString();
            if (!string.IsNullOrEmpty(layerName))
            {
                int layerId = LayerMask.NameToLayer(layerName);
                if (layerId != -1)
                {
                    newGo.layer = layerId;
                }
                else
                {
                    CodelyLogger.LogWarning(
                        $"[ManageGameObject.Create] Layer '{layerName}' not found. Using default layer."
                    );
                }
            }

            // Add Components
            if (@params["componentsToAdd"] is JArray componentsToAddArray)
            {
                foreach (var compToken in componentsToAddArray)
                {
                    string typeName = null;
                    JObject properties = null;

                    if (compToken.Type == JTokenType.String)
                    {
                        typeName = compToken.ToString();
                    }
                    else if (compToken is JObject compObj)
                    {
                        typeName = compObj["typeName"]?.ToString();
                        properties = compObj["properties"] as JObject;
                    }

                    if (!string.IsNullOrEmpty(typeName))
                    {
                        var addResult = AddComponentInternal(newGo, typeName, properties);
                        if (addResult != null) // Check if AddComponentInternal returned an error object
                        {
                            UnityEngine.Object.DestroyImmediate(newGo); // Clean up
                            return addResult; // Return the error response
                        }
                    }
                    else
                    {
                        CodelyLogger.LogWarning(
                            $"[ManageGameObject] Invalid component format in componentsToAdd: {compToken}"
                        );
                    }
                }
            }

            // Save as Prefab ONLY if we *created* a new object AND saveAsPrefab is true
            GameObject finalInstance = newGo; // Use this for selection and return data
            if (createdNewObject && saveAsPrefab)
            {
                string finalPrefabPath = prefabPath; // Use a separate variable for saving path
                // This check should now happen *before* attempting to save
                if (string.IsNullOrEmpty(finalPrefabPath))
                {
                    // Clean up the created object before returning error
                    UnityEngine.Object.DestroyImmediate(newGo);
                    return Response.Error(
                        "'prefabPath' is required when 'saveAsPrefab' is true and creating a new object."
                    );
                }
                // Ensure the *saving* path ends with .prefab
                if (!finalPrefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    CodelyLogger.Log(
                        $"[ManageGameObject.Create] Appending .prefab extension to save path: '{finalPrefabPath}' -> '{finalPrefabPath}.prefab'"
                    );
                    finalPrefabPath += ".prefab";
                }

                // Validate the prefab path stays within Assets/ before creating any files.
                // ManageAsset.TrySanitizeAssetPath uses Path.GetFullPath to reject ".." escapes.
                if (!ManageAsset.TrySanitizeAssetPath(finalPrefabPath, out string sanitizedPrefabPath, out var prefabPathError))
                    return prefabPathError;
                finalPrefabPath = sanitizedPrefabPath;

                try
                {
                    // Ensure directory exists using AssetDatabase API so Unity registers
                    // the folder (creates .meta) without needing a manual Refresh().
                    string assetDir = System.IO.Path.GetDirectoryName(finalPrefabPath)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(assetDir))
                        ManageAsset.EnsureDirectoryExists(assetDir);

                    // Use SaveAsPrefabAssetAndConnect with the final saving path
                    finalInstance = PrefabUtility.SaveAsPrefabAssetAndConnect(
                        newGo,
                        finalPrefabPath,
                        InteractionMode.UserAction
                    );

                    if (finalInstance == null)
                    {
                        // Destroy the original if saving failed somehow (shouldn't usually happen if path is valid)
                        UnityEngine.Object.DestroyImmediate(newGo);
                        return Response.Error(
                            $"Failed to save GameObject '{name}' as prefab at '{finalPrefabPath}'. Check path and permissions."
                        );
                    }
                    CodelyLogger.Log(
                        $"[ManageGameObject.Create] GameObject '{name}' saved as prefab to '{finalPrefabPath}' and instance connected."
                    );
                    // Mark the new prefab asset as dirty? Not usually necessary, SaveAsPrefabAsset handles it.
                    // EditorUtility.SetDirty(finalInstance); // Instance is handled by SaveAsPrefabAssetAndConnect
                }
                catch (Exception e)
                {
                    // Clean up the instance if prefab saving fails
                    UnityEngine.Object.DestroyImmediate(newGo); // Destroy the original attempt
                    return Response.Error($"Error saving prefab '{finalPrefabPath}': {e.Message}");
                }
            }

            // Select the instance in the scene (either prefab instance or newly created/saved one)
            Selection.activeGameObject = finalInstance;

            // Determine appropriate success message using the potentially updated or original path
            string messagePrefabPath =
                finalInstance == null
                    ? originalPrefabPath
                    : AssetDatabase.GetAssetPath(
                        PrefabUtility.GetCorrespondingObjectFromSource(finalInstance)
                            ?? (UnityEngine.Object)finalInstance
                    );
            string successMessage;
            if (!createdNewObject && !string.IsNullOrEmpty(messagePrefabPath)) // Instantiated existing prefab
            {
                successMessage =
                    $"Prefab '{messagePrefabPath}' instantiated successfully as '{finalInstance.name}'.";
            }
            else if (createdNewObject && saveAsPrefab && !string.IsNullOrEmpty(messagePrefabPath)) // Created new and saved as prefab
            {
                successMessage =
                    $"GameObject '{finalInstance.name}' created and saved as prefab to '{messagePrefabPath}'.";
            }
            else // Created new primitive or empty GO, didn't save as prefab
            {
                successMessage =
                    $"GameObject '{finalInstance.name}' created successfully in scene.";
            }

            // Use the new serializer helper
            //return Response.Success(successMessage, GetGameObjectData(finalInstance));
            return Response.Success(successMessage, Helpers.GameObjectSerializer.GetGameObjectData(finalInstance));
        }

        private static object ModifyGameObject(
            JObject @params,
            JToken targetToken,
            string searchMethod
        )
        {
            GameObject targetGo = FindObjectInternal(targetToken, searchMethod);
            if (targetGo == null)
            {
                return Response.Error(
                    $"Target GameObject ('{targetToken}') not found using method '{searchMethod ?? "default"}'."
                );
            }

            // Record state for Undo *before* modifications
            Undo.RecordObject(targetGo.transform, "Modify GameObject Transform");
            Undo.RecordObject(targetGo, "Modify GameObject Properties");

            bool modified = false;

            // Rename (using consolidated 'name' parameter)
            string name = @params["name"]?.ToString();
            if (!string.IsNullOrEmpty(name) && targetGo.name != name)
            {
                targetGo.name = name;
                modified = true;
            }

            // Change Parent (using consolidated 'parent' parameter)
            JToken parentToken = @params["parent"];
            if (parentToken != null)
            {
                GameObject newParentGo = FindObjectInternal(parentToken, "by_id_or_name_or_path");
                // Check for hierarchy loops
                if (
                    newParentGo == null
                    && !(
                        parentToken.Type == JTokenType.Null
                        || (
                            parentToken.Type == JTokenType.String
                            && string.IsNullOrEmpty(parentToken.ToString())
                        )
                    )
                )
                {
                    return Response.Error($"New parent ('{parentToken}') not found.");
                }
                if (newParentGo != null && newParentGo.transform.IsChildOf(targetGo.transform))
                {
                    return Response.Error(
                        $"Cannot parent '{targetGo.name}' to '{newParentGo.name}', as it would create a hierarchy loop."
                    );
                }
                if (targetGo.transform.parent != (newParentGo?.transform))
                {
                    targetGo.transform.SetParent(newParentGo?.transform, true); // worldPositionStays = true
                    modified = true;
                }
            }

            // Set Active State
            bool? setActive = @params["setActive"]?.ToObject<bool?>();
            if (setActive.HasValue && targetGo.activeSelf != setActive.Value)
            {
                targetGo.SetActive(setActive.Value);
                modified = true;
            }

            // Change Tag (using consolidated 'tag' parameter)
            string tag = @params["tag"]?.ToString();
            // Only attempt to change tag if a non-null tag is provided and it's different from the current one.
            // Allow setting an empty string to remove the tag (Unity uses "Untagged").
            if (tag != null && targetGo.tag != tag)
            {
                // Ensure the tag is not empty, if empty, it means "Untagged" implicitly
                string tagToSet = string.IsNullOrEmpty(tag) ? "Untagged" : tag;
                try
                {
                    targetGo.tag = tagToSet;
                    modified = true;
                }
                catch (UnityException ex)
                {
                    // Check if the error is specifically because the tag doesn't exist
                    if (ex.Message.Contains("is not defined"))
                    {
                        CodelyLogger.LogWarning(
                            $"[ManageGameObject] Tag '{tagToSet}' not found. Attempting to create it."
                        );
                        try
                        {
                            // Attempt to create the tag using internal utility
                            InternalEditorUtility.AddTag(tagToSet);
                            // Wait a frame maybe? Not strictly necessary but sometimes helps editor updates.
                            // yield return null; // Cannot yield here, editor script limitation

                            // Retry setting the tag immediately after creation
                            targetGo.tag = tagToSet;
                            modified = true;
                            CodelyLogger.Log(
                                $"[ManageGameObject] Tag '{tagToSet}' created and assigned successfully."
                            );
                        }
                        catch (Exception innerEx)
                        {
                            // Handle failure during tag creation or the second assignment attempt
                            CodelyLogger.LogError(
                                $"[ManageGameObject] Failed to create or assign tag '{tagToSet}' after attempting creation: {innerEx.Message}"
                            );
                            return Response.Error(
                                $"Failed to create or assign tag '{tagToSet}': {innerEx.Message}. Check Tag Manager and permissions."
                            );
                        }
                    }
                    else
                    {
                        // If the exception was for a different reason, return the original error
                        return Response.Error($"Failed to set tag to '{tagToSet}': {ex.Message}.");
                    }
                }
            }

            // Change Layer (using consolidated 'layer' parameter)
            string layerName = @params["layer"]?.ToString();
            if (!string.IsNullOrEmpty(layerName))
            {
                int layerId = LayerMask.NameToLayer(layerName);
                if (layerId == -1 && layerName != "Default")
                {
                    return Response.Error(
                        $"Invalid layer specified: '{layerName}'. Use a valid layer name."
                    );
                }
                if (layerId != -1 && targetGo.layer != layerId)
                {
                    targetGo.layer = layerId;
                    modified = true;
                }
            }

            // Transform Modifications
            Vector3? position = ParseVector3(@params["position"] as JArray);
            Vector3? rotation = ParseVector3(@params["rotation"] as JArray);
            Vector3? scale = ParseVector3(@params["scale"] as JArray);

            if (position.HasValue && targetGo.transform.localPosition != position.Value)
            {
                targetGo.transform.localPosition = position.Value;
                modified = true;
            }
            if (rotation.HasValue && targetGo.transform.localEulerAngles != rotation.Value)
            {
                targetGo.transform.localEulerAngles = rotation.Value;
                modified = true;
            }
            if (scale.HasValue && targetGo.transform.localScale != scale.Value)
            {
                targetGo.transform.localScale = scale.Value;
                modified = true;
            }

            // --- Component Modifications ---
            // Note: These might need more specific Undo recording per component

            // Remove Components
            if (@params["componentsToRemove"] is JArray componentsToRemoveArray)
            {
                foreach (var compToken in componentsToRemoveArray)
                {
                    // ... (parsing logic as in CreateGameObject) ...
                    string typeName = compToken.ToString();
                    if (!string.IsNullOrEmpty(typeName))
                    {
                        var removeResult = RemoveComponentInternal(targetGo, typeName);
                        if (removeResult != null)
                            return removeResult; // Return error if removal failed
                        modified = true;
                    }
                }
            }

            // Add Components (similar to create)
            if (@params["componentsToAdd"] is JArray componentsToAddArrayModify)
            {
                foreach (var compToken in componentsToAddArrayModify)
                {
                    string typeName = null;
                    JObject properties = null;
                    if (compToken.Type == JTokenType.String)
                        typeName = compToken.ToString();
                    else if (compToken is JObject compObj)
                    {
                        typeName = compObj["typeName"]?.ToString();
                        properties = compObj["properties"] as JObject;
                    }

                    if (!string.IsNullOrEmpty(typeName))
                    {
                        var addResult = AddComponentInternal(targetGo, typeName, properties);
                        if (addResult != null)
                            return addResult;
                        modified = true;
                    }
                }
            }

            // Set Component Properties
            var componentErrors = new List<object>();
            if (@params["componentProperties"] is JObject componentPropertiesObj)
            {
                foreach (var prop in componentPropertiesObj.Properties())
                {
                    string compName = prop.Name;
                    JObject propertiesToSet = prop.Value as JObject;
                    if (propertiesToSet != null)
                    {
                        var setResult = SetComponentPropertiesInternal(
                            targetGo,
                            compName,
                            propertiesToSet
                        );
                        if (setResult != null)
                        {
                            componentErrors.Add(setResult);
                        }
                        else
                        {
                            modified = true;
                        }
                    }
                }
            }

            // Return component errors if any occurred (after processing all components)
            if (componentErrors.Count > 0)
            {
                // Aggregate flattened error strings to make tests/API assertions simpler
                var aggregatedErrors = new System.Collections.Generic.List<string>();
                foreach (var errorObj in componentErrors)
                {
                    try
                    {
                        var dataProp = errorObj?.GetType().GetProperty("data");
                        var dataVal = dataProp?.GetValue(errorObj);
                        if (dataVal != null)
                        {
                            var errorsProp = dataVal.GetType().GetProperty("errors");
                            var errorsEnum = errorsProp?.GetValue(dataVal) as System.Collections.IEnumerable;
                            if (errorsEnum != null)
                            {
                                foreach (var item in errorsEnum)
                                {
                                    var s = item?.ToString();
                                    if (!string.IsNullOrEmpty(s)) aggregatedErrors.Add(s);
                                }
                            }
                        }
                    }
                    catch { }
                }

                return Response.Error(
                    $"One or more component property operations failed on '{targetGo.name}'.",
                    new { componentErrors = componentErrors, errors = aggregatedErrors }
                );
            }

            if (!modified)
            {
                // Use the new serializer helper
                // return Response.Success(
                //     $"No modifications applied to GameObject '{targetGo.name}'.",
                //     GetGameObjectData(targetGo));

                return Response.Success(
                    $"No modifications applied to GameObject '{targetGo.name}'.",
                    Helpers.GameObjectSerializer.GetGameObjectData(targetGo)
                );
            }

            EditorUtility.SetDirty(targetGo); // Mark scene as dirty
            // Use the new serializer helper
            return Response.Success(
                $"GameObject '{targetGo.name}' modified successfully.",
                Helpers.GameObjectSerializer.GetGameObjectData(targetGo)
            );
            // return Response.Success(
            //     $"GameObject '{targetGo.name}' modified successfully.",
            //     GetGameObjectData(targetGo));
            
        }

        private static object DeleteGameObject(JToken targetToken, string searchMethod)
        {
            // Find potentially multiple objects if name/tag search is used without find_all=false implicitly
            List<GameObject> targets = FindObjectsInternal(targetToken, searchMethod, true); // find_all=true for delete safety

            if (targets.Count == 0)
            {
                return Response.Error(
                    $"Target GameObject(s) ('{targetToken}') not found using method '{searchMethod ?? "default"}'."
                );
            }

            List<object> deletedObjects = new List<object>();
            foreach (var targetGo in targets)
            {
                if (targetGo != null)
                {
                    string goName = targetGo.name;
                    long goId = targetGo.GetStableInstanceId();
                    // Use Undo.DestroyObjectImmediate for undo support
                    Undo.DestroyObjectImmediate(targetGo);
                    deletedObjects.Add(new { name = goName, instanceID = goId });
                }
            }

            if (deletedObjects.Count > 0)
            {
                string message =
                    targets.Count == 1
                        ? $"GameObject '{deletedObjects[0].GetType().GetProperty("name").GetValue(deletedObjects[0])}' deleted successfully."
                        : $"{deletedObjects.Count} GameObjects deleted successfully.";
                return Response.Success(message, deletedObjects);
            }
            else
            {
                // Should not happen if targets.Count > 0 initially, but defensive check
                return Response.Error("Failed to delete target GameObject(s).");
            }
        }

        private static object FindGameObjects(
            JObject @params,
            JToken targetToken,
            string searchMethod
        )
        {
            bool findAll = @params["findAll"]?.ToObject<bool>() ?? false;
            List<GameObject> foundObjects = FindObjectsInternal(
                targetToken,
                searchMethod,
                findAll,
                @params
            );

            if (foundObjects.Count == 0)
            {
                return Response.Success("No matching GameObjects found.", new List<object>());
            }

            // Check if result would be too large
            if (foundObjects.Count > 100)
            {
                string searchTerm =
                    @params?["searchTerm"]?.ToString()
                    ?? targetToken?.ToString()
                    ?? "unknown";
                return Response.Error(
                    $"Too many GameObjects found ({foundObjects.Count}). Response would be too large. " +
                    "Hints to narrow scope:\n" +
                    "1. Use more specific search criteria (exact names instead of partial matches)\n" +
                    "2. Add 'findAll': false to get only the first match\n" +
                    "3. Use different searchMethod: 'by_id', 'by_path', or 'by_name' for exact matches\n" +
                    "4. Search within a specific parent object instead of the entire scene\n" +
                    $"5. Found objects include: {string.Join(", ", foundObjects.Take(10).Select(go => go.name))}{(foundObjects.Count > 10 ? "..." : "")}"
                );
            }

            // Use the new serializer helper
            //var results = foundObjects.Select(go => GetGameObjectData(go)).ToList();
            var results = foundObjects.Select(go => Helpers.GameObjectSerializer.GetGameObjectData(go)).ToList();
            return Response.Success($"Found {results.Count} GameObject(s).", results);
        }

        private static object ListChildren(
            JObject @params,
            JToken targetToken,
            string searchMethod
        )
        {
            if (targetToken == null)
            {
                return Response.Error(
                    "'target' parameter is required for list_children. Provide a GameObject name, instance ID, or hierarchy path."
                );
            }

            // Allow both names: includeInactive (preferred) and legacy searchInactive
            bool includeInactive =
                @params["includeInactive"]?.ToObject<bool>()
                ?? @params["searchInactive"]?.ToObject<bool>()
                ?? true;

            // Depth: 1 = direct children
            int depth = 1;
            try
            {
                var depthToken = @params["depth"] ?? @params["maxDepth"];
                if (depthToken != null && depthToken.Type != JTokenType.Null)
                {
                    var depthStr = depthToken.ToString().Trim();
                    if (int.TryParse(depthStr, out var parsedDepth))
                    {
                        depth = parsedDepth;
                    }
                    else if (double.TryParse(depthStr, out var parsedDouble))
                    {
                        depth = (int)parsedDouble;
                    }
                }
            }
            catch { /* fall back to default */ }

            if (depth < 1)
            {
                return Response.Error("'depth' must be >= 1.");
            }

            // resultMode: auto | inline | file
            string resultMode =
                (@params["resultMode"]?.ToString() ?? "auto").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(resultMode)) resultMode = "auto";
            if (resultMode != "auto" && resultMode != "inline" && resultMode != "file")
            {
                return Response.Error(
                    $"Invalid resultMode '{resultMode}'. Valid values: auto, inline, file."
                );
            }

            int maxInlineItems = 200;
            try
            {
                var maxInlineToken = @params["maxInlineItems"] ?? @params["inlineLimit"];
                if (maxInlineToken != null && maxInlineToken.Type != JTokenType.Null)
                {
                    var s = maxInlineToken.ToString().Trim();
                    if (int.TryParse(s, out var parsed))
                    {
                        maxInlineItems = parsed;
                    }
                    else if (double.TryParse(s, out var parsedDouble))
                    {
                        maxInlineItems = (int)parsedDouble;
                    }
                }
            }
            catch { /* fall back */ }

            if (maxInlineItems < 1) maxInlineItems = 1;

            // Resolve the target first (respect includeInactive via searchInactive)
            var findParams = new JObject();
            findParams["searchInactive"] = includeInactive;
            GameObject targetGo = FindObjectInternal(targetToken, searchMethod, findParams);
            if (targetGo == null)
            {
                return Response.Error(
                    $"Target GameObject ('{targetToken}') not found using method '{searchMethod ?? "default"}'.\n" +
                    "Next steps:\n" +
                    "1. Use action='find' with searchMethod='by_name' or 'by_path' to locate the exact object\n" +
                    "2. If you have an instanceID, set searchMethod='by_id'\n" +
                    "3. If path contains '/', prefer searchMethod='by_path' (e.g. 'Root/Child')"
                );
            }

            // Determine whether to return inline tree or write to file (no truncation).
            // We do a bounded count first so "auto" can decide without building the entire tree in memory.
            int boundedCount = CountDescendantsUpToDepth(targetGo.transform, depth, includeInactive, maxInlineItems + 1);
            bool shouldWriteToFile = resultMode == "file" || (resultMode == "auto" && boundedCount > maxInlineItems);

            if (resultMode == "inline" && boundedCount > maxInlineItems)
            {
                return Response.Error(
                    $"Result too large to return inline (>{maxInlineItems} nodes within depth={depth}). This tool will not truncate.\n" +
                    "Next steps:\n" +
                    "1. Set resultMode='file' (or 'auto') to write full JSON to disk\n" +
                    "2. Reduce depth (default is 1)\n" +
                    "3. Set includeInactive=false to reduce scope"
                );
            }

            if (shouldWriteToFile)
            {
                try
                {
                    string outputAssetsPath = ResolveOutputPathUnderAssets(@params, targetGo, "unity_gameobject_list_children");
                    string outputDiskPath = ResolveAssetsPathToDiskPath(outputAssetsPath);

                    // Stream JSON to file using iterative depth-limited traversal (no recursion).
                    int writtenCount = 0;
                    int visitedCount = 0;

                    using (var sw = new StreamWriter(outputDiskPath, false, new System.Text.UTF8Encoding(false)))
                    using (var jw = new JsonTextWriter(sw) { Formatting = Formatting.None })
                    {
                        jw.WriteStartObject();
                        jw.WritePropertyName("target");
                        WriteNodeSummary(jw, targetGo, "", 0, includeChildrenArray: false);

                        jw.WritePropertyName("params");
                        jw.WriteStartObject();
                        jw.WritePropertyName("depth"); jw.WriteValue(depth);
                        jw.WritePropertyName("includeInactive"); jw.WriteValue(includeInactive);
                        jw.WritePropertyName("resultMode"); jw.WriteValue("file");
                        jw.WriteEndObject();

                        jw.WritePropertyName("children");
                        jw.WriteStartArray();

                        WriteChildrenTreeIterative(jw, targetGo.transform, depth, includeInactive, ref writtenCount, ref visitedCount);

                        jw.WriteEndArray();
                        jw.WritePropertyName("meta");
                        jw.WriteStartObject();
                        jw.WritePropertyName("includedCount"); jw.WriteValue(writtenCount);
                        jw.WritePropertyName("visitedCount"); jw.WriteValue(visitedCount);
                        jw.WriteEndObject();
                        jw.WriteEndObject();
                        jw.Flush();
                    }

                    // Make it visible in Unity as a text asset (best-effort)
                    try { AssetDatabase.ImportAsset(outputAssetsPath); } catch { }

                    return Response.Success(
                        $"Listed child GameObjects of '{targetGo.name}' (tree up to depth={depth}). Output written to: {outputAssetsPath}",
                        new
                        {
                            target = CreateGameObjectChildSummary(targetGo, "", 0),
                            depth = depth,
                            includeInactive = includeInactive,
                            includedCount = writtenCount,
                            visitedCount = visitedCount,
                            output = new { mode = "file", path = outputAssetsPath },
                            hints = new[]
                            {
                                "If the result is still too large, reduce 'depth' (default is 1).",
                                "Set 'includeInactive': false to skip inactive subtrees.",
                                "If the target is ambiguous/not found, use action='find' with searchMethod='by_path' (e.g. 'Root/Child') or searchMethod='by_id' (instanceID).",
                                "You can set 'outputPath' under Assets/ to control where the JSON is written.",
                                "If you really want inline output, increase 'maxInlineItems' or keep 'depth' small (no truncation)."
                            }
                        }
                    );
                }
                catch (Exception e)
                {
                    return Response.Error($"Error listing children to file: {e.Message}");
                }
            }

            // Inline: build a tree structure in memory (depth-limited, non-recursive).
            try
            {
                int visitedCount = 0;
                int includedCount = 0;
                var children = BuildChildrenTreeIterative(targetGo.transform, depth, includeInactive, ref includedCount, ref visitedCount);

                return Response.Success(
                    $"Listed child GameObjects of '{targetGo.name}' (tree up to depth={depth}).",
                    new
                    {
                        target = CreateGameObjectChildSummary(targetGo, "", 0),
                        depth = depth,
                        includeInactive = includeInactive,
                        includedCount = includedCount,
                        visitedCount = visitedCount,
                        children = children,
                        output = new { mode = "inline" }
                    }
                );
            }
            catch (Exception e)
            {
                return Response.Error($"Error listing children: {e.Message}");
            }
        }

        private static int CountDescendantsUpToDepth(Transform root, int maxDepth, bool includeInactive, int cap)
        {
            if (root == null || maxDepth < 1) return 0;
            int count = 0;
            var q = new Queue<Tuple<Transform, int>>();

            int childCount = root.childCount;
            for (int i = 0; i < childCount; i++)
            {
                var c = root.GetChild(i);
                if (c != null) q.Enqueue(new Tuple<Transform, int>(c, 1));
            }

            while (q.Count > 0)
            {
                var item = q.Dequeue();
                var tr = item.Item1;
                int d = item.Item2;
                if (tr == null) continue;
                var go = tr.gameObject;
                if (go == null) continue;

                if (!includeInactive && !go.activeInHierarchy)
                {
                    // Skip subtree
                    continue;
                }

                count++;
                if (count >= cap) return cap;

                if (d < maxDepth)
                {
                    int cc = tr.childCount;
                    for (int i = 0; i < cc; i++)
                    {
                        var child = tr.GetChild(i);
                        if (child != null) q.Enqueue(new Tuple<Transform, int>(child, d + 1));
                    }
                }
            }

            return count;
        }

        private static List<object> BuildChildrenTreeIterative(
            Transform root,
            int maxDepth,
            bool includeInactive,
            ref int includedCount,
            ref int visitedCount
        )
        {
            var result = new List<object>();
            if (root == null || maxDepth < 1) return result;

            // Frame: (transform, depth, relativePath, childrenListRef, nextChildIndex)
            var stack = new Stack<Tuple<Transform, int, string, List<object>, int>>();

            // Seed with direct children in reverse order so output preserves sibling order
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                var c = root.GetChild(i);
                if (c != null)
                {
                    stack.Push(new Tuple<Transform, int, string, List<object>, int>(c, 1, c.name, result, 0));
                }
            }

            // Track node state separately: map Transform instanceID to its node + children list.
            // Using instanceID avoids holding Transform keys for long.
            var nodeById = new Dictionary<long, Dictionary<string, object>>();

            while (stack.Count > 0)
            {
                var frame = stack.Pop();
                var tr = frame.Item1;
                int d = frame.Item2;
                string relPath = frame.Item3;
                var parentChildren = frame.Item4;
                int nextChildIndex = frame.Item5;

                if (tr == null) continue;
                var go = tr.gameObject;
                if (go == null) continue;
                visitedCount++;

                if (!includeInactive && !go.activeInHierarchy)
                {
                    // Skip subtree entirely
                    continue;
                }

                long id = go.GetStableInstanceId();
                if (!nodeById.TryGetValue(id, out var node))
                {
                    // First time: create node and attach to parent
                    node = CreateGameObjectChildSummary(go, relPath, d);
                    var children = new List<object>();
                    node["children"] = children;
                    parentChildren.Add(node);
                    includedCount++;
                    nodeById[id] = node;

                    // Prepare to traverse children if we can go deeper
                    if (d < maxDepth && tr.childCount > 0)
                    {
                        // Push a continuation frame for this node after its children, then push children frames
                        // Continuation not needed because we store children directly; we just push children frames.
                        var childList = (List<object>)node["children"];
                        for (int i = tr.childCount - 1; i >= 0; i--)
                        {
                            var c = tr.GetChild(i);
                            if (c != null)
                            {
                                string childRel = string.IsNullOrEmpty(relPath) ? c.name : relPath + "/" + c.name;
                                stack.Push(new Tuple<Transform, int, string, List<object>, int>(c, d + 1, childRel, childList, 0));
                            }
                        }
                    }
                }
                else
                {
                    // Should not generally happen in a tree, but keep safety (no-op).
                    // nextChildIndex currently unused; kept for future extensions.
                    _ = nextChildIndex;
                }
            }

            return result;
        }

        private static void WriteChildrenTreeIterative(
            JsonTextWriter jw,
            Transform root,
            int maxDepth,
            bool includeInactive,
            ref int includedCount,
            ref int visitedCount
        )
        {
            if (root == null || maxDepth < 1) return;

            // Stack frame for streaming JSON without recursion:
            // (transform, depth, relativePath, childIndex, childCount, started)
            var stack = new Stack<Tuple<Transform, int, string, int, int, bool>>();

            // Seed direct children in reverse order so they are written in ascending sibling order
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                var c = root.GetChild(i);
                if (c != null)
                {
                    stack.Push(new Tuple<Transform, int, string, int, int, bool>(c, 1, c.name, 0, 0, false));
                }
            }

            while (stack.Count > 0)
            {
                var frame = stack.Pop();
                var tr = frame.Item1;
                int d = frame.Item2;
                string relPath = frame.Item3;
                int childIndex = frame.Item4;
                int childCount = frame.Item5;
                bool started = frame.Item6;

                if (tr == null) continue;
                var go = tr.gameObject;
                if (go == null) continue;

                if (!started)
                {
                    visitedCount++;
                    if (!includeInactive && !go.activeInHierarchy)
                    {
                        // Skip subtree entirely
                        continue;
                    }

                    // Start node object
                    WriteNodeSummary(jw, go, relPath, d, includeChildrenArray: true);
                    includedCount++;

                    // Prepare to write children if within depth
                    childCount = (d < maxDepth) ? tr.childCount : 0;
                    // Push continuation frame to close this node after children are written
                    stack.Push(new Tuple<Transform, int, string, int, int, bool>(tr, d, relPath, 0, childCount, true));

                    // Push children frames (reverse order for stable output order)
                    for (int i = childCount - 1; i >= 0; i--)
                    {
                        var c = tr.GetChild(i);
                        if (c != null)
                        {
                            string childRel = string.IsNullOrEmpty(relPath) ? c.name : relPath + "/" + c.name;
                            stack.Push(new Tuple<Transform, int, string, int, int, bool>(c, d + 1, childRel, 0, 0, false));
                        }
                    }
                }
                else
                {
                    // Close children array + object
                    jw.WriteEndArray();
                    jw.WriteEndObject();
                }
            }
        }

        private static string ResolveOutputPathUnderAssets(JObject @params, GameObject targetGo, string prefix)
        {
            string requested = @params["outputPath"]?.ToString();
            if (string.IsNullOrEmpty(requested))
            {
                string dir = "Assets/Codely/ToolOutputs";
                string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                requested = $"{dir}/{prefix}_{timestamp}_{targetGo.GetStableInstanceId()}.json";
            }
            requested = requested.Replace('\\', '/').Trim();
            if (!requested.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception($"outputPath must start with 'Assets/'. Provided: '{requested}'");
            }
            return requested.Replace('\\', '/');
        }

        private static string ResolveAssetsPathToDiskPath(string assetsPath)
        {
            string requested = (assetsPath ?? "").Replace('\\', '/').Trim();
            if (!requested.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception($"Path must start with 'Assets/': '{requested}'");
            }

            string assetsDisk = Application.dataPath.Replace('\\', '/');
            string relUnderAssets = requested.Substring("Assets/".Length).TrimStart('/');
            string diskPath = Path.Combine(assetsDisk, relUnderAssets).Replace('\\', '/');
            string fullDisk = Path.GetFullPath(diskPath).Replace('\\', '/');
            string assetsFull = Path.GetFullPath(assetsDisk).Replace('\\', '/');

            if (
                !fullDisk.StartsWith(assetsFull + "/", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(fullDisk, assetsFull, StringComparison.OrdinalIgnoreCase)
            )
            {
                throw new Exception($"outputPath escapes Assets/. Resolved: '{fullDisk}'");
            }

            string fullDir = Path.GetDirectoryName(fullDisk);
            if (!string.IsNullOrEmpty(fullDir))
            {
                Directory.CreateDirectory(fullDir);
            }

            return fullDisk;
        }

        private static void WriteNodeSummary(
            JsonTextWriter jw,
            GameObject go,
            string relativePathFromTarget,
            int depthFromTarget,
            bool includeChildrenArray
        )
        {
            jw.WriteStartObject();
            jw.WritePropertyName("name"); jw.WriteValue(go.name);
            jw.WritePropertyName("instanceID"); jw.WriteValue(go.GetStableInstanceId());
            jw.WritePropertyName("hierarchyPath"); jw.WriteValue(GetHierarchyPath(go));
            jw.WritePropertyName("relativePath"); jw.WriteValue(relativePathFromTarget ?? string.Empty);
            jw.WritePropertyName("depth"); jw.WriteValue(depthFromTarget);
            jw.WritePropertyName("activeSelf"); jw.WriteValue(go.activeSelf);
            jw.WritePropertyName("activeInHierarchy"); jw.WriteValue(go.activeInHierarchy);
            jw.WritePropertyName("tag"); jw.WriteValue(go.tag);
            jw.WritePropertyName("layer"); jw.WriteValue(go.layer);
            jw.WritePropertyName("isStatic"); jw.WriteValue(go.isStatic);
            jw.WritePropertyName("childCount"); jw.WriteValue(go.transform != null ? go.transform.childCount : 0);
            jw.WritePropertyName("siblingIndex"); jw.WriteValue(go.transform != null ? go.transform.GetSiblingIndex() : 0);
            jw.WritePropertyName("scenePath"); jw.WriteValue(go.scene.path ?? string.Empty);

            if (includeChildrenArray)
            {
                jw.WritePropertyName("children");
                jw.WriteStartArray();
            }
            // Caller closes children/object appropriately.
        }

        private static Dictionary<string, object> CreateGameObjectChildSummary(
            GameObject go,
            string relativePathFromTarget,
            int depthFromTarget
        )
        {
            if (go == null)
            {
                return new Dictionary<string, object>();
            }

            var tr = go.transform;
            return new Dictionary<string, object>
            {
                { "name", go.name },
                { "instanceID", go.GetStableInstanceId() },
                { "hierarchyPath", GetHierarchyPath(go) },
                { "relativePath", relativePathFromTarget ?? string.Empty },
                { "depth", depthFromTarget },
                { "activeSelf", go.activeSelf },
                { "activeInHierarchy", go.activeInHierarchy },
                { "tag", go.tag },
                { "layer", go.layer },
                { "isStatic", go.isStatic },
                { "childCount", tr != null ? tr.childCount : 0 },
                { "siblingIndex", tr != null ? tr.GetSiblingIndex() : 0 },
                { "scenePath", go.scene.path ?? string.Empty },
            };
        }

        private static string GetHierarchyPath(GameObject go)
        {
            if (go == null) return string.Empty;

            var path = go.name;
            var parent = go.transform != null ? go.transform.parent : null;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        private static object GetComponentsFromTarget(string target, string searchMethod, bool includeNonPublicSerialized = true)
        {
            GameObject targetGo = FindObjectInternal(target, searchMethod);
            if (targetGo == null)
            {
                return Response.Error(
                    $"Target GameObject ('{target}') not found using method '{searchMethod ?? "default"}'."
                );
            }

            try
            {
                // --- Get components, immediately copy to list, and null original array --- 
                Component[] originalComponents = targetGo.GetComponents<Component>();
                List<Component> componentsToIterate = new List<Component>(originalComponents ?? Array.Empty<Component>()); // Copy immediately, handle null case
                int componentCount = componentsToIterate.Count; 
                originalComponents = null; // Null the original reference
                // CodelyLogger.Log($"[GetComponentsFromTarget] Found {componentCount} components on {targetGo.name}. Copied to list, nulled original. Starting REVERSE for loop...");
                // --- End Copy and Null ---
                
                // Check if component serialization would be too large
                if (componentCount > 50) 
                {
                    return Response.Error(
                        $"GameObject '{targetGo.name}' has too many components ({componentCount}). Response would be too large. " +
                        "Hints to narrow scope:\n" +
                        "1. Use 'manage_gameobject' with action='find' to get basic GameObject info without components\n" +
                        "2. Query specific components by type instead of all components\n" +
                        "3. Use 'includeNonPublicSerialized': false to reduce serialized data per component\n" +
                        $"4. Component types found: {string.Join(", ", componentsToIterate.Where(c => c != null).Take(10).Select(c => c.GetType().Name))}{(componentCount > 10 ? "..." : "")}"
                    );
                } 
                
                var componentData = new List<object>();
                
                for (int i = componentCount - 1; i >= 0; i--) // Iterate backwards over the COPY
                {
                    Component c = componentsToIterate[i]; // Use the copy
                    if (c == null) 
                    {
                        // CodelyLogger.LogWarning($"[GetComponentsFromTarget REVERSE for] Encountered a null component at index {i} on {targetGo.name}. Skipping.");
                        continue; // Safety check
                    }
                    // CodelyLogger.Log($"[GetComponentsFromTarget REVERSE for] Processing component: {c.GetType()?.FullName ?? "null"} (ID: {c.GetStableInstanceId()}) at index {i} on {targetGo.name}");
                    try 
                    {
                        var data = Helpers.GameObjectSerializer.GetComponentData(c, includeNonPublicSerialized);
                        if (data != null) // Ensure GetComponentData didn't return null
                        {
                            componentData.Insert(0, data); // Insert at beginning to maintain original order in final list
                        }
                        // else
                        // {
                        //     CodelyLogger.LogWarning($"[GetComponentsFromTarget REVERSE for] GetComponentData returned null for component {c.GetType().FullName} (ID: {c.GetStableInstanceId()}) on {targetGo.name}. Skipping addition.");
                        // }
                    }
                    catch (Exception ex)
                    {
                        CodelyLogger.LogError($"[GetComponentsFromTarget REVERSE for] Error processing component {c.GetType().FullName} (ID: {c.GetStableInstanceId()}) on {targetGo.name}: {ex.Message}\n{ex.StackTrace}");
                        // Optionally add placeholder data or just skip
                        componentData.Insert(0, new JObject( // Insert error marker at beginning
                            new JProperty("typeName", c.GetType().FullName + " (Serialization Error)"),
                            new JProperty("instanceID", c.GetStableInstanceId()),
                            new JProperty("error", ex.Message)
                        ));
                    }
                }
                // CodelyLogger.Log($"[GetComponentsFromTarget] Finished REVERSE for loop.");
                
                // Cleanup the list we created
                componentsToIterate.Clear();
                componentsToIterate = null;

                return Response.Success(
                    $"Retrieved {componentData.Count} components from '{targetGo.name}'.",
                    componentData // List was built in original order
                );
            }
            catch (Exception e)
            {
                return Response.Error(
                    $"Error getting components from '{targetGo.name}': {e.Message}"
                );
            }
        }

        private static object RemoveComponentFromTarget(
            JObject @params,
            JToken targetToken,
            string searchMethod
        )
        {
            GameObject targetGo = FindObjectInternal(targetToken, searchMethod);
            if (targetGo == null)
            {
                return Response.Error(
                    $"Target GameObject ('{targetToken}') not found using method '{searchMethod ?? "default"}'."
                );
            }

            string typeName = null;
            // Allow removing component specified directly or via componentsToRemove array (take first)
            if (@params["componentName"] != null)
            {
                typeName = @params["componentName"]?.ToString();
            }
            else if (
                @params["componentsToRemove"] is JArray componentsToRemoveArray
                && componentsToRemoveArray.Count > 0
            )
            {
                typeName = componentsToRemoveArray.First?.ToString();
            }

            if (string.IsNullOrEmpty(typeName))
            {
                return Response.Error(
                    "Component type name ('componentName' or first element in 'componentsToRemove') is required."
                );
            }

            var removeResult = RemoveComponentInternal(targetGo, typeName);
            if (removeResult != null)
                return removeResult; // Return error

            EditorUtility.SetDirty(targetGo);
             // Use the new serializer helper
            return Response.Success(
                $"Component '{typeName}' removed from '{targetGo.name}'.",
                Helpers.GameObjectSerializer.GetGameObjectData(targetGo)
            );
        }

        private static object SetComponentPropertyOnTarget(
            JObject @params,
            JToken targetToken,
            string searchMethod
        )
        {
            GameObject targetGo = FindObjectInternal(targetToken, searchMethod);
            if (targetGo == null)
            {
                return Response.Error(
                    $"Target GameObject ('{targetToken}') not found using method '{searchMethod ?? "default"}'."
                );
            }

            string compName = @params["componentName"]?.ToString();
            JObject propertiesToSet = null;

            if (!string.IsNullOrEmpty(compName))
            {
                // Properties might be directly under componentProperties or nested under the component name
                if (@params["componentProperties"] is JObject compProps)
                {
                    propertiesToSet = compProps[compName] as JObject ?? compProps; // Allow flat or nested structure
                }
                // Support simplified propertyName + propertyValue format for compatibility
                else if (@params["propertyName"] != null && @params["propertyValue"] != null)
                {
                    string propName = @params["propertyName"].ToString();
                    JToken propValue = @params["propertyValue"];
                    propertiesToSet = new JObject { [propName] = propValue };
                }
            }
            else
            {
                return Response.Error("'componentName' parameter is required.");
            }

            if (propertiesToSet == null || !propertiesToSet.HasValues)
            {
                return Response.Error(
                    "'componentProperties' dictionary or 'propertyName'/'propertyValue' pair is required."
                );
            }

            var setResult = SetComponentPropertiesInternal(targetGo, compName, propertiesToSet);
            if (setResult != null)
                return setResult; // Return error

            EditorUtility.SetDirty(targetGo);
             // Use the new serializer helper
            return Response.Success(
                $"Properties set for component '{compName}' on '{targetGo.name}'.",
                Helpers.GameObjectSerializer.GetGameObjectData(targetGo)
            );
        }

        // --- Internal Helpers ---

        /// <summary>
        /// Parses a JArray like [x, y, z] into a Vector3.
        /// </summary>
        private static Vector3? ParseVector3(JArray array)
        {
            if (array != null && array.Count == 3)
            {
                try
                {
                    return new Vector3(
                        array[0].ToObject<float>(),
                        array[1].ToObject<float>(),
                        array[2].ToObject<float>()
                    );
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"Failed to parse JArray as Vector3: {array}. Error: {ex.Message}");
                }
            }
            return null;
        }

        /// <summary>
        /// Finds a single GameObject based on token (ID, name, path) and search method.
        /// </summary>
        private static GameObject FindObjectInternal(
            JToken targetToken,
            string searchMethod,
            JObject findParams = null
        )
        {
            // If find_all is not explicitly false, we still want only one for most single-target operations.
            bool findAll = findParams?["findAll"]?.ToObject<bool>() ?? false;
            // If a specific target ID is given, always find just that one.
            if (
                targetToken?.Type == JTokenType.Integer
                || (searchMethod == "by_id" && TryParseStableInstanceId(targetToken, out _))
            )
            {
                findAll = false;
            }
            List<GameObject> results = FindObjectsInternal(
                targetToken,
                searchMethod,
                findAll,
                findParams
            );
            return results.Count > 0 ? results[0] : null;
        }

        /// <summary>
        /// Core logic for finding GameObjects based on various criteria.
        /// </summary>
        private static List<GameObject> FindObjectsInternal(
            JToken targetToken,
            string searchMethod,
            bool findAll,
            JObject findParams = null
        )
        {
            List<GameObject> results = new List<GameObject>();
            string searchTerm = findParams?["searchTerm"]?.ToString() ?? targetToken?.ToString(); // Use searchTerm if provided, else the target itself
            bool searchInChildren = findParams?["searchInChildren"]?.ToObject<bool>() ?? false;
            bool searchInactive = findParams?["searchInactive"]?.ToObject<bool>() ?? true;
            bool hasExplicitSearchTerm =
                findParams != null
                && findParams["searchTerm"] != null
                && findParams["searchTerm"].Type != JTokenType.Null;

            // Default search method if not specified. Treat the literal "default" the same as
            // null/empty so callers that forward a placeholder don't fall into the switch's
            // default case (which would silently match nothing).
            if (string.IsNullOrEmpty(searchMethod) || searchMethod == "default")
            {
                // Recognize an integer-typed token OR a string that parses as an integer as
                // an instance ID. This matters when callers forward `target` as a string
                // (e.g. get_components passes `targetToken.ToString()`), which erases the
                // original JTokenType.Integer.
                if (targetToken?.Type == JTokenType.Integer
                    || (!string.IsNullOrEmpty(searchTerm) && long.TryParse(searchTerm, out _)))
                    searchMethod = "by_id";
                else if (!string.IsNullOrEmpty(searchTerm) && searchTerm.Contains("/"))
                    searchMethod = "by_path";
                else
                    searchMethod = "by_name"; // Default fallback
            }

            GameObject rootSearchObject = null;
            // If searching in children, find the initial target first
            if (searchInChildren && targetToken != null)
            {
                rootSearchObject = FindObjectInternal(targetToken, "by_id_or_name_or_path"); // Find the root for child search
                if (rootSearchObject == null)
                {
                    CodelyLogger.LogWarning(
                        $"[ManageGameObject.Find] Root object '{targetToken}' for child search not found."
                    );
                    return results; // Return empty if root not found
                }
            }

            switch (searchMethod)
            {
                case "by_id":
                    if (TryParseStableInstanceId(searchTerm, out long instanceId))
                    {
                        var allObjects = GetAllSceneObjects(true); // Fetch all, filter by active state below
                        GameObject obj = allObjects.FirstOrDefault(go =>
                            go != null && go.GetStableInstanceId() == instanceId
                        );
                        if (obj != null && (searchInactive || obj.activeInHierarchy))
                            results.Add(obj);
                    }
                    break;
                case "by_name":
                    var searchPoolName = rootSearchObject
                        ? rootSearchObject
                            .GetComponentsInChildren<Transform>(true)
                            .Select(t => t.gameObject)
                        : GetAllSceneObjects(true);
                    if (string.IsNullOrEmpty(searchTerm))
                    {
                        break;
                    }

                    // When searchTerm is explicitly provided, treat it as a fuzzy "contains" filter (case-insensitive).
                    // When only target is provided (no explicit searchTerm), keep exact name matching semantics.
                    if (hasExplicitSearchTerm)
                    {
                        results.AddRange(
                            searchPoolName.Where(go =>
                                go != null
                                && !string.IsNullOrEmpty(go.name)
                                && go.name.IndexOf(
                                    searchTerm,
                                    StringComparison.OrdinalIgnoreCase
                                ) >= 0
                                && (searchInactive || go.activeInHierarchy)
                            )
                        );
                    }
                    else
                    {
                        results.AddRange(
                            searchPoolName.Where(go =>
                                go != null
                                && string.Equals(
                                    go.name,
                                    searchTerm,
                                    StringComparison.OrdinalIgnoreCase
                                )
                                && (searchInactive || go.activeInHierarchy)
                            )
                        );
                    }
                    break;
                case "by_path":
                    GameObject foundByPath = FindByHierarchyPath(
                        searchTerm,
                        rootSearchObject,
                        searchInactive);
                    if (foundByPath != null)
                        results.Add(foundByPath);
                    break;
                case "by_tag":
                    var searchPoolTag = rootSearchObject
                        ? rootSearchObject
                            .GetComponentsInChildren<Transform>(true)
                            .Select(t => t.gameObject)
                        : GetAllSceneObjects(true);
                    results.AddRange(
                        searchPoolTag.Where(go =>
                            go != null
                            && go.CompareTag(searchTerm)
                            && (searchInactive || go.activeInHierarchy)
                        )
                    );
                    break;
                case "by_layer":
                    var searchPoolLayer = rootSearchObject
                        ? rootSearchObject
                            .GetComponentsInChildren<Transform>(true)
                            .Select(t => t.gameObject)
                        : GetAllSceneObjects(true);
                    if (int.TryParse(searchTerm, out int layerIndex))
                    {
                        results.AddRange(
                            searchPoolLayer.Where(go =>
                                go != null
                                && go.layer == layerIndex
                                && (searchInactive || go.activeInHierarchy)
                            )
                        );
                    }
                    else
                    {
                        int namedLayer = LayerMask.NameToLayer(searchTerm);
                        if (namedLayer != -1)
                            results.AddRange(
                                searchPoolLayer.Where(go =>
                                    go != null
                                    && go.layer == namedLayer
                                    && (searchInactive || go.activeInHierarchy)
                                )
                            );
                    }
                    break;
                case "by_component":
                    Type componentType = FindType(searchTerm);
                    if (componentType != null)
                    {
                        IEnumerable<GameObject> searchPoolComp;
                        if (rootSearchObject)
                        {
                            searchPoolComp = rootSearchObject
                                .GetComponentsInChildren(componentType, searchInactive)
                                .Select(c => (c as Component).gameObject);
                        }
                        else
                        {
#if UNITY_6000_5_OR_NEWER
                            FindObjectsInactive findInactive = searchInactive
                                ? FindObjectsInactive.Include
                                : FindObjectsInactive.Exclude;
                            searchPoolComp = UnityEngine
                                .Object.FindObjectsByType(componentType, findInactive)
                                .Select(c => (c as Component).gameObject);
#elif UNITY_2022_2_OR_NEWER
                            // Use FindObjectsByType for Unity 2022.2+ (including 6000.0–6000.4)
                            FindObjectsInactive findInactive = searchInactive
                                ? FindObjectsInactive.Include
                                : FindObjectsInactive.Exclude;
                            searchPoolComp = UnityEngine
                                .Object.FindObjectsByType(
                                    componentType,
                                    findInactive,
                                    FindObjectsSortMode.None
                                )
                                .Select(c => (c as Component).gameObject);
#elif UNITY_2020_3_OR_NEWER
                            // FindObjectsOfType(Type, bool) was added in Unity 2020.3
                            searchPoolComp = UnityEngine
                                .Object.FindObjectsOfType(componentType, searchInactive)
                                .Select(c => (c as Component).gameObject);
#else
                            // Unity 2019 / early-2020: only FindObjectsOfType(Type) exists; it
                            // already excludes inactive objects, so the searchInactive=false case
                            // matches its semantics. When inactive objects are requested, fall
                            // back to scanning loaded scenes' root objects via Resources.
                            if (searchInactive)
                            {
                                searchPoolComp = Resources.FindObjectsOfTypeAll(componentType)
                                    .Where(c => c != null)
                                    .Select(c => (c as Component))
                                    .Where(c => c != null && c.gameObject != null
                                        && c.gameObject.hideFlags == HideFlags.None
                                        && !UnityEditor.EditorUtility.IsPersistent(c.gameObject))
                                    .Select(c => c.gameObject);
                            }
                            else
                            {
                                searchPoolComp = UnityEngine
                                    .Object.FindObjectsOfType(componentType)
                                    .Select(c => (c as Component).gameObject);
                            }
#endif
                        }
                        results.AddRange(
                            searchPoolComp.Where(go =>
                                go != null
                                && (searchInactive || go.activeInHierarchy)
                            )
                        ); // Ensure GO is valid and respects active filter
                    }
                    else
                    {
                        CodelyLogger.LogWarning(
                            $"[ManageGameObject.Find] Component type not found: {searchTerm}"
                        );
                    }
                    break;
                case "by_id_or_name_or_path": // Helper method used internally
                    if (TryParseStableInstanceId(searchTerm, out long id))
                    {
                        var allObjectsId = GetAllSceneObjects(true); // Internal helper always sees all, filtered below
                        GameObject objById = allObjectsId.FirstOrDefault(go =>
                            go != null && go.GetStableInstanceId() == id
                        );
                        if (objById != null && (searchInactive || objById.activeInHierarchy))
                        {
                            results.Add(objById);
                            break;
                        }
                    }
                    GameObject objByPath = FindByHierarchyPath(searchTerm, null, searchInactive);
                    if (objByPath != null)
                    {
                        results.Add(objByPath);
                        break;
                    }

                    var allObjectsName = GetAllSceneObjects(true);
                    results.AddRange(
                        allObjectsName.Where(go =>
                            go != null
                            && string.Equals(
                                go.name,
                                searchTerm,
                                StringComparison.OrdinalIgnoreCase
                            )
                            && (searchInactive || go.activeInHierarchy)
                        )
                    );
                    break;
                default:
                    CodelyLogger.LogWarning(
                        $"[ManageGameObject.Find] Unknown search method: {searchMethod}"
                    );
                    break;
            }

            // If only one result is needed, return just the first one found.
            if (!findAll && results.Count > 1)
            {
                return new List<GameObject> { results[0] };
            }

            return results.Distinct().ToList(); // Ensure uniqueness
        }

        // Helper to get all scene objects, including Prefab Stage preview objects.
        private static IEnumerable<GameObject> GetAllSceneObjects(bool includeInactive)
        {
            var allObjects = new List<GameObject>();
            CollectSceneObjects(SceneManager.GetActiveScene(), includeInactive, allObjects);
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.scene.IsValid()
                && stage.scene != SceneManager.GetActiveScene())
            {
                CollectSceneObjects(stage.scene, includeInactive, allObjects);
            }
            return allObjects;
        }

        private static void CollectSceneObjects(
            Scene scene,
            bool includeInactive,
            List<GameObject> allObjects)
        {
            if (!scene.IsValid()) return;
            var rootObjects = scene.GetRootGameObjects();
            foreach (var root in rootObjects)
            {
                if (root == null) continue;
                allObjects.AddRange(
                    root.GetComponentsInChildren<Transform>(includeInactive)
                        .Select(t => t.gameObject)
                );
            }
        }

        private static IEnumerable<GameObject> GetSearchRoots()
        {
            var roots = new List<GameObject>();
            if (SceneManager.GetActiveScene().IsValid())
                roots.AddRange(SceneManager.GetActiveScene().GetRootGameObjects());
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.scene.IsValid()
                && stage.scene != SceneManager.GetActiveScene())
            {
                roots.AddRange(stage.scene.GetRootGameObjects());
            }
            return roots.Where(go => go != null);
        }

        /// <summary>
        /// Resolves a hierarchy path that may contain same-name ordinals
        /// (e.g. Root/Cube[1]/Child). Does not use GameObject.Find, so inactive
        /// objects and Prefab Stage contents are included.
        /// </summary>
        private static GameObject FindByHierarchyPath(
            string path,
            GameObject rootSearchObject,
            bool searchInactive)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var segments = path.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return null;

            IEnumerable<Transform> start;
            if (rootSearchObject != null)
            {
                start = new[] { rootSearchObject.transform };
            }
            else
            {
                start = GetSearchRoots().Select(go => go.transform);
            }

            IEnumerable<Transform> current = start;
            for (int s = 0; s < segments.Length; s++)
            {
                string segment = segments[s];
                string name;
                int ordinal;
                if (!InstanceIdExtensions.TryParseHierarchySegment(segment, out name, out ordinal))
                    return null;

                bool firstSegmentFromRoots = s == 0 && rootSearchObject == null;
                var matches = new List<Transform>();
                if (firstSegmentFromRoots)
                {
                    foreach (var root in current)
                    {
                        if (root != null && root.name == name)
                            matches.Add(root);
                    }
                }
                else
                {
                    foreach (var parent in current)
                    {
                        if (parent == null) continue;
                        for (int i = 0; i < parent.childCount; i++)
                        {
                            var child = parent.GetChild(i);
                            if (child.name == name)
                                matches.Add(child);
                        }
                    }
                }

                Transform picked = null;
                if (ordinal >= 0)
                {
                    if (ordinal < matches.Count)
                        picked = matches[ordinal];
                }
                else if (matches.Count == 1)
                {
                    picked = matches[0];
                }
                else if (matches.Count > 1)
                {
                    // No suffix: generated paths only omit [N] when the name is unique.
                    // Fall back to the first match for older paths.
                    picked = matches[0];
                }

                if (picked == null) return null;
                if (!searchInactive && !picked.gameObject.activeInHierarchy) return null;
                current = new[] { picked };
            }

            var found = current.FirstOrDefault();
            return found != null ? found.gameObject : null;
        }

        /// <summary>
        /// Adds a component by type name and optionally sets properties.
        /// Returns null on success, or an error response object on failure.
        /// </summary>
        private static object AddComponentInternal(
            GameObject targetGo,
            string typeName,
            JObject properties
        )
        {
            Type componentType = FindType(typeName);
            if (componentType == null)
            {
                return Response.Error(
                    $"Component type '{typeName}' not found or is not a valid Component."
                );
            }
            if (!typeof(Component).IsAssignableFrom(componentType))
            {
                return Response.Error($"Type '{typeName}' is not a Component.");
            }

            // Prevent adding Transform again
            if (componentType == typeof(Transform))
            {
                return Response.Error("Cannot add another Transform component.");
            }

            // Check for 2D/3D physics component conflicts
            bool isAdding2DPhysics =
                typeof(Rigidbody2D).IsAssignableFrom(componentType)
                || typeof(Collider2D).IsAssignableFrom(componentType);
            bool isAdding3DPhysics =
                typeof(Rigidbody).IsAssignableFrom(componentType)
                || typeof(Collider).IsAssignableFrom(componentType);

            if (isAdding2DPhysics)
            {
                // Check if the GameObject already has any 3D Rigidbody or Collider
                if (
                    targetGo.GetComponent<Rigidbody>() != null
                    || targetGo.GetComponent<Collider>() != null
                )
                {
                    return Response.Error(
                        $"Cannot add 2D physics component '{typeName}' because the GameObject '{targetGo.name}' already has a 3D Rigidbody or Collider."
                    );
                }
            }
            else if (isAdding3DPhysics)
            {
                // Check if the GameObject already has any 2D Rigidbody or Collider
                if (
                    targetGo.GetComponent<Rigidbody2D>() != null
                    || targetGo.GetComponent<Collider2D>() != null
                )
                {
                    return Response.Error(
                        $"Cannot add 3D physics component '{typeName}' because the GameObject '{targetGo.name}' already has a 2D Rigidbody or Collider."
                    );
                }
            }

            try
            {
                // Use Undo.AddComponent for undo support
                Component newComponent = Undo.AddComponent(targetGo, componentType);
                if (newComponent == null)
                {
                    return Response.Error(
                        $"Failed to add component '{typeName}' to '{targetGo.name}'. It might be disallowed (e.g., adding script twice)."
                    );
                }

                // Set default values for specific component types
                if (newComponent is Light light)
                {
                    // Default newly added lights to directional
                    light.type = LightType.Directional;
                }

                // Set properties if provided
                if (properties != null)
                {
                    var setResult = SetComponentPropertiesInternal(
                        targetGo,
                        typeName,
                        properties,
                        newComponent
                    ); // Pass the new component instance
                    if (setResult != null)
                    {
                        // If setting properties failed, maybe remove the added component?
                        Undo.DestroyObjectImmediate(newComponent);
                        return setResult; // Return the error from setting properties
                    }
                }

                return null; // Success
            }
            catch (Exception e)
            {
                return Response.Error(
                    $"Error adding component '{typeName}' to '{targetGo.name}': {e.Message}"
                );
            }
        }

        /// <summary>
        /// Removes a component by type name.
        /// Returns null on success, or an error response object on failure.
        /// </summary>
        private static object RemoveComponentInternal(GameObject targetGo, string typeName)
        {
            Type componentType = FindType(typeName);
            if (componentType == null)
            {
                return Response.Error($"Component type '{typeName}' not found for removal.");
            }

            // Prevent removing essential components
            if (componentType == typeof(Transform))
            {
                return Response.Error("Cannot remove the Transform component.");
            }

            Component componentToRemove = targetGo.GetComponent(componentType);
            if (componentToRemove == null)
            {
                return Response.Error(
                    $"Component '{typeName}' not found on '{targetGo.name}' to remove."
                );
            }

            try
            {
                // Handle known dependency chains before removing the primary component.
                // Example: In URP, UniversalAdditionalLightData depends on Light, so we should
                // remove the additional data component first to allow Light removal.
                TryRemoveDependentComponents(targetGo, componentType);

                // Use Undo.DestroyObjectImmediate for undo support
                Undo.DestroyObjectImmediate(componentToRemove);
                return null; // Success
            }
            catch (Exception e)
            {
                return Response.Error(
                    $"Error removing component '{typeName}' from '{targetGo.name}': {e.Message}"
                );
            }
        }

        /// <summary>
        /// Removes known dependent components that would otherwise block removal of the primary component.
        /// This keeps behavior robust across render pipelines (e.g., URP additional light data).
        /// </summary>
        private static void TryRemoveDependentComponents(GameObject targetGo, Type primaryComponentType)
        {
            try
            {
                // Special-case Light in URP: UniversalAdditionalLightData depends on Light.
                if (primaryComponentType == typeof(Light))
                {
                    // Try to resolve the URP additional light data type without hard assembly references.
                    Type urpAdditionalLightType = FindType("UnityEngine.Rendering.Universal.UniversalAdditionalLightData");
                    if (urpAdditionalLightType != null)
                    {
                        Component extra = targetGo.GetComponent(urpAdditionalLightType);
                        if (extra != null)
                        {
                            Undo.DestroyObjectImmediate(extra);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning(
                    $"[ManageGameObject] Failed to remove dependent components for '{primaryComponentType.FullName}' on '{targetGo.name}': {ex.Message}"
                );
            }
        }

        /// <summary>
        /// Sets properties on a component.
        /// Returns null on success, or an error response object on failure.
        /// </summary>
        private static object SetComponentPropertiesInternal(
            GameObject targetGo,
            string compName,
            JObject propertiesToSet,
            Component targetComponentInstance = null
        )
        {
            Component targetComponent = targetComponentInstance;
            if (targetComponent == null)
            {
                if (ComponentResolver.TryResolve(compName, out var compType, out var compError))
                {
                    targetComponent = targetGo.GetComponent(compType);
                }
                else
                {
                    targetComponent = targetGo.GetComponent(compName); // fallback to string-based lookup
                }
            }
            if (targetComponent == null)
            {
                return Response.Error(
                    $"Component '{compName}' not found on '{targetGo.name}' to set properties."
                );
            }

            Undo.RecordObject(targetComponent, "Set Component Properties");

            var failures = new List<object>();
            foreach (var prop in propertiesToSet.Properties())
            {
                string propName = prop.Name;
                JToken propValue = prop.Value;

                try
                {
                    string failureCode;
                    string failureMessage;
                    bool setResult = SetProperty(targetComponent, propName, propValue, out failureCode, out failureMessage);
                    if (!setResult)
                    {
                        string msg;
                        if (failureCode == "conversion_failed" || failureCode == "exception")
                        {
                            msg = failureMessage ?? $"Conversion failed for '{propName}'.";
                        }
                        else
                        {
                            var availableProperties = ComponentResolver.GetAllComponentProperties(targetComponent.GetType());
                            var suggestions = ComponentResolver.GetAIPropertySuggestions(propName, availableProperties);
                            msg = suggestions.Any()
                                ? $"Property '{propName}' not found. Did you mean: {string.Join(", ", suggestions)}? Available: [{string.Join(", ", availableProperties)}]"
                                : $"Property '{propName}' not found. Available: [{string.Join(", ", availableProperties)}]";
                        }
                        CodelyLogger.LogWarning($"[ManageGameObject] {msg}");
                        failures.Add(new { property = propName, code = failureCode ?? "member_not_found", message = msg });
                    }
                }
                catch (Exception e)
                {
                    CodelyLogger.LogError(
                        $"[ManageGameObject] Error setting property '{propName}' on '{compName}': {e.Message}"
                    );
                    failures.Add(new { property = propName, code = "exception", message = $"Error setting '{propName}': {e.Message}" });
                }
            }
            EditorUtility.SetDirty(targetComponent);
            return failures.Count == 0
                ? null
                : new
                {
                    success = false,
                    code = "set_component_property_failed",
                    message = $"One or more properties failed on '{compName}'.",
                    data = new { errors = failures }
                };
        }

        /// <summary>
        /// Helper to set a property or field via reflection, handling basic types.
        /// </summary>
        private static bool SetProperty(object target, string memberName, JToken value, out string failureCode, out string failureMessage)
        {
            failureCode = null;
            failureMessage = null;
            Type type = target.GetType();
            BindingFlags flags =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

             // Use shared serializer to avoid per-call allocation
             var inputSerializer = InputSerializer;

            try
            {
                // Handle special case for materials with dot notation (material.property)
                // Examples: material.color, sharedMaterial.color, materials[0].color
                if (memberName.Contains(".") || memberName.Contains("["))
                {
                    // Pass the inputSerializer down for nested conversions
                    bool ok = SetNestedProperty(target, memberName, value, inputSerializer);
                    if (!ok)
                    {
                        failureCode = "member_not_found";
                    }
                    return ok;
                }

                PropertyInfo propInfo = type.GetProperty(memberName, flags);
                if (propInfo != null && propInfo.CanWrite)
                {
                    object convertedValue = null;
                    try
                    {
                        // Use the inputSerializer for conversion
                        convertedValue = ConvertJTokenToType(value, propInfo.PropertyType, inputSerializer);
                    }
                    catch (Exception ex)
                    {
                        failureCode = "conversion_failed";
                        failureMessage = $"Conversion failed for property '{memberName}' (Type: {propInfo.PropertyType.FullName}): {ex.Message}";
                        return false;
                    }
                    if (convertedValue != null || value.Type == JTokenType.Null) // Allow setting null
                    {
                        propInfo.SetValue(target, convertedValue);
                        return true;
                    }
                    failureCode = "conversion_failed";
                    failureMessage = $"Conversion failed for property '{memberName}' (Type: {propInfo.PropertyType.FullName}) from token: {value.ToString(Formatting.None)}";
                    return false;
                }
                else
                {
                    FieldInfo fieldInfo = type.GetField(memberName, flags);
                    if (fieldInfo != null) // Check if !IsLiteral?
                    {
                        object convertedValue = null;
                        try
                        {
                             // Use the inputSerializer for conversion
                            convertedValue = ConvertJTokenToType(value, fieldInfo.FieldType, inputSerializer);
                        }
                        catch (Exception ex)
                        {
                            failureCode = "conversion_failed";
                            failureMessage = $"Conversion failed for field '{memberName}' (Type: {fieldInfo.FieldType.FullName}): {ex.Message}";
                            return false;
                        }
                         if (convertedValue != null || value.Type == JTokenType.Null) // Allow setting null
                        {
                            fieldInfo.SetValue(target, convertedValue);
                            return true;
                        }
                        failureCode = "conversion_failed";
                        failureMessage = $"Conversion failed for field '{memberName}' (Type: {fieldInfo.FieldType.FullName}) from token: {value.ToString(Formatting.None)}";
                        return false;
                    }
                    else
                    {
                        // Try NonPublic [SerializeField] fields
                        var npField = type.GetField(memberName, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (npField != null && npField.GetCustomAttribute<SerializeField>() != null)
                        {
                            object convertedValue = null;
                            try
                            {
                                convertedValue = ConvertJTokenToType(value, npField.FieldType, inputSerializer);
                            }
                            catch (Exception ex)
                            {
                                failureCode = "conversion_failed";
                                failureMessage = $"Conversion failed for field '{memberName}' (Type: {npField.FieldType.FullName}): {ex.Message}";
                                return false;
                            }
                            if (convertedValue != null || value.Type == JTokenType.Null)
                            {
                                npField.SetValue(target, convertedValue);
                                return true;
                            }
                            failureCode = "conversion_failed";
                            failureMessage = $"Conversion failed for field '{memberName}' (Type: {npField.FieldType.FullName}) from token: {value.ToString(Formatting.None)}";
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                failureCode = "exception";
                failureMessage = $"Failed to set '{memberName}' on {type.Name}: {ex.Message}\nToken: {value.ToString(Formatting.None)}";
            }
            if (failureCode == null)
            {
                failureCode = "member_not_found";
            }
            return false;
        }

        /// <summary>
        /// Sets a nested property using dot notation (e.g., "material.color") or array access (e.g., "materials[0]")
        /// </summary>
        // Pass the input serializer for conversions
        //Using the serializer helper
        private static bool SetNestedProperty(object target, string path, JToken value, JsonSerializer inputSerializer)
        {
            try
            {
                // Split the path into parts (handling both dot notation and array indexing)
                string[] pathParts = SplitPropertyPath(path);
                if (pathParts.Length == 0)
                    return false;

                object currentObject = target;
                Type currentType = currentObject.GetType();
                BindingFlags flags =
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

                // Traverse the path until we reach the final property
                for (int i = 0; i < pathParts.Length - 1; i++)
                {
                    string part = pathParts[i];
                    bool isArray = false;
                    int arrayIndex = -1;

                    // Check if this part contains array indexing
                    if (part.Contains("["))
                    {
                        int startBracket = part.IndexOf('[');
                        int endBracket = part.IndexOf(']');
                        if (startBracket > 0 && endBracket > startBracket)
                        {
                            string indexStr = part.Substring(
                                startBracket + 1,
                                endBracket - startBracket - 1
                            );
                            if (int.TryParse(indexStr, out arrayIndex))
                            {
                                isArray = true;
                                part = part.Substring(0, startBracket);
                            }
                        }
                    }
                    // Get the property/field
                    PropertyInfo propInfo = currentType.GetProperty(part, flags);
                    FieldInfo fieldInfo = null;
                    if (propInfo == null)
                    {
                        fieldInfo = currentType.GetField(part, flags);
                        if (fieldInfo == null)
                        {
                            CodelyLogger.LogWarning(
                                $"[SetNestedProperty] Could not find property or field '{part}' on type '{currentType.Name}'"
                            );
                            return false;
                        }
                    }

                    // Get the value
                    currentObject =
                        propInfo != null
                            ? propInfo.GetValue(currentObject)
                            : fieldInfo.GetValue(currentObject);
                    //Need to stop if current property is null
                    if (currentObject == null)
                    {
                        CodelyLogger.LogWarning(
                            $"[SetNestedProperty] Property '{part}' is null, cannot access nested properties."
                        );
                        return false;
                    }
                    // If this part was an array or list, access the specific index
                    if (isArray)
                    {
                        if (currentObject is Material[])
                        {
                            var materials = currentObject as Material[];
                            if (arrayIndex < 0 || arrayIndex >= materials.Length)
                            {
                                CodelyLogger.LogWarning(
                                    $"[SetNestedProperty] Material index {arrayIndex} out of range (0-{materials.Length - 1})"
                                );
                                return false;
                            }
                            currentObject = materials[arrayIndex];
                        }
                        else if (currentObject is System.Collections.IList)
                        {
                            var list = currentObject as System.Collections.IList;
                            if (arrayIndex < 0 || arrayIndex >= list.Count)
                            {
                                CodelyLogger.LogWarning(
                                    $"[SetNestedProperty] Index {arrayIndex} out of range (0-{list.Count - 1})"
                                );
                                return false;
                            }
                            currentObject = list[arrayIndex];
                        }
                        else
                        {
                            CodelyLogger.LogWarning(
                                $"[SetNestedProperty] Property '{part}' is not an array or list, cannot access by index."
                            );
                            return false;
                        }
                    }
                    currentType = currentObject.GetType();
                }

                // Set the final property
                string finalPart = pathParts[pathParts.Length - 1];

                // Special handling for Material properties (shader properties)
                if (currentObject is Material material && finalPart.StartsWith("_"))
                {
                    // Use the serializer to convert the JToken value first
                    if (value is JArray jArray)
                    {
                        // Try converting to known types that SetColor/SetVector accept
                        if (jArray.Count == 4) {
                            try { Color color = value.ToObject<Color>(inputSerializer); material.SetColor(finalPart, color); return true; } catch { }
                            try { Vector4 vec = value.ToObject<Vector4>(inputSerializer); material.SetVector(finalPart, vec); return true; } catch { }
                        } else if (jArray.Count == 3) {
                            try { Color color = value.ToObject<Color>(inputSerializer); material.SetColor(finalPart, color); return true; } catch { } // ToObject handles conversion to Color
                        } else if (jArray.Count == 2) {
                            try { Vector2 vec = value.ToObject<Vector2>(inputSerializer); material.SetVector(finalPart, vec); return true; } catch { }
                        }
                    }
                    else if (value.Type == JTokenType.Float || value.Type == JTokenType.Integer)
                    {
                        try { material.SetFloat(finalPart, value.ToObject<float>(inputSerializer)); return true; } catch { }
                    }
                    else if (value.Type == JTokenType.Boolean)
                    {
                        try { material.SetFloat(finalPart, value.ToObject<bool>(inputSerializer) ? 1f : 0f); return true; } catch { }
                    }
                    else if (value.Type == JTokenType.String)
                    {
                        // Try converting to Texture using the serializer/converter
                        try {
                            Texture texture = value.ToObject<Texture>(inputSerializer);
                            if (texture != null) {
                                material.SetTexture(finalPart, texture);
                                return true;
                            }
                        } catch { }
                    }

                    CodelyLogger.LogWarning(
                        $"[SetNestedProperty] Unsupported or failed conversion for material property '{finalPart}' from value: {value.ToString(Formatting.None)}"
                    );
                    return false;
                }

                // For standard properties (not shader specific)
                PropertyInfo finalPropInfo = currentType.GetProperty(finalPart, flags);
                if (finalPropInfo != null && finalPropInfo.CanWrite)
                {
                    // Use the inputSerializer for conversion
                    object convertedValue = ConvertJTokenToType(value, finalPropInfo.PropertyType, inputSerializer);
                    if (convertedValue != null || value.Type == JTokenType.Null)
                    {
                        finalPropInfo.SetValue(currentObject, convertedValue);
                        return true;
                    }
                    else {
                        CodelyLogger.LogWarning($"[SetNestedProperty] Final conversion failed for property '{finalPart}' (Type: {finalPropInfo.PropertyType.Name}) from token: {value.ToString(Formatting.None)}");
                    }
                }
                else
                {
                    FieldInfo finalFieldInfo = currentType.GetField(finalPart, flags);
                    if (finalFieldInfo != null)
                    {
                        // Use the inputSerializer for conversion
                        object convertedValue = ConvertJTokenToType(value, finalFieldInfo.FieldType, inputSerializer);
                        if (convertedValue != null || value.Type == JTokenType.Null)
                        {
                            finalFieldInfo.SetValue(currentObject, convertedValue);
                            return true;
                        }
                        else {
                            CodelyLogger.LogWarning($"[SetNestedProperty] Final conversion failed for field '{finalPart}' (Type: {finalFieldInfo.FieldType.Name}) from token: {value.ToString(Formatting.None)}");
                        }
                    }
                    else
                    {
                        CodelyLogger.LogWarning(
                            $"[SetNestedProperty] Could not find final writable property or field '{finalPart}' on type '{currentType.Name}'"
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError(
                    $"[SetNestedProperty] Error setting nested property '{path}': {ex.Message}\nToken: {value.ToString(Formatting.None)}"
                );
            }

            return false;
        }


        /// <summary>
        /// Split a property path into parts, handling both dot notation and array indexers
        /// </summary>
        private static string[] SplitPropertyPath(string path)
        {
            // Handle complex paths with both dots and array indexers
            List<string> parts = new List<string>();
            int startIndex = 0;
            bool inBrackets = false;

            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];

                if (c == '[')
                {
                    inBrackets = true;
                }
                else if (c == ']')
                {
                    inBrackets = false;
                }
                else if (c == '.' && !inBrackets)
                {
                    // Found a dot separator outside of brackets
                    parts.Add(path.Substring(startIndex, i - startIndex));
                    startIndex = i + 1;
                }
            }
            if (startIndex < path.Length)
            {
                parts.Add(path.Substring(startIndex));
            }
            return parts.ToArray();
        }

        /// <summary>
        /// Simple JToken to Type conversion for common Unity types, using JsonSerializer.
        /// </summary>
         // Pass the input serializer
        private static object ConvertJTokenToType(JToken token, Type targetType, JsonSerializer inputSerializer)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                {
                    CodelyLogger.LogWarning($"Cannot assign null to non-nullable value type {targetType.Name}. Returning default value.");
                    return Activator.CreateInstance(targetType);
                }
                return null;
            }

            // Fast-path for common Unity structs where we want to gracefully accept both
            // object-style {x,y,z} and array-style [x,y,z] representations.
            try
            {
                if (targetType == typeof(Vector3))
                {
                    return ParseJTokenToVector3(token);
                }
                if (targetType == typeof(Vector2))
                {
                    return ParseJTokenToVector2(token);
                }
                if (targetType == typeof(Quaternion))
                {
                    return ParseJTokenToQuaternion(token);
                }
                if (targetType == typeof(Color))
                {
                    return ParseJTokenToColor(token);
                }
                if (targetType == typeof(Rect))
                {
                    return ParseJTokenToRect(token);
                }
                if (targetType == typeof(Bounds))
                {
                    return ParseJTokenToBounds(token);
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning(
                    $"[ConvertJTokenToType] Fallback parse for {targetType.FullName} failed: {ex.Message}\nToken: {token.ToString(Formatting.None)}"
                );
                // Fall through to serializer-based conversion below
            }

            try
            {
                // Use the provided serializer instance which includes our custom converters
                return token.ToObject(targetType, inputSerializer);
            }
            catch (JsonSerializationException jsonEx)
            {
                CodelyLogger.LogError(
                    $"JSON Deserialization Error converting token to {targetType.FullName}: {jsonEx.Message}\nToken: {token.ToString(Formatting.None)}"
                );
                // As a last resort for known vector types, try the fallback parsers once more to
                // avoid hard failures during property setting.
                try
                {
                    if (targetType == typeof(Vector3))
                        return ParseJTokenToVector3(token);
                    if (targetType == typeof(Vector2))
                        return ParseJTokenToVector2(token);
                    if (targetType == typeof(Quaternion))
                        return ParseJTokenToQuaternion(token);
                    if (targetType == typeof(Color))
                        return ParseJTokenToColor(token);
                    if (targetType == typeof(Rect))
                        return ParseJTokenToRect(token);
                    if (targetType == typeof(Bounds))
                        return ParseJTokenToBounds(token);
                }
                catch (Exception fallbackEx)
                {
                    CodelyLogger.LogError(
                        $"[ConvertJTokenToType] Secondary fallback for {targetType.FullName} also failed: {fallbackEx.Message}\nToken: {token.ToString(Formatting.None)}"
                    );
                }

                // If everything failed, rethrow so callers can surface a clear error.
                throw;
            }
            catch (ArgumentException argEx)
            {
                CodelyLogger.LogError(
                    $"Argument Error converting token to {targetType.FullName}: {argEx.Message}\nToken: {token.ToString(Formatting.None)}"
                );
                throw;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError(
                    $"Unexpected error converting token to {targetType.FullName}: {ex}\nToken: {token.ToString(Formatting.None)}"
                );
                throw;
            }
        }

        // --- ParseJTokenTo... helpers are likely redundant now with the serializer approach ---
        // Keep them temporarily for reference or if specific fallback logic is ever needed.

        private static Vector3 ParseJTokenToVector3(JToken token)
        {
            // ... (implementation - likely replaced by Vector3Converter) ...
            // Consider removing these if the serializer handles them reliably.
            if (token is JObject obj && obj.ContainsKey("x") && obj.ContainsKey("y") && obj.ContainsKey("z"))
            {
                return new Vector3(obj["x"].ToObject<float>(), obj["y"].ToObject<float>(), obj["z"].ToObject<float>());
            }
            if (token is JArray arr && arr.Count >= 3)
            {
                 return new Vector3(arr[0].ToObject<float>(), arr[1].ToObject<float>(), arr[2].ToObject<float>());
            }
            CodelyLogger.LogWarning($"Could not parse JToken '{token}' as Vector3 using fallback. Returning Vector3.zero.");
            return Vector3.zero;

        }
        private static Vector2 ParseJTokenToVector2(JToken token)
        {
            // ... (implementation - likely replaced by Vector2Converter) ...
             if (token is JObject obj && obj.ContainsKey("x") && obj.ContainsKey("y"))
            {
                return new Vector2(obj["x"].ToObject<float>(), obj["y"].ToObject<float>());
            }
            if (token is JArray arr && arr.Count >= 2)
            {
                 return new Vector2(arr[0].ToObject<float>(), arr[1].ToObject<float>());
            }
            CodelyLogger.LogWarning($"Could not parse JToken '{token}' as Vector2 using fallback. Returning Vector2.zero.");
            return Vector2.zero;
        }
        private static Quaternion ParseJTokenToQuaternion(JToken token)
        {
            // ... (implementation - likely replaced by QuaternionConverter) ...
            if (token is JObject obj && obj.ContainsKey("x") && obj.ContainsKey("y") && obj.ContainsKey("z") && obj.ContainsKey("w"))
            {
                return new Quaternion(obj["x"].ToObject<float>(), obj["y"].ToObject<float>(), obj["z"].ToObject<float>(), obj["w"].ToObject<float>());
            }
            if (token is JArray arr && arr.Count >= 4)
            {
                 return new Quaternion(arr[0].ToObject<float>(), arr[1].ToObject<float>(), arr[2].ToObject<float>(), arr[3].ToObject<float>());
            }
            CodelyLogger.LogWarning($"Could not parse JToken '{token}' as Quaternion using fallback. Returning Quaternion.identity.");
            return Quaternion.identity;
        }
        private static Color ParseJTokenToColor(JToken token)
        {
             // ... (implementation - likely replaced by ColorConverter) ...
            if (token is JObject obj && obj.ContainsKey("r") && obj.ContainsKey("g") && obj.ContainsKey("b") && obj.ContainsKey("a"))
            {
                return new Color(obj["r"].ToObject<float>(), obj["g"].ToObject<float>(), obj["b"].ToObject<float>(), obj["a"].ToObject<float>());
            }
            if (token is JArray arr && arr.Count >= 4)
            {
                 return new Color(arr[0].ToObject<float>(), arr[1].ToObject<float>(), arr[2].ToObject<float>(), arr[3].ToObject<float>());
            }
            CodelyLogger.LogWarning($"Could not parse JToken '{token}' as Color using fallback. Returning Color.white.");
            return Color.white;
        }
        private static Rect ParseJTokenToRect(JToken token)
        {
             // ... (implementation - likely replaced by RectConverter) ...
            if (token is JObject obj && obj.ContainsKey("x") && obj.ContainsKey("y") && obj.ContainsKey("width") && obj.ContainsKey("height"))
            {
                return new Rect(obj["x"].ToObject<float>(), obj["y"].ToObject<float>(), obj["width"].ToObject<float>(), obj["height"].ToObject<float>());
            }
            if (token is JArray arr && arr.Count >= 4)
            {
                 return new Rect(arr[0].ToObject<float>(), arr[1].ToObject<float>(), arr[2].ToObject<float>(), arr[3].ToObject<float>());
            }
            CodelyLogger.LogWarning($"Could not parse JToken '{token}' as Rect using fallback. Returning Rect.zero.");
            return Rect.zero;
        }
        private static Bounds ParseJTokenToBounds(JToken token)
        {
             // ... (implementation - likely replaced by BoundsConverter) ...
            if (token is JObject obj && obj.ContainsKey("center") && obj.ContainsKey("size"))
            {
                // Requires Vector3 conversion, which should ideally use the serializer too
                 Vector3 center = ParseJTokenToVector3(obj["center"]); // Or use obj["center"].ToObject<Vector3>(inputSerializer)
                 Vector3 size = ParseJTokenToVector3(obj["size"]);     // Or use obj["size"].ToObject<Vector3>(inputSerializer)
                return new Bounds(center, size);
            }
            // Array fallback for Bounds is less intuitive, maybe remove?
            // if (token is JArray arr && arr.Count >= 6)
            // {
            //      return new Bounds(new Vector3(arr[0].ToObject<float>(), arr[1].ToObject<float>(), arr[2].ToObject<float>()), new Vector3(arr[3].ToObject<float>(), arr[4].ToObject<float>(), arr[5].ToObject<float>()));
            // }
            CodelyLogger.LogWarning($"Could not parse JToken '{token}' as Bounds using fallback. Returning new Bounds(Vector3.zero, Vector3.zero).");
            return new Bounds(Vector3.zero, Vector3.zero);
        }
        // --- End Redundant Parse Helpers ---

         /// <summary>
         /// Finds a specific UnityEngine.Object based on a find instruction JObject.
         /// Primarily used by UnityEngineObjectConverter during deserialization.
         /// </summary>
         // Made public static so UnityEngineObjectConverter can call it. Moved from ConvertJTokenToType.
         public static UnityEngine.Object FindObjectByInstruction(JObject instruction, Type targetType)
         {
             string findTerm = instruction["find"]?.ToString();
             string method = instruction["method"]?.ToString()?.ToLower();
             string componentName = instruction["component"]?.ToString(); // Specific component to get

             if (string.IsNullOrEmpty(findTerm))
             {
                 CodelyLogger.LogWarning("Find instruction missing 'find' term.");
                 return null;
             }

             // Use a flexible default search method if none provided
             string searchMethodToUse = string.IsNullOrEmpty(method) ? "by_id_or_name_or_path" : method;

             // If the target is an asset (Material, Texture, ScriptableObject etc.) try AssetDatabase first
             if (typeof(Material).IsAssignableFrom(targetType) ||
                 typeof(Texture).IsAssignableFrom(targetType) ||
                 typeof(ScriptableObject).IsAssignableFrom(targetType) ||
                 targetType.FullName.StartsWith("UnityEngine.U2D") || // Sprites etc.
                 typeof(AudioClip).IsAssignableFrom(targetType) ||
                 typeof(AnimationClip).IsAssignableFrom(targetType) ||
                 typeof(Font).IsAssignableFrom(targetType) ||
                 typeof(Shader).IsAssignableFrom(targetType) ||
                 typeof(ComputeShader).IsAssignableFrom(targetType) ||
                 typeof(GameObject).IsAssignableFrom(targetType) && findTerm.StartsWith("Assets/")) // Prefab check
             {
                // Try loading directly by path/GUID first
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath(findTerm, targetType);
                 if (asset != null) return asset;
                 asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(findTerm); // Try generic if type specific failed
                 if (asset != null && targetType.IsAssignableFrom(asset.GetType())) return asset;


                 // If direct path failed, try finding by name/type using FindAssets
                 string searchFilter = $"t:{targetType.Name} {System.IO.Path.GetFileNameWithoutExtension(findTerm)}"; // Search by type and name
                 string[] guids = AssetDatabase.FindAssets(searchFilter);

                 if (guids.Length == 1)
                 {
                     asset = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(guids[0]), targetType);
                     if (asset != null) return asset;
                 }
                 else if (guids.Length > 1)
                 {
                     CodelyLogger.LogWarning($"[FindObjectByInstruction] Ambiguous asset find: Found {guids.Length} assets matching filter '{searchFilter}'. Provide a full path or unique name.");
                     // Optionally return the first one? Or null? Returning null is safer.
                     return null;
                 }
                 // If still not found, fall through to scene search (though unlikely for assets)
             }


             // --- Scene Object Search ---
             // Find the GameObject using the internal finder
             GameObject foundGo = FindObjectInternal(new JValue(findTerm), searchMethodToUse);

             if (foundGo == null)
             {
                 // Don't warn yet, could still be an asset not found above
                 // CodelyLogger.LogWarning($"Could not find GameObject using instruction: {instruction}");
                 return null;
             }

             // Now, get the target object/component from the found GameObject
             if (targetType == typeof(GameObject))
             {
                 return foundGo; // We were looking for a GameObject
             }
             else if (typeof(Component).IsAssignableFrom(targetType))
             {
                 Type componentToGetType = targetType;
                 if (!string.IsNullOrEmpty(componentName))
                 {
                     Type specificCompType = FindType(componentName);
                     if (specificCompType != null && typeof(Component).IsAssignableFrom(specificCompType))
                     {
                         componentToGetType = specificCompType;
                     }
                     else
                     {
                         CodelyLogger.LogWarning($"Could not find component type '{componentName}' specified in find instruction. Falling back to target type '{targetType.Name}'.");
                     }
                 }

                 Component foundComp = foundGo.GetComponent(componentToGetType);
                 if (foundComp == null)
                 {
                     CodelyLogger.LogWarning($"Found GameObject '{foundGo.name}' but could not find component of type '{componentToGetType.Name}'.");
                 }
                 return foundComp;
             }
             else
             {
                  CodelyLogger.LogWarning($"Find instruction handling not implemented for target type: {targetType.Name}");
                  return null;
             }
         }


        /// <summary>
        /// Robust component resolver that avoids Assembly.LoadFrom and works with asmdefs.
        /// Searches already-loaded assemblies, prioritizing runtime script assemblies.
        /// </summary>
        private static Type FindType(string typeName)
        {
            if (ComponentResolver.TryResolve(typeName, out Type resolvedType, out string error))
            {
                return resolvedType;
            }
            
            // Log the resolver error if type wasn't found
            if (!string.IsNullOrEmpty(error))
            {
                CodelyLogger.LogWarning($"[FindType] {error}");
            }
            
            return null;
        }
    }
    
    /// <summary>
    /// Robust component resolver that avoids Assembly.LoadFrom and supports assembly definitions.
    /// Prioritizes runtime (Player) assemblies over Editor assemblies.
    /// </summary>
    internal static class ComponentResolver
    {
        private static readonly Dictionary<string, Type> CacheByFqn = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Type> CacheByName = new Dictionary<string, Type>(StringComparer.Ordinal);

        /// <summary>
        /// Resolve a Component/MonoBehaviour type by short or fully-qualified name.
        /// Prefers runtime (Player) script assemblies; falls back to Editor assemblies.
        /// Never uses Assembly.LoadFrom.
        /// </summary>
        public static bool TryResolve(string nameOrFullName, out Type type, out string error)
        {
            error = string.Empty;
            type = null;

            // Handle null/empty input
            if (string.IsNullOrWhiteSpace(nameOrFullName))
            {
                error = "Component name cannot be null or empty";
                return false;
            }

            // 1) Exact cache hits
            if (CacheByFqn.TryGetValue(nameOrFullName, out type)) return true;
            if (!nameOrFullName.Contains(".") && CacheByName.TryGetValue(nameOrFullName, out type)) return true;
            type = Type.GetType(nameOrFullName, throwOnError: false);
            if (IsValidComponent(type)) { Cache(type); return true; }

            // 2) Search loaded assemblies (prefer Player assemblies)
            var candidates = FindCandidates(nameOrFullName);
            if (candidates.Count == 1) { type = candidates[0]; Cache(type); return true; }
            if (candidates.Count > 1) { error = Ambiguity(nameOrFullName, candidates); type = null; return false; }

#if UNITY_EDITOR
            // 3) Last resort: Editor-only TypeCache (fast index)
            var tc = TypeCache.GetTypesDerivedFrom<Component>()
                              .Where(t => NamesMatch(t, nameOrFullName));
            candidates = PreferPlayer(tc).ToList();
            if (candidates.Count == 1) { type = candidates[0]; Cache(type); return true; }
            if (candidates.Count > 1) { error = Ambiguity(nameOrFullName, candidates); type = null; return false; }
#endif

            error = $"Component type '{nameOrFullName}' not found in loaded runtime assemblies. " +
                    "Use a fully-qualified name (Namespace.TypeName) and ensure the script compiled.";
            type = null;
            return false;
        }

        private static bool NamesMatch(Type t, string q) =>
            t.Name.Equals(q, StringComparison.Ordinal) ||
            (t.FullName?.Equals(q, StringComparison.Ordinal) ?? false);

        private static bool IsValidComponent(Type t) =>
            t != null && typeof(Component).IsAssignableFrom(t);

        private static void Cache(Type t)
        {
            if (t.FullName != null) CacheByFqn[t.FullName] = t;
            CacheByName[t.Name] = t;
        }

        private static List<Type> FindCandidates(string query)
        {
            bool isShort = !query.Contains(".");
            var loaded = AppDomain.CurrentDomain.GetAssemblies();

#if UNITY_EDITOR
            // Names of Player (runtime) script assemblies (asmdefs + Assembly-CSharp)
            var playerAsmNames = new HashSet<string>(
                UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.Player).Select(a => a.name),
                StringComparer.Ordinal);

            IEnumerable<System.Reflection.Assembly> playerAsms = loaded.Where(a => playerAsmNames.Contains(a.GetName().Name));
            IEnumerable<System.Reflection.Assembly> editorAsms = loaded.Except(playerAsms);
#else
            IEnumerable<System.Reflection.Assembly> playerAsms = loaded;
            IEnumerable<System.Reflection.Assembly> editorAsms = Array.Empty<System.Reflection.Assembly>();
#endif
            IEnumerable<Type> SafeGetTypes(System.Reflection.Assembly a)
            {
                try { return a.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { return rtle.Types.Where(t => t != null); }
            }

            Func<Type, bool> match;
            if (isShort)
                match = t => t.Name.Equals(query, StringComparison.Ordinal);
            else
                match = t => t.FullName != null && t.FullName.Equals(query, StringComparison.Ordinal);

            var fromPlayer = playerAsms.SelectMany(SafeGetTypes)
                                       .Where(IsValidComponent)
                                       .Where(match);
            var fromEditor = editorAsms.SelectMany(SafeGetTypes)
                                       .Where(IsValidComponent)
                                       .Where(match);

            var list = new List<Type>(fromPlayer);
            if (list.Count == 0) list.AddRange(fromEditor);
            return list;
        }

#if UNITY_EDITOR
        private static IEnumerable<Type> PreferPlayer(IEnumerable<Type> seq)
        {
            var player = new HashSet<string>(
                UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.Player).Select(a => a.name),
                StringComparer.Ordinal);

            return seq.OrderBy(t => player.Contains(t.Assembly.GetName().Name) ? 0 : 1);
        }
#endif

        private static string Ambiguity(string query, IEnumerable<Type> cands)
        {
            var lines = cands.Select(t => $"{t.FullName} (assembly {t.Assembly.GetName().Name})");
            return $"Multiple component types matched '{query}':\n - " + string.Join("\n - ", lines) +
                   "\nProvide a fully qualified type name to disambiguate.";
        }

        /// <summary>
        /// Gets all accessible property and field names from a component type.
        /// </summary>
        public static List<string> GetAllComponentProperties(Type componentType)
        {
            if (componentType == null) return new List<string>();

            var properties = componentType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                         .Where(p => p.CanRead && p.CanWrite)
                                         .Select(p => p.Name);

            var fields = componentType.GetFields(BindingFlags.Public | BindingFlags.Instance)
                                     .Where(f => !f.IsInitOnly && !f.IsLiteral)
                                     .Select(f => f.Name);

            // Also include SerializeField private fields (common in Unity)
            var serializeFields = componentType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                                              .Where(f => f.GetCustomAttribute<SerializeField>() != null)
                                              .Select(f => f.Name);

            return properties.Concat(fields).Concat(serializeFields).Distinct().OrderBy(x => x).ToList();
        }

        /// <summary>
        /// Uses AI to suggest the most likely property matches for a user's input.
        /// </summary>
        public static List<string> GetAIPropertySuggestions(string userInput, List<string> availableProperties)
        {
            if (string.IsNullOrWhiteSpace(userInput) || !availableProperties.Any())
                return new List<string>();

            // Simple caching to avoid repeated AI calls for the same input
            var cacheKey = $"{userInput.ToLowerInvariant()}:{string.Join(",", availableProperties)}";
            if (PropertySuggestionCache.TryGetValue(cacheKey, out var cached))
                return cached;

            try
            {
                var prompt = $"A Unity developer is trying to set a component property but used an incorrect name.\n\n" +
                             $"User requested: \"{userInput}\"\n" +
                             $"Available properties: [{string.Join(", ", availableProperties)}]\n\n" +
                             $"Find 1-3 most likely matches considering:\n" +
                             $"- Unity Inspector display names vs actual field names (e.g., \"Max Reach Distance\" → \"maxReachDistance\")\n" +
                             $"- camelCase vs PascalCase vs spaces\n" +
                             $"- Similar meaning/semantics\n" +
                             $"- Common Unity naming patterns\n\n" +
                             $"Return ONLY the matching property names, comma-separated, no quotes or explanation.\n" +
                             $"If confidence is low (<70%), return empty string.\n\n" +
                             $"Examples:\n" +
                             $"- \"Max Reach Distance\" → \"maxReachDistance\"\n" +
                             $"- \"Health Points\" → \"healthPoints, hp\"\n" +
                             $"- \"Move Speed\" → \"moveSpeed, movementSpeed\"";

                // For now, we'll use a simple rule-based approach that mimics AI behavior
                // This can be replaced with actual AI calls later
                var suggestions = GetRuleBasedSuggestions(userInput, availableProperties);
                
                PropertySuggestionCache[cacheKey] = suggestions;
                return suggestions;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[AI Property Matching] Error getting suggestions for '{userInput}': {ex.Message}");
                return new List<string>();
            }
        }

        private static readonly Dictionary<string, List<string>> PropertySuggestionCache = new Dictionary<string, List<string>>();

        /// <summary>
        /// Rule-based suggestions that mimic AI behavior for property matching.
        /// This provides immediate value while we could add real AI integration later.
        /// </summary>
        private static List<string> GetRuleBasedSuggestions(string userInput, List<string> availableProperties)
        {
            var suggestions = new List<string>();
            var cleanedInput = userInput.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");

            foreach (var property in availableProperties)
            {
                var cleanedProperty = property.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
                
                // Exact match after cleaning
                if (cleanedProperty == cleanedInput)
                {
                    suggestions.Add(property);
                    continue;
                }

                // Check if property contains all words from input
                var inputWords = userInput.ToLowerInvariant().Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
                if (inputWords.All(word => cleanedProperty.Contains(word.ToLowerInvariant())))
                {
                    suggestions.Add(property);
                    continue;
                }

                // Levenshtein distance for close matches
                if (LevenshteinDistance(cleanedInput, cleanedProperty) <= Math.Max(2, cleanedInput.Length / 4))
                {
                    suggestions.Add(property);
                }
            }

            // Prioritize exact matches, then by similarity
            return suggestions.OrderBy(s => LevenshteinDistance(cleanedInput, s.ToLowerInvariant().Replace(" ", "")))
                             .Take(3)
                             .ToList();
        }

        /// <summary>
        /// Calculates Levenshtein distance between two strings for similarity matching.
        /// </summary>
        private static int LevenshteinDistance(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1)) return s2?.Length ?? 0;
            if (string.IsNullOrEmpty(s2)) return s1.Length;

            var matrix = new int[s1.Length + 1, s2.Length + 1];

            for (int i = 0; i <= s1.Length; i++) matrix[i, 0] = i;
            for (int j = 0; j <= s2.Length; j++) matrix[0, j] = j;

            for (int i = 1; i <= s1.Length; i++)
            {
                for (int j = 1; j <= s2.Length; j++)
                {
                    int cost = (s2[j - 1] == s1[i - 1]) ? 0 : 1;
                    matrix[i, j] = Math.Min(Math.Min(
                        matrix[i - 1, j] + 1,      // deletion
                        matrix[i, j - 1] + 1),     // insertion
                        matrix[i - 1, j - 1] + cost); // substitution
                }
            }

            return matrix[s1.Length, s2.Length];
        }
    }

    // Continue ManageGameObject class with Ensure methods
    public static partial class ManageGameObject
    {
        // --- Ensure Methods (Idempotent Operations) ---

        /// <summary>
        /// Ensures a GameObject has a specific component. Idempotent - only adds if missing.
        /// </summary>
        private static object EnsureComponent(JObject @params, JToken targetToken, string searchMethod)
        {
            try
            {
                var writeCheck = WriteGuard.CheckWriteAllowed("ensure_component");
                if (writeCheck != null) return writeCheck;

                string componentType = @params["component_type"]?.ToString();
                if (string.IsNullOrEmpty(componentType))
                    return Response.Error("'component_type' parameter required for ensure_component.");

                GameObject go = FindObjectInternal(targetToken, searchMethod);
                if (go == null)
                    return Response.Error($"GameObject not found.");

                Type compType = FindType(componentType);
                if (compType == null)
                    return Response.Error($"Component type '{componentType}' not found.");

                Component existingComp = go.GetComponent(compType);
                if (existingComp != null)
                {
                    // Already exists - no change, so dirty=false (idempotent)
                    return new
                    {
                        success = true,
                        message = $"Component '{componentType}' already exists on '{go.name}'.",
                        data = new { gameObject = go.name, component = componentType, alreadyExists = true }
                    };
                }

                Component newComp = go.AddComponent(compType);
                EditorUtility.SetDirty(go);

                return new
                {
                    success = true,
                    message = $"Component '{componentType}' added to '{go.name}'.",
                    data = new { gameObject = go.name, component = componentType, alreadyExists = false }
                };
            }
            catch (Exception e)
            {
                return Response.Error($"Failed to ensure component: {e.Message}");
            }
        }

        /// <summary>
        /// Ensures a Renderer has a specific material assigned. Idempotent.
        /// </summary>
        private static object EnsureRendererMaterial(JObject @params, JToken targetToken, string searchMethod)
        {
            try
            {
                var writeCheck = WriteGuard.CheckWriteAllowed("ensure_renderer_material");
                if (writeCheck != null) return writeCheck;

                string materialPath = @params["material"]?.ToString();
                if (string.IsNullOrEmpty(materialPath))
                    return Response.Error("'material' parameter required for ensure_renderer_material.");

                GameObject go = FindObjectInternal(targetToken, searchMethod);
                if (go == null)
                    return Response.Error($"GameObject not found.");

                Renderer renderer = go.GetComponent<Renderer>();
                if (renderer == null)
                    return Response.Error($"GameObject '{go.name}' has no Renderer component.");

                Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                    return Response.Error($"Material not found at: {materialPath}");

                // Check if material is already assigned
                if (renderer.sharedMaterial == material)
                {
                    return new
                    {
                        success = true,
                        message = $"Material already assigned.",
                        data = new { gameObject = go.name, material = materialPath, alreadyAssigned = true }
                    };
                }

                renderer.sharedMaterial = material;
                EditorUtility.SetDirty(go);

                return new
                {
                    success = true,
                    message = $"Material assigned to renderer.",
                    data = new { gameObject = go.name, material = materialPath, alreadyAssigned = false }
                };
            }
            catch (Exception e)
            {
                return Response.Error($"Failed to ensure renderer material: {e.Message}");
            }
        }

        /// <summary>
        /// Ensures a MeshCollider has a specific mesh assigned. Idempotent.
        /// </summary>
        private static object EnsureMeshColliderMesh(JObject @params, JToken targetToken, string searchMethod)
        {
            try
            {
                var writeCheck = WriteGuard.CheckWriteAllowed("ensure_mesh_collider_mesh");
                if (writeCheck != null) return writeCheck;

                string meshPath = @params["mesh"]?.ToString();
                if (string.IsNullOrEmpty(meshPath))
                    return Response.Error("'mesh' parameter required for ensure_mesh_collider_mesh.");

                GameObject go = FindObjectInternal(targetToken, searchMethod);
                if (go == null)
                    return Response.Error($"GameObject not found.");

                MeshCollider collider = go.GetComponent<MeshCollider>();
                if (collider == null)
                    return Response.Error($"GameObject '{go.name}' has no MeshCollider component.");

                Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                if (mesh == null)
                    return Response.Error($"Mesh not found at: {meshPath}");

                if (collider.sharedMesh == mesh)
                {
                    return new
                    {
                        success = true,
                        message = $"Mesh already assigned.",
                        data = new { gameObject = go.name, mesh = meshPath, alreadyAssigned = true }
                    };
                }

                collider.sharedMesh = mesh;
                EditorUtility.SetDirty(go);

                return new
                {
                    success = true,
                    message = $"Mesh assigned to collider.",
                    data = new { gameObject = go.name, mesh = meshPath, alreadyAssigned = false }
                };
            }
            catch (Exception e)
            {
                return Response.Error($"Failed to ensure mesh collider mesh: {e.Message}");
            }
        }

        /// <summary>
        /// Ensures a Prefab's default sprite is set. Idempotent.
        /// </summary>
        private static object EnsurePrefabDefaultSprite(JObject @params)
        {
            try
            {
                var writeCheck = WriteGuard.CheckWriteAllowed("ensure_prefab_default_sprite");
                if (writeCheck != null) return writeCheck;

                string prefabPath = @params["prefab"]?.ToString();
                string spritePath = @params["sprite"]?.ToString();

                if (string.IsNullOrEmpty(prefabPath))
                    return Response.Error("'prefab' parameter required.");
                if (string.IsNullOrEmpty(spritePath))
                    return Response.Error("'sprite' parameter required.");

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                    return Response.Error($"Prefab not found at: {prefabPath}");

                Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
                if (sprite == null)
                    return Response.Error($"Sprite not found at: {spritePath}");

                SpriteRenderer spriteRenderer = prefab.GetComponent<SpriteRenderer>();
                if (spriteRenderer == null)
                    return Response.Error($"Prefab '{prefabPath}' has no SpriteRenderer component.");

                if (spriteRenderer.sprite == sprite)
                {
                    return new
                    {
                        success = true,
                        message = $"Sprite already assigned.",
                        data = new { prefab = prefabPath, sprite = spritePath, alreadyAssigned = true }
                    };
                }

                spriteRenderer.sprite = sprite;
                EditorUtility.SetDirty(prefab);
                // SavePrefabAsset persists in-place prefab changes atomically,
                // matching the pattern used in ManageAsset.ModifyAsset (L456-458).
                PrefabUtility.SavePrefabAsset(prefab);
                AssetDatabase.SaveAssets();

                return new
                {
                    success = true,
                    message = $"Sprite assigned to prefab.",
                    data = new { prefab = prefabPath, sprite = spritePath, alreadyAssigned = false }
                };
            }
            catch (Exception e)
            {
                return Response.Error($"Failed to ensure prefab sprite: {e.Message}");
            }
        }

        /// <summary>
        /// Create batch operation: execute multiple write-only GameObject operations in sequence.
        /// Supports alias capture (captureAs) and ref resolution ($alias).
        /// </summary>
        private static object HandleCreateBatch(JObject @params)
        {
            var opsToken = @params["ops"] as JArray;
            if (opsToken == null || opsToken.Count == 0)
            {
                return Response.Error("'ops' array is required for create_batch action.");
            }

            // Guardrail: keep batches small enough for reliable planning/retry (parity with TS client)
            if (opsToken.Count > MaxBatchOps)
            {
                return Response.Error(
                    $"Too many ops for create_batch action: {opsToken.Count}. Please split into multiple batches of <= {MaxBatchOps} ops."
                );
            }

            string mode = @params["mode"]?.ToString()?.ToLower() ?? "stop_on_error";
            if (mode != "stop_on_error" && mode != "continue_on_error")
            {
                return Response.Error($"Invalid mode: '{mode}'. Valid values are: stop_on_error, continue_on_error");
            }

            // create_batch is write-only (no reads). We still track whether there are
            // write ops so we can apply additional guardrails for deterministic targeting.
            var writeOps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ensure_component",
                "ensure_renderer_material",
                "ensure_mesh_collider_mesh",
                "ensure_prefab_default_sprite",
                "create",
                "modify",
                "delete",
                "remove_component",
                "set_component_property",
            };
            bool hasWriteOps = false;
            foreach (var token in opsToken)
            {
                var op = token as JObject;
                if (op == null) continue;
                string opId = op["id"]?.ToString() ?? "unknown";
                var opAction = op["action"]?.ToString()?.ToLower();
                if (string.IsNullOrEmpty(opAction)) continue;
                if (!writeOps.Contains(opAction))
                {
                    return Response.Error(
                        $"Op '{opId}' invalid: create_batch only supports write ops (no reads like '{opAction}')."
                    );
                }
                hasWriteOps = true;
            }

            // Extra guardrail for create_batch: enforce deterministic targeting (parity with TS client).
            if (hasWriteOps)
            {
                foreach (var token in opsToken)
                {
                    var op = token as JObject;
                    if (op == null) continue;
                    string opId = op["id"]?.ToString() ?? "unknown";
                    string opAction = op["action"]?.ToString()?.ToLower();
                    if (string.IsNullOrEmpty(opAction)) continue;

                    // Only validate deterministic targeting for write ops that can take target/parent.
                    if (!writeOps.Contains(opAction)) continue;

                    var opParams = op["params"] as JObject ?? new JObject();

                    // target determinism: allow instanceID (number), $alias, or by_path targeting.
                    var searchMethod = opParams["searchMethod"]?.ToString()?.ToLower();
                    var targetTok = opParams["target"];
                    if (targetTok != null && targetTok.Type == JTokenType.String)
                    {
                        var t = targetTok.ToString();
                        var isAlias = t.StartsWith("$");
                        var isPath = searchMethod == "by_path";
                        if (!isAlias && !isPath)
                        {
                            return Response.Error(
                                $"Op '{opId}' invalid: create_batch requires deterministic target. Use instanceID (number), $alias, or targetRef.hierarchy_path (searchMethod='by_path'). Do not use by_name targets inside create_batch."
                            );
                        }
                    }

                    // If the op uses targetRef.name, it will become by_name later; reject early for determinism.
                    if (targetTok == null && opParams["targetRef"] is JObject tr && tr["name"] != null)
                    {
                        return Response.Error(
                            $"Op '{opId}' invalid: create_batch requires deterministic target. Use instanceID (number), $alias, or targetRef.hierarchy_path (searchMethod='by_path'). Do not use by_name targets inside create_batch."
                        );
                    }

                    // parent determinism: allow instanceID (number) or $alias.
                    var parentTok = opParams["parent"];
                    if (parentTok != null && parentTok.Type == JTokenType.String)
                    {
                        var p = parentTok.ToString();
                        if (!p.StartsWith("$"))
                        {
                            return Response.Error(
                                $"Op '{opId}' invalid: create_batch requires deterministic parent. Use instanceID (number) or $alias for parent (avoid name-based parenting in create_batch)."
                            );
                        }
                    }
                }
            }

            var aliases = new Dictionary<string, long>();
            var results = new List<Dictionary<string, object>>();
            int succeeded = 0;
            int failed = 0;

            foreach (var opToken in opsToken)
            {
                var op = opToken as JObject;
                if (op == null)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        ["id"] = "unknown",
                        ["success"] = false,
                        ["message"] = "Invalid op format"
                    });
                    failed++;
                    if (mode == "stop_on_error") break;
                    continue;
                }

                string opId = op["id"]?.ToString() ?? "unknown";
                string opAction = op["action"]?.ToString()?.ToLower();
                bool allowFailure = op["allowFailure"]?.ToObject<bool>() ?? false;
                string captureAs = op["captureAs"]?.ToString();

                if (string.IsNullOrEmpty(opAction))
                {
                    results.Add(new Dictionary<string, object>
                    {
                        ["id"] = opId,
                        ["success"] = false,
                        ["message"] = "Op action is required"
                    });
                    failed++;
                    if (mode == "stop_on_error" && !allowFailure) break;
                    continue;
                }

                if (opAction == "batch")
                {
                    results.Add(new Dictionary<string, object>
                    {
                        ["id"] = opId,
                        ["success"] = false,
                        ["message"] = "Nested batch is not allowed"
                    });
                    failed++;
                    if (mode == "stop_on_error" && !allowFailure) break;
                    continue;
                }

                // Build params for the individual operation
                var opParams = op["params"] as JObject ?? new JObject();
                opParams = ResolveRefs(opParams, aliases);
                opParams["action"] = opAction;

                // Execute the operation
                try
                {
                    var opResult = HandleCommand(opParams);
                    bool opSuccess = true;
                    string opMessage = "Success";
                    string opCode = null;
                    object opData = null;
                    long? instanceId = null;

                    if (opResult is Dictionary<string, object> resultDict)
                    {
                        if (resultDict.TryGetValue("success", out var successObj) && successObj is bool s)
                            opSuccess = s;
                        if (resultDict.TryGetValue("message", out var msgObj))
                            opMessage = msgObj?.ToString();
                        if (resultDict.TryGetValue("code", out var codeObj))
                            opCode = codeObj?.ToString();
                        if (resultDict.TryGetValue("data", out var dataObj))
                        {
                            opData = dataObj;
                            // Try to extract instanceID for alias capture
                            if (dataObj is Dictionary<string, object> dataDict)
                            {
                                instanceId = ExtractStableInstanceId(dataDict);
                            }
                        }
                        else
                        {
                            opData = resultDict;
                            instanceId = ExtractStableInstanceId(resultDict);
                        }
                    }
                    else
                    {
                        opData = opResult;
                    }

                    // Best-effort instanceID extraction for alias capture (supports anonymous objects and JTokens)
                    if (!instanceId.HasValue)
                    {
                        instanceId = ExtractStableInstanceIdFromAny(opData) ?? ExtractStableInstanceIdFromAny(opResult);
                    }

                    // Capture alias if specified and operation succeeded
                    if (opSuccess && !string.IsNullOrEmpty(captureAs) && instanceId.HasValue)
                    {
                        aliases[captureAs] = instanceId.Value;
                    }

                    var resultEntry = new Dictionary<string, object>
                    {
                        ["id"] = opId,
                        ["action"] = opAction,
                        ["success"] = opSuccess,
                        ["message"] = opMessage
                    };
                    if (opCode != null) resultEntry["code"] = opCode;
                    if (opData != null) resultEntry["data"] = opData;

                    results.Add(resultEntry);

                    if (opSuccess)
                        succeeded++;
                    else
                    {
                        failed++;
                        if (mode == "stop_on_error" && !allowFailure) break;
                    }
                }
                catch (Exception e)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        ["id"] = opId,
                        ["action"] = opAction,
                        ["success"] = false,
                        ["message"] = e.Message,
                        ["code"] = "exception"
                    });
                    failed++;
                    if (mode == "stop_on_error" && !allowFailure) break;
                }
            }

            bool success = failed == 0;
            var message = success
                ? "Unity GameObject create_batch completed successfully."
                : "Unity GameObject create_batch completed with errors.";

            var response = new Dictionary<string, object>
            {
                ["mode"] = mode,
                ["aliases"] = aliases,
                ["summary"] = new Dictionary<string, object>
                {
                    ["total"] = opsToken.Count,
                    ["succeeded"] = succeeded,
                    ["failed"] = failed
                },
                ["results"] = results,
                ["success"] = success,
                ["message"] = message
            };

            if (!success)
            {
                response["code"] = "create_batch_failed";
                response["error"] = message;
            }

            return response;
        }

        /// <summary>
        /// Edit batch operation: "find-then-write" edits with early-stop on 0 targets found.
        ///
        /// Contract:
        /// - Phase 1: one or more `find` ops (must provide captureAs) to resolve deterministic instanceIDs.
        /// - Phase 2: write ops that must target the captured `$alias` (no by_name/by_path targets in edit_batch writes).
        /// - If a find resolves 0 targets, the workflow early-stops and returns success=true (not an error).
        /// </summary>
        private static object HandleEditBatch(JObject @params)
        {
            var opsToken = @params["ops"] as JArray;
            if (opsToken == null || opsToken.Count == 0)
            {
                return Response.Error("'ops' array is required for edit_batch action.");
            }

            if (opsToken.Count > MaxBatchOps)
            {
                return Response.Error(
                    $"Too many ops for edit_batch action: {opsToken.Count}. Please split into multiple batches of <= {MaxBatchOps} ops."
                );
            }

            string mode = @params["mode"]?.ToString()?.ToLower() ?? "stop_on_error";
            if (mode != "stop_on_error" && mode != "continue_on_error")
            {
                return Response.Error($"Invalid mode: '{mode}'. Valid values are: stop_on_error, continue_on_error");
            }

            var writeOps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ensure_component",
                "ensure_renderer_material",
                "ensure_mesh_collider_mesh",
                "ensure_prefab_default_sprite",
                "create",
                "modify",
                "delete",
                "remove_component",
                "set_component_property",
            };

            var aliases = new Dictionary<string, long>();
            var results = new List<Dictionary<string, object>>();
            int succeeded = 0;
            int failed = 0;

            bool seenWrite = false;
            int findOpsCount = 0;
            int writeOpsCount = 0;

            foreach (var opToken in opsToken)
            {
                var op = opToken as JObject;
                if (op == null)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        ["id"] = "unknown",
                        ["success"] = false,
                        ["message"] = "Invalid op format"
                    });
                    failed++;
                    if (mode == "stop_on_error") break;
                    continue;
                }

                string opId = op["id"]?.ToString() ?? "unknown";
                string opAction = op["action"]?.ToString()?.ToLower();
                bool allowFailure = op["allowFailure"]?.ToObject<bool>() ?? false;
                string captureAs = op["captureAs"]?.ToString();

                if (string.IsNullOrEmpty(opAction))
                {
                    results.Add(new Dictionary<string, object>
                    {
                        ["id"] = opId,
                        ["success"] = false,
                        ["message"] = "Op action is required"
                    });
                    failed++;
                    if (mode == "stop_on_error" && !allowFailure) break;
                    continue;
                }

                if (opAction == "batch" || opAction == "create_batch" || opAction == "edit_batch")
                {
                    return Response.Error($"Op '{opId}' invalid: nested batch is not allowed");
                }

                bool isWriteOp = writeOps.Contains(opAction);

                if (!isWriteOp)
                {
                    // Phase 1: find only
                    if (opAction != "find")
                    {
                        return Response.Error(
                            $"Op '{opId}' invalid: edit_batch only supports read op action 'find' before write ops."
                        );
                    }
                    if (seenWrite)
                    {
                        return Response.Error(
                            $"Op '{opId}' invalid: edit_batch requires all 'find' ops to come before write ops."
                        );
                    }
                    if (allowFailure)
                    {
                        return Response.Error(
                            $"Op '{opId}' invalid: allowFailure is not supported for find ops in edit_batch."
                        );
                    }
                    if (string.IsNullOrEmpty(captureAs) || !captureAs.StartsWith("$"))
                    {
                        return Response.Error(
                            $"Op '{opId}' invalid: find op must provide captureAs starting with '$' (e.g. '$target')."
                        );
                    }
                    if (aliases.ContainsKey(captureAs))
                    {
                        return Response.Error($"Duplicate captureAs alias: {captureAs}");
                    }

                    // Build params for find (force deterministic single-result behavior)
                    var opParams = op["params"] as JObject ?? new JObject();
                    if (opParams["findAll"]?.ToObject<bool>() == true)
                    {
                        return Response.Error($"Op '{opId}' invalid: findAll must be false for edit_batch.");
                    }
                    opParams["findAll"] = false;
                    opParams["action"] = "find";

                    var opResult = HandleCommand(opParams);

                    bool opSuccess = true;
                    string opMessage = "Success";
                    string opCode = null;
                    object opData = null;

                    if (opResult is Dictionary<string, object> resultDict)
                    {
                        if (resultDict.TryGetValue("success", out var successObj) && successObj is bool s)
                            opSuccess = s;
                        if (resultDict.TryGetValue("message", out var msgObj))
                            opMessage = msgObj?.ToString();
                        if (resultDict.TryGetValue("code", out var codeObj))
                            opCode = codeObj?.ToString();
                        if (resultDict.TryGetValue("data", out var dataObj))
                            opData = dataObj;
                        else
                            opData = resultDict;
                    }
                    else
                    {
                        opData = opResult;
                    }

                    var resultEntry = new Dictionary<string, object>
                    {
                        ["id"] = opId,
                        ["action"] = "find",
                        ["success"] = opSuccess,
                        ["message"] = opMessage
                    };
                    if (opCode != null) resultEntry["code"] = opCode;
                    if (opData != null) resultEntry["data"] = opData;
                    results.Add(resultEntry);

                    if (!opSuccess)
                    {
                        failed++;
                        if (mode == "stop_on_error") break;
                        continue;
                    }

                    // Expect a list of GameObject results (0 or 1, since findAll=false)
                    int foundCount = 0;
                    object first = null;
                    if (opData is List<object> list)
                    {
                        foundCount = list.Count;
                        if (list.Count > 0) first = list[0];
                    }
                    else if (opData is JArray jarr)
                    {
                        foundCount = jarr.Count;
                        if (jarr.Count > 0) first = jarr[0];
                    }

                    if (foundCount == 0)
                    {
                        // Early stop (not an error)
                        var early = new Dictionary<string, object>
                        {
                            ["mode"] = mode,
                            ["status"] = "early_stop",
                            ["reason"] = "no_targets_found",
                            ["aliases"] = aliases,
                            ["summary"] = new Dictionary<string, object>
                            {
                                ["total"] = results.Count,
                                ["succeeded"] = succeeded,
                                ["failed"] = failed
                            },
                            ["results"] = results,
                            ["success"] = true,
                            ["message"] = "Unity GameObject edit_batch early-stopped (0 targets found)."
                        };
                        return early;
                    }

                    if (foundCount > 1)
                    {
                        return Response.Error(
                            $"Op '{opId}' invalid: edit_batch find must return at most 1 result (set findAll=false)."
                        );
                    }

                    long? instanceId = ExtractStableInstanceIdFromAny(first);
                    if (!instanceId.HasValue)
                    {
                        return Response.Error(
                            $"Op '{opId}' invalid: edit_batch could not extract instanceID from find result."
                        );
                    }
                    aliases[captureAs] = instanceId.Value;

                    succeeded++;
                    findOpsCount++;
                    continue;
                }

                // Phase 2: write ops (must target aliases)
                seenWrite = true;
                writeOpsCount++;

                if (!string.IsNullOrEmpty(captureAs))
                {
                    return Response.Error(
                        $"Op '{opId}' invalid: captureAs is not supported for write ops in edit_batch (capture aliases using 'find')."
                    );
                }

                var rawParams = op["params"] as JObject ?? new JObject();
                if (rawParams["targetRef"] is JObject)
                {
                    return Response.Error(
                        $"Op '{opId}' invalid: edit_batch write ops must use target '$alias' (not targetRef)."
                    );
                }

                // If target/parent are strings, they must be $aliases (deterministic)
                var targetTok = rawParams["target"];
                if (targetTok != null && targetTok.Type == JTokenType.Integer)
                {
                    return Response.Error(
                        $"Op '{opId}' invalid: edit_batch write ops must target a $alias captured by a previous find op."
                    );
                }
                if (targetTok != null && targetTok.Type == JTokenType.String)
                {
                    var t = targetTok.ToString();
                    if (!t.StartsWith("$"))
                    {
                        return Response.Error(
                            $"Op '{opId}' invalid: edit_batch write ops must target a $alias captured by a previous find op."
                        );
                    }
                }
                if (targetTok is JObject targetObj && targetObj["ref"] != null)
                {
                    var r = targetObj["ref"]?.ToString() ?? "";
                    if (!r.StartsWith("$"))
                    {
                        return Response.Error(
                            $"Op '{opId}' invalid: edit_batch write ops must target a $alias captured by a previous find op."
                        );
                    }
                }
                var parentTok = rawParams["parent"];
                if (parentTok != null && parentTok.Type == JTokenType.Integer)
                {
                    return Response.Error(
                        $"Op '{opId}' invalid: edit_batch write ops must parent using a $alias captured by a previous find op."
                    );
                }
                if (parentTok != null && parentTok.Type == JTokenType.String)
                {
                    var p = parentTok.ToString();
                    if (!p.StartsWith("$"))
                    {
                        return Response.Error(
                            $"Op '{opId}' invalid: edit_batch write ops must parent using a $alias captured by a previous find op."
                        );
                    }
                }
                if (parentTok is JObject parentObj && parentObj["ref"] != null)
                {
                    var r = parentObj["ref"]?.ToString() ?? "";
                    if (!r.StartsWith("$"))
                    {
                        return Response.Error(
                            $"Op '{opId}' invalid: edit_batch write ops must parent using a $alias captured by a previous find op."
                        );
                    }
                }

                // Build params for the individual operation (resolve $aliases to instanceIDs)
                var opParamsWrite = ResolveRefs(rawParams, aliases);
                opParamsWrite["action"] = opAction;

                try
                {
                    var opResult = HandleCommand(opParamsWrite);
                    bool opSuccess = true;
                    string opMessage = "Success";
                    string opCode = null;
                    object opData = null;
                    long? instanceId = null;

                    if (opResult is Dictionary<string, object> resultDict)
                    {
                        if (resultDict.TryGetValue("success", out var successObj) && successObj is bool s)
                            opSuccess = s;
                        if (resultDict.TryGetValue("message", out var msgObj))
                            opMessage = msgObj?.ToString();
                        if (resultDict.TryGetValue("code", out var codeObj))
                            opCode = codeObj?.ToString();
                        if (resultDict.TryGetValue("data", out var dataObj))
                        {
                            opData = dataObj;
                            if (dataObj is Dictionary<string, object> dataDict)
                            {
                                instanceId = ExtractStableInstanceId(dataDict);
                            }
                        }
                        else
                        {
                            opData = resultDict;
                            instanceId = ExtractStableInstanceId(resultDict);
                        }
                    }
                    else
                    {
                        opData = opResult;
                    }

                    if (!instanceId.HasValue)
                    {
                        instanceId = ExtractStableInstanceIdFromAny(opData) ?? ExtractStableInstanceIdFromAny(opResult);
                    }

                    var resultEntry = new Dictionary<string, object>
                    {
                        ["id"] = opId,
                        ["action"] = opAction,
                        ["success"] = opSuccess,
                        ["message"] = opMessage
                    };
                    if (opCode != null) resultEntry["code"] = opCode;
                    if (opData != null) resultEntry["data"] = opData;

                    results.Add(resultEntry);

                    if (opSuccess)
                        succeeded++;
                    else
                    {
                        failed++;
                        if (mode == "stop_on_error" && !allowFailure) break;
                    }
                }
                catch (Exception e)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        ["id"] = opId,
                        ["action"] = opAction,
                        ["success"] = false,
                        ["message"] = e.Message,
                        ["code"] = "exception"
                    });
                    failed++;
                    if (mode == "stop_on_error" && !allowFailure) break;
                }
            }

            if (findOpsCount == 0)
            {
                return Response.Error("edit_batch requires at least one find op with captureAs before write ops.");
            }
            if (writeOpsCount == 0)
            {
                return Response.Error("edit_batch requires at least one write op after find ops.");
            }

            bool success = failed == 0;
            var message = success
                ? "Unity GameObject edit_batch completed successfully."
                : "Unity GameObject edit_batch completed with errors.";

            var response = new Dictionary<string, object>
            {
                ["mode"] = mode,
                ["aliases"] = aliases,
                ["summary"] = new Dictionary<string, object>
                {
                    ["total"] = opsToken.Count,
                    ["succeeded"] = succeeded,
                    ["failed"] = failed
                },
                ["results"] = results,
                ["success"] = success,
                ["message"] = message
            };

            if (!success)
            {
                response["code"] = "edit_batch_failed";
                response["error"] = message;
            }

            return response;
        }

        /// <summary>
        /// Resolve $alias references in params to actual instanceIDs.
        /// </summary>
        private static JObject ResolveRefs(JObject obj, Dictionary<string, long> aliases)
        {
            var result = new JObject();
            foreach (var prop in obj.Properties())
            {
                result[prop.Name] = ResolveRefValue(prop.Value, aliases);
            }
            return result;
        }

        private static JToken ResolveRefValue(JToken value, Dictionary<string, long> aliases)
        {
            if (value == null) return null;

            switch (value.Type)
            {
                case JTokenType.String:
                    string strVal = value.ToString();
                    if (strVal.StartsWith("$") && aliases.TryGetValue(strVal, out long id))
                    {
                        return new JValue(id);
                    }
                    return value;

                case JTokenType.Object:
                    var objVal = value as JObject;
                    // Check for {ref: "$alias"} pattern
                    if (objVal != null && objVal.Count == 1 && objVal["ref"] != null)
                    {
                        string refStr = objVal["ref"].ToString();
                        if (refStr.StartsWith("$") && aliases.TryGetValue(refStr, out long refId))
                        {
                            return new JValue(refId);
                        }
                    }
                    // Recursively resolve nested objects
                    return ResolveRefs(objVal, aliases);

                case JTokenType.Array:
                    var arrVal = value as JArray;
                    var newArr = new JArray();
                    foreach (var item in arrVal)
                    {
                        newArr.Add(ResolveRefValue(item, aliases));
                    }
                    return newArr;

                default:
                    return value;
            }
        }

        private static bool TryParseStableInstanceId(JToken token, out long instanceId)
        {
            instanceId = 0;
            if (token == null) return false;

            if (token.Type == JTokenType.Integer)
            {
                instanceId = token.ToObject<long>();
                return true;
            }

            var text = token.ToString();
            return !string.IsNullOrEmpty(text) && long.TryParse(text, out instanceId);
        }

        private static bool TryParseStableInstanceId(string text, out long instanceId)
        {
            instanceId = 0;
            return !string.IsNullOrEmpty(text) && long.TryParse(text, out instanceId);
        }

        private static bool TryCoerceToLong(object value, out long result)
        {
            switch (value)
            {
                case long l:
                    result = l;
                    return true;
                case int i:
                    result = i;
                    return true;
                case ulong ul:
                    result = unchecked((long)ul);
                    return true;
                case string s when long.TryParse(s, out var parsed):
                    result = parsed;
                    return true;
                default:
                    result = 0;
                    return false;
            }
        }

        /// <summary>
        /// Extract instanceID from result dictionary.
        /// </summary>
        private static long? ExtractStableInstanceId(Dictionary<string, object> dict)
        {
            if (dict.TryGetValue("instanceID", out var id1) && TryCoerceToLong(id1, out var l1)) return l1;
            if (dict.TryGetValue("instanceId", out var id2) && TryCoerceToLong(id2, out var l2)) return l2;
            if (dict.TryGetValue("id", out var id3) && TryCoerceToLong(id3, out var l3)) return l3;

            // Check nested data
            if (dict.TryGetValue("data", out var dataObj) && dataObj is Dictionary<string, object> dataDict)
            {
                if (dataDict.TryGetValue("instanceID", out var did1) && TryCoerceToLong(did1, out var dl1)) return dl1;
                if (dataDict.TryGetValue("instanceId", out var did2) && TryCoerceToLong(did2, out var dl2)) return dl2;
                if (dataDict.TryGetValue("id", out var did3) && TryCoerceToLong(did3, out var dl3)) return dl3;
            }

            return null;
        }

        /// <summary>
        /// Extract instanceID from any object shape (anonymous objects, dictionaries, JTokens).
        /// Used by batch alias capture.
        /// </summary>
        private static long? ExtractStableInstanceIdFromAny(object obj)
        {
            if (obj == null) return null;

            try
            {
                if (obj is long ll) return ll;
                if (obj is int ii) return ii;

                if (obj is Dictionary<string, object> dict)
                {
                    return ExtractStableInstanceId(dict);
                }

                if (obj is JObject jobj)
                {
                    return ExtractStableInstanceIdFromJToken(jobj);
                }

                if (obj is JToken jt)
                {
                    return ExtractStableInstanceIdFromJToken(jt);
                }

                // Reflection (anonymous objects)
                var t = obj.GetType();
                var prop =
                    t.GetProperty("instanceID")
                    ?? t.GetProperty("instanceId")
                    ?? t.GetProperty("id");
                if (prop != null)
                {
                    var v = prop.GetValue(obj);
                    var parsed = ExtractStableInstanceIdFromAny(v);
                    if (parsed.HasValue) return parsed.Value;
                }

                var dataProp = t.GetProperty("data");
                if (dataProp != null)
                {
                    var dataObj = dataProp.GetValue(obj);
                    var parsed = ExtractStableInstanceIdFromAny(dataObj);
                    if (parsed.HasValue) return parsed.Value;
                }

                // Last resort: serialize to JObject and search
                var j = JObject.FromObject(obj);
                return ExtractStableInstanceIdFromJToken(j);
            }
            catch
            {
                return null;
            }
        }

        private static long? ExtractStableInstanceIdFromJToken(JToken token)
        {
            if (token == null) return null;
            try
            {
                if (token.Type == JTokenType.Integer)
                {
                    return token.ToObject<long>();
                }

                if (token.Type != JTokenType.Object)
                {
                    return null;
                }

                var obj = token as JObject;
                if (obj == null) return null;

                var direct = obj["instanceID"] ?? obj["instanceId"] ?? obj["id"];
                if (direct != null)
                {
                    if (direct.Type == JTokenType.Integer) return direct.ToObject<long>();
                    if (direct.Type == JTokenType.String && long.TryParse(direct.ToString(), out var parsed))
                        return parsed;
                }

                var data = obj["data"];
                if (data != null)
                {
                    return ExtractStableInstanceIdFromJToken(data);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}

