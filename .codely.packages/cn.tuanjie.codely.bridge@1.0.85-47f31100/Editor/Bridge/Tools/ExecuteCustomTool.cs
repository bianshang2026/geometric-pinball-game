using System;
using System.Collections.Generic;
using System.Reflection;
using Codely.Newtonsoft.Json.Linq;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Executes custom tools registered in the Unity project.
    /// Custom tools must be static methods with a specific signature:
    /// public static object ToolName(JObject parameters)
    /// 
    /// Tools can be registered via the [CustomTool] attribute or
    /// by convention in the CustomToolsRegistry static class.
    /// </summary>
    public static class ExecuteCustomTool
    {
        // Attribute-discovered tools. Cleared and rebuilt on InvalidateCache / domain reload.
        private static readonly Dictionary<string, MethodInfo> _discoveredTools =
            new Dictionary<string, MethodInfo>();

        // Tools registered via RegisterTool(). Survives InvalidateCache / GetCustomToolsInfo
        // rescans so a query does not wipe hand-registered entries.
        private static readonly Dictionary<string, MethodInfo> _manualTools =
            new Dictionary<string, MethodInfo>();

        // Merged view used for lookup/execution: discovered + manual (manual wins on name clash).
        private static readonly Dictionary<string, MethodInfo> _registeredTools =
            new Dictionary<string, MethodInfo>();

        private static bool _initialized = false;
        private static readonly object _initLock = new object();

        /// <summary>
        /// Attribute to mark a method as a custom tool.
        /// </summary>
        [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
        public class CustomToolAttribute : Attribute
        {
            public string Name { get; }
            public string Description { get; }

            public CustomToolAttribute(string name, string description = null)
            {
                Name = name;
                Description = description;
            }
        }

        /// <summary>
        /// Main handler for executing custom tools.
        /// </summary>
        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return Response.Error("Parameters cannot be null.");
            }

            // Ensure tools are discovered
            EnsureInitialized();

            string toolName = @params["tool_name"]?.ToString();
            if (string.IsNullOrEmpty(toolName))
            {
                return Response.Error("'tool_name' parameter is required.");
            }

            JToken rawToolParams = @params["parameters"];
            JObject toolParams;
            if (rawToolParams == null || rawToolParams.Type == JTokenType.Null)
            {
                toolParams = new JObject();
            }
            else if (rawToolParams.Type == JTokenType.Object)
            {
                toolParams = (JObject)rawToolParams;
            }
            else
            {
                // Be forgiving with model/runtime mistakes. Custom tools receive
                // an empty object instead of failing the whole tool call.
                toolParams = new JObject();
            }

            try
            {
                // Check if tool exists
                if (!_registeredTools.TryGetValue(toolName, out MethodInfo method))
                {
                    // Try case-insensitive lookup
                    method = FindToolCaseInsensitive(toolName);
                    if (method == null)
                    {
                        return Response.Error($"Custom tool '{toolName}' not found. Available tools: {string.Join(", ", _registeredTools.Keys)}");
                    }
                }

                // Execute the tool
                CodelyLogger.Log($"[ExecuteCustomTool] Executing custom tool: {toolName}");
                var result = method.Invoke(null, new object[] { toolParams });

                // Wrap result in standard response format if not already
                if (result is Dictionary<string, object> dictResult && dictResult.ContainsKey("success"))
                {
                    // Already in standard format
                    return result;
                }

                // Wrap in success response
                return new
                {
                    success = true,
                    message = $"Custom tool '{toolName}' executed successfully.",
                    data = result
                };
            }
            catch (TargetInvocationException tie)
            {
                // Unwrap the inner exception
                var innerEx = tie.InnerException ?? tie;
                CodelyLogger.LogError($"[ExecuteCustomTool] Tool '{toolName}' failed: {innerEx}");
                return Response.Error($"Custom tool '{toolName}' execution failed: {innerEx.Message}");
            }
            catch (Exception e)
            {
                CodelyLogger.LogError($"[ExecuteCustomTool] Error executing tool '{toolName}': {e}");
                return Response.Error($"Error executing custom tool '{toolName}': {e.Message}");
            }
        }

        /// <summary>
        /// Registers a custom tool manually. Survives attribute-cache invalidation /
        /// <see cref="GetCustomToolsInfo"/> rescans.
        /// </summary>
        public static void RegisterTool(string name, MethodInfo method)
        {
            lock (_initLock)
            {
                if (_manualTools.ContainsKey(name) || _registeredTools.ContainsKey(name))
                {
                    CodelyLogger.LogWarning($"[ExecuteCustomTool] Tool '{name}' is already registered. Overwriting.");
                }
                _manualTools[name] = method;
                _registeredTools[name] = method;
                CodelyLogger.Log($"[ExecuteCustomTool] Registered custom tool: {name}");
            }
        }

        /// <summary>
        /// Clears the attribute-discovered cache and forces a rescan on next use.
        /// Manually registered tools (<see cref="RegisterTool"/>) are preserved.
        /// </summary>
        public static void InvalidateCache()
        {
            lock (_initLock)
            {
                _discoveredTools.Clear();
                RebuildMergedRegistryLocked();
                _initialized = false;
                CodelyLogger.Log(
                    $"[ExecuteCustomTool] Attribute cache invalidated " +
                    $"({_manualTools.Count} manual tool(s) preserved).");
            }
        }

        /// <summary>
        /// Lists all registered custom tools.
        /// </summary>
        public static IEnumerable<string> GetRegisteredTools()
        {
            EnsureInitialized();
            return _registeredTools.Keys;
        }

        /// <summary>
        /// Returns structured info about all registered tools. Forces an attribute rescan
        /// but preserves tools registered via <see cref="RegisterTool"/>.
        /// </summary>
        public static object GetCustomToolsInfo()
        {
            InvalidateCache();
            EnsureInitialized();

            var tools = new List<object>();
            foreach (var kvp in _registeredTools)
            {
                var attr = kvp.Value.GetCustomAttribute<CustomToolAttribute>();
                tools.Add(new
                {
                    name        = kvp.Key,
                    description = attr?.Description ?? "",
                    method      = $"{kvp.Value.DeclaringType?.FullName}.{kvp.Value.Name}",
                    source      = _manualTools.ContainsKey(kvp.Key) ? "manual" : "attribute"
                });
            }
            return new { tool_count = tools.Count, tools };
        }

        static void RebuildMergedRegistryLocked()
        {
            _registeredTools.Clear();
            foreach (var kvp in _discoveredTools)
                _registeredTools[kvp.Key] = kvp.Value;
            // Manual registrations win on name clash.
            foreach (var kvp in _manualTools)
                _registeredTools[kvp.Key] = kvp.Value;
        }

        /// <summary>
        /// Discovers and registers all custom tools in the project.
        /// </summary>
        private static void EnsureInitialized()
        {
            lock (_initLock)
            {
                if (_initialized)
                    return;

                try
                {
                    _discoveredTools.Clear();

                    // Scan all assemblies for [CustomTool] methods
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var assemblyName = assembly.GetName().Name;
                        if (assemblyName.StartsWith("UnityTcp"))
                        {
                            // always scan our assemblies
                        }
                        else if (assemblyName.StartsWith("System") ||
                            assemblyName.StartsWith("mscorlib") ||
                            assemblyName.StartsWith("Unity") ||
                            assemblyName.StartsWith("Newtonsoft") ||
                            assemblyName.StartsWith("netstandard") ||
                            assemblyName.StartsWith("Microsoft"))
                            continue;

                        try
                        {
                            foreach (var type in assembly.GetTypes())
                            {
                                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                                {
                                    var attr = method.GetCustomAttribute<CustomToolAttribute>();
                                    if (attr != null)
                                    {
                                        var parameters = method.GetParameters();
                                        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(JObject))
                                        {
                                            _discoveredTools[attr.Name] = method;
                                        }
                                        else
                                        {
                                            CodelyLogger.LogWarning($"[ExecuteCustomTool] Invalid signature for tool '{attr.Name}'. Expected: public static object ToolName(JObject parameters)");
                                        }
                                    }
                                }
                            }
                        }
                        catch (ReflectionTypeLoadException) { }
                    }

                    RebuildMergedRegistryLocked();

                    CodelyLogger.Log(
                        $"[ExecuteCustomTool] Initialization complete. " +
                        $"{_registeredTools.Count} custom tools registered " +
                        $"({_discoveredTools.Count} attribute, {_manualTools.Count} manual).");

                    if (_registeredTools.Count == 0)
                    {
                        CodelyLogger.LogWarning(
                            "[ExecuteCustomTool] No custom tools found. " +
                            "If you expect AI generation tools (generate_sprite, generate_3d_model, etc.), " +
                            "make sure the 'cn.tuanjie.ai.generators' package is listed in Packages/manifest.json. " +
                            "If Codely CLI is connected, it will attempt to install the package automatically.");
                    }
                }
                catch (Exception e)
                {
                    CodelyLogger.LogError($"[ExecuteCustomTool] Failed to initialize: {e}");
                }
                finally
                {
                    _initialized = true;
                }
            }
        }

        /// <summary>
        /// Finds a tool by name (case-insensitive).
        /// </summary>
        private static MethodInfo FindToolCaseInsensitive(string toolName)
        {
            foreach (var kvp in _registeredTools)
            {
                if (string.Equals(kvp.Key, toolName, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Value;
                }
            }
            return null;
        }
    }
}

