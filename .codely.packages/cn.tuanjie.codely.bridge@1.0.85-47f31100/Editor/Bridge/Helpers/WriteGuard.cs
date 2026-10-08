using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Write protection guard for Unity Editor operations.
    /// Prevents unsafe modifications during Play mode or other restricted states.
    /// </summary>
    public static class WriteGuard
    {
        /// <summary>
        /// Write guard policy enum.
        /// </summary>
        public enum Policy
        {
            /// <summary>
            /// Deny all writes in Play/Paused mode (default, safest)
            /// </summary>
            Deny,
            
            /// <summary>
            /// Allow writes but log warnings (experimental, use with caution)
            /// </summary>
            AllowWithWarning
        }

        // Current policy (default: Deny)
        private static Policy _currentPolicy = Policy.Deny;

        // Central "command.action" write classification used by UnityTcpBridge to
        // enforce the Play Mode write guard before dispatch. Clients no longer
        // track editor state locally, so this table is the single source of truth
        // for which bridge operations count as writes. Individual handlers may
        // still call CheckWriteAllowed for stricter, handler-specific rules.
        //
        // Keys are command types; values are the *top-level* action names from
        // that command's `switch (action)`. Nested switches (e.g.
        // manage_gameobject's search modes) are NOT actions and must
        // not be listed here. WriteGuardTests asserts this table against the
        // dispatch switches so an action added there cannot stay unclassified.
        private static readonly Dictionary<string, HashSet<string>> WriteActions =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["manage_gameobject"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "create_batch",
                    "edit_batch",
                    "ensure_component",
                    "ensure_renderer_material",
                    "ensure_mesh_collider_mesh",
                    "ensure_prefab_default_sprite",
                    "create",
                    "modify",
                    "delete",
                    "remove_component",
                    "set_component_property"
                },
                ["manage_asset"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "ensure_prefab_variant",
                    "create",
                    "modify"
                },
                ["manage_scene"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "create",
                    "load",
                    "save"
                },
                ["manage_package"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "install_package",
                    "remove_package"
                },
                ["manage_bake"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "bake_navmesh",
                    "bake_lighting",
                    "clear_navmesh",
                    "clear_baked_data"
                },
                ["manage_editor"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "add_tag",
                    "remove_tag",
                    // ensure_tag/ensure_layer write TagManager.asset via
                    // InternalEditorUtility.AddTag + AssetDatabase.SaveAssets
                    // exactly as add_tag/add_layer do, so they must be guarded
                    // identically or clients defeat the guard by retrying with
                    // the ensure_* variant.
                    "ensure_tag",
                    "add_layer",
                    "remove_layer",
                    "ensure_layer",
                    "set_active_tool"
                }
            };

        /// <summary>
        /// Gets the current write guard policy.
        /// </summary>
        public static Policy CurrentPolicy
        {
            get => _currentPolicy;
            set => _currentPolicy = value;
        }

        /// <summary>
        /// Returns whether the given bridge command/action pair is classified as
        /// a write operation subject to the Play Mode write guard.
        /// </summary>
        public static bool IsWriteAction(string commandName, string action)
        {
            if (string.IsNullOrEmpty(commandName) || string.IsNullOrEmpty(action))
            {
                return false;
            }

            return WriteActions.TryGetValue(commandName, out var actions)
                && actions.Contains(action);
        }

        /// <summary>
        /// Central write guard applied by UnityTcpBridge before dispatching a
        /// command. Returns null when the command may proceed (not a write, or
        /// writes currently allowed), or an error response object when blocked.
        /// </summary>
        public static object CheckCommandWriteAllowed(string commandName, string action)
        {
            if (!IsWriteAction(commandName, action))
            {
                return null;
            }

            return CheckWriteAllowed($"{commandName}.{action}");
        }

        /// <summary>
        /// Checks if write operations are allowed in current editor state.
        /// Returns null if allowed, or an error response object if blocked.
        /// </summary>
        /// <param name="operationName">Name of the operation being attempted (for logging)</param>
        /// <returns>Error response if blocked, null if allowed</returns>
        public static object CheckWriteAllowed(string operationName = "write operation")
        {
            // Check if we're in Play or Paused mode
            bool inPlayMode = EditorApplication.isPlaying || EditorApplication.isPaused;
            
            if (!inPlayMode)
            {
                // Not in Play mode - writes always allowed
                return null;
            }

            // In Play mode - check policy
            switch (_currentPolicy)
            {
                case Policy.Deny:
                    // Block the write and return error
                    string errorMessage = $"Cannot perform {operationName} in Play/Paused mode. " +
                                        "Stop Play mode first, or change write guard policy to 'allow_with_warning' (experimental).";
                    
                    CodelyLogger.LogWarning($"[WriteGuard] {errorMessage}");
                    
                    return Response.Error("write_blocked_in_play_mode", new
                    {
                        code = "write_blocked_in_play_mode",
                        message = errorMessage,
                        currentPlayMode = EditorApplication.isPlaying ? "playing" : "paused",
                        policy = "deny",
                        suggestion = "Stop Play mode or change policy to 'allow_with_warning'"
                    });

                case Policy.AllowWithWarning:
                    // Allow but warn
                    string warningMessage = $"[EXPERIMENTAL] Performing {operationName} in Play/Paused mode. " +
                                          "This may cause unexpected behavior or data loss!";
                    
                    CodelyLogger.LogWarning($"[WriteGuard] {warningMessage}");
                    
                    // Log to audit trail
                    LogAuditEvent(operationName, "allowed_with_warning");
                    
                    // Return null to allow operation
                    return null;

                default:
                    return Response.Error("Invalid write guard policy");
            }
        }

        /// <summary>
        /// Force-checks if write operations are blocked (returns true if blocked).
        /// </summary>
        public static bool IsWriteBlocked()
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPaused)
            {
                return false; // Not in Play mode - not blocked
            }

            return _currentPolicy == Policy.Deny;
        }

        /// <summary>
        /// Sets the write guard policy.
        /// </summary>
        /// <param name="policy">Policy to set ("deny" or "allow_with_warning")</param>
        /// <returns>Success or error response</returns>
        public static object SetPolicy(string policy)
        {
            if (string.IsNullOrEmpty(policy))
            {
                return Response.Error("Policy parameter is required");
            }

            string lowerPolicy = policy.ToLowerInvariant();
            
            switch (lowerPolicy)
            {
                case "deny":
                    _currentPolicy = Policy.Deny;
                    CodelyLogger.Log("[WriteGuard] Write guard policy set to: Deny");
                    return Response.Success($"Write guard policy set to 'deny'.", new { policy = "deny" });

                case "allow_with_warning":
                    _currentPolicy = Policy.AllowWithWarning;
                    CodelyLogger.LogWarning("[WriteGuard] Write guard policy set to: AllowWithWarning (EXPERIMENTAL)");
                    return Response.Success($"Write guard policy set to 'allow_with_warning' (experimental).", 
                        new { policy = "allow_with_warning", warning = "This is experimental and may cause issues" });

                default:
                    return Response.Error($"Invalid policy: '{policy}'. Valid policies are: 'deny', 'allow_with_warning'");
            }
        }

        /// <summary>
        /// Gets the current policy as a string.
        /// </summary>
        public static string GetPolicyString()
        {
            return _currentPolicy == Policy.Deny ? "deny" : "allow_with_warning";
        }

        /// <summary>
        /// Logs audit events for write operations in Play mode.
        /// </summary>
        private static void LogAuditEvent(string operationName, string action)
        {
            // In production, this could write to a file or telemetry system
            var auditEntry = new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                operation = operationName,
                action = action,
                playMode = EditorApplication.isPlaying ? "playing" : (EditorApplication.isPaused ? "paused" : "stopped"),
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
            };
            
            CodelyLogger.Log($"[WriteGuard Audit] {Codely.Newtonsoft.Json.JsonConvert.SerializeObject(auditEntry)}");
        }

        /// <summary>
        /// Creates a write-blocked error response with detailed information.
        /// </summary>
        public static object CreateBlockedResponse(string operationName, string additionalInfo = null)
        {
            string message = $"Write operation '{operationName}' blocked in Play/Paused mode.";
            if (!string.IsNullOrEmpty(additionalInfo))
            {
                message += $" {additionalInfo}";
            }

            return Response.Error("write_blocked_in_play_mode", new
            {
                code = "write_blocked_in_play_mode",
                operation = operationName,
                currentMode = EditorApplication.isPlaying ? "playing" : "paused",
                policy = GetPolicyString(),
                message = message,
                remediation = new
                {
                    options = new[]
                    {
                        "Stop Play mode (recommended)",
                        "Change write guard policy to 'allow_with_warning' (experimental, use with caution)"
                    }
                }
            });
        }
    }
}

