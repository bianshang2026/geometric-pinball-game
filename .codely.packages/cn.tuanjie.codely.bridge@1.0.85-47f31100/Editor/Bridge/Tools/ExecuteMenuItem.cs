using System;
using System.Collections.Generic; // Added for HashSet
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityTcp.Editor.Helpers; // For Response class

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Handles executing Unity Editor menu items by path.
    /// </summary>
    public static class ExecuteMenuItem
    {
        // Basic blacklist to prevent accidental execution of potentially disruptive menu items.
        // This can be expanded based on needs.
        private static readonly HashSet<string> _menuPathBlacklist = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            "File/Quit",
            // Add other potentially dangerous items like "Edit/Preferences...", "File/Build Settings..." if needed
        };

        private const string TmpEssentialsImportMenuPath =
            "Window/TextMeshPro/Import TMP Essential Resources";

        /// <summary>
        /// Main handler for executing menu items or getting available ones.
        /// </summary>
        private static readonly Dictionary<string, Func<JObject, object>> ActionHandlers =
            new Dictionary<string, Func<JObject, object>>
            {
                { "execute", ExecuteItem },
                { "get_available_menus", GetAvailableMenus },
            };

        public static object HandleCommand(JObject @params)
            => ActionRouter.Route(@params, ActionHandlers, defaultAction: "execute");

        /// <summary>
        /// Executes a specific menu item.
        /// </summary>
        private static object ExecuteItem(JObject @params)
        {
            // Try both naming conventions: snake_case and camelCase
            string menuPath = @params["menu_path"]?.ToString() ?? @params["menuPath"]?.ToString();
            // Optional future param retained for API compatibility; not used in synchronous mode
            // int timeoutMs = Math.Max(0, (@params["timeout_ms"]?.ToObject<int>() ?? 2000));

            // string alias = @params["alias"]?.ToString(); // TODO: Implement alias mapping based on refactor plan requirements.
            // JObject parameters = @params["parameters"] as JObject; // TODO: Investigate parameter passing (often not directly supported by ExecuteMenuItem).

            if (string.IsNullOrWhiteSpace(menuPath))
            {
                return Response.Error("Required parameter 'menu_path' or 'menuPath' is missing or empty.");
            }

            // Validate against blacklist
            if (_menuPathBlacklist.Contains(menuPath))
            {
                return Response.Error(
                    $"Execution of menu item '{menuPath}' is blocked for safety reasons."
                );
            }

            // TODO: Implement alias lookup here if needed (Map alias to actual menuPath).
            // if (!string.IsNullOrEmpty(alias)) { menuPath = LookupAlias(alias); if(menuPath == null) return Response.Error(...); }

            // TODO: Handle parameters ('parameters' object) if a viable method is found.
            // This is complex as EditorApplication.ExecuteMenuItem doesn't take arguments directly.
            // It might require finding the underlying EditorWindow or command if parameters are needed.

            try
            {
                // Trace incoming execute requests (debug-gated)
                CodelyLogger.Verbose($"[ExecuteMenuItem] Request to execute menu: '{menuPath}'");

                if (string.Equals(menuPath, TmpEssentialsImportMenuPath, StringComparison.Ordinal))
                {
                    TmpEssentialsAutoImporter.ScheduleImport();
                    return Response.Success(
                        $"Scheduled TMP essential resources import for menu item: '{menuPath}'",
                        new { executed = true, menuPath }
                    );
                }

                // Execute synchronously. This code runs on the Editor main thread in our bridge path.
                bool executed = EditorApplication.ExecuteMenuItem(menuPath);
                if (executed)
                {
                    // Success trace (debug-gated)
                    CodelyLogger.Verbose($"[ExecuteMenuItem] Executed successfully: '{menuPath}'");
                    return Response.Success(
                        $"Executed menu item: '{menuPath}'",
                        new { executed = true, menuPath }
                    );
                }
                CodelyLogger.LogWarning($"[ExecuteMenuItem] Failed (not found/disabled): '{menuPath}'");
                return Response.Error(
                    $"Failed to execute menu item (not found or disabled): '{menuPath}'",
                    new { executed = false, menuPath }
                );
            }
            catch (Exception e)
            {
                CodelyLogger.LogError($"[ExecuteMenuItem] Error executing '{menuPath}': {e}");
                return Response.Error($"Error executing menu item '{menuPath}': {e.Message}");
            }
        }

        // TODO: Add helper for alias lookup if implementing aliases.
        // private static string LookupAlias(string alias) { ... return actualMenuPath or null ... }

        // --- get_available_menus support ---------------------------------------------------

        // Standard built-in top-level Unity menus. These are native (C++-defined) and so are
        // invisible to TypeCache; we keep them as a baseline. Custom top-level menus such as
        // "AI" are added dynamically in EnumerateTopLevelSeeds(). Querying a menu that does not
        // exist simply returns no items, so over-seeding is harmless.
        private static readonly string[] _topLevelMenus =
        {
            "File", "Edit", "Assets", "GameObject", "Component", "Window", "Help", "Tools", "Mobile"
        };

        // Cached reflection handles for UnityEditor.Menu.GetMenuItems(string, bool, bool).
        private static bool _menuReflectionResolved;
        private static MethodInfo _getMenuItemsMethod;

        // 'path' PropertyInfo cached per concrete item runtime type. Keying by type keeps the
        // lookup cheap while staying correct if GetMenuItems ever returns more than one item
        // type (e.g. a separator type alongside regular items). A null value is cached too,
        // so a type that genuinely lacks a 'path' property is not re-probed every item.
        private static readonly Dictionary<Type, PropertyInfo> _pathPropByType =
            new Dictionary<Type, PropertyInfo>();

        /// <summary>
        /// Enumerates available Editor menu item paths.
        /// Optional params: 'menu_path'/'menuPath' to scope to a subtree (default: all),
        /// 'filter'/'search' for a case-insensitive substring match, and
        /// 'include_separators'/'includeSeparators' (default: false).
        /// </summary>
        private static object GetAvailableMenus(JObject @params)
        {
            string rootPath = @params["menu_path"]?.ToString() ?? @params["menuPath"]?.ToString() ?? string.Empty;
            rootPath = (rootPath ?? string.Empty).Trim().TrimEnd('/');

            string filter = @params["filter"]?.ToString() ?? @params["search"]?.ToString();
            bool includeSeparators = @params["include_separators"]?.ToObject<bool>()
                ?? @params["includeSeparators"]?.ToObject<bool>() ?? false;

            var paths = new HashSet<string>(StringComparer.Ordinal);

            // Primary source: native UnityEditor.Menu.GetMenuItems via reflection. This
            // captures built-in (C++-defined) menu items as well as script-defined ones.
            string reflectionError = TryCollectMenuPathsViaReflection(rootPath, includeSeparators, paths);

            // Supplement with [MenuItem]-attributed methods discovered via TypeCache. Merged
            // unconditionally into the same set so duplicates collapse, ensuring script-defined
            // items are always present even when the reflection API only returned a partial
            // subtree (e.g. immediate children) or was unavailable entirely.
            CollectMenuPathsViaTypeCache(rootPath, paths);

            if (paths.Count == 0 && reflectionError != null)
            {
                return Response.Error($"Unable to enumerate menu items: {reflectionError}");
            }

            IEnumerable<string> result = paths;
            if (!string.IsNullOrEmpty(filter))
            {
                result = result.Where(p => p.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            var sorted = result.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

            CodelyLogger.Verbose(
                $"[ExecuteMenuItem] get_available_menus -> {sorted.Count} item(s) (root='{rootPath}', filter='{filter}').");

            return Response.Success($"Found {sorted.Count} menu item(s).", sorted);
        }

        /// <summary>
        /// Walks the Editor menu tree using the internal UnityEditor.Menu.GetMenuItems API.
        /// Returns null on success, or an error message if the API could not be used.
        /// </summary>
        private static string TryCollectMenuPathsViaReflection(
            string rootPath, bool includeSeparators, HashSet<string> outPaths)
        {
            EnsureMenuReflection();
            if (_getMenuItemsMethod == null)
            {
                return "UnityEditor.Menu.GetMenuItems(string, bool, bool) was not found on this Unity version.";
            }

            try
            {
                var expanded = new HashSet<string>(StringComparer.Ordinal);
                var queue = new Queue<string>();
                queue.Enqueue(rootPath ?? string.Empty);

                // When listing everything, also seed the top-level menus in case the
                // empty-root query is not honored on this Unity version.
                if (string.IsNullOrEmpty(rootPath))
                {
                    foreach (string top in EnumerateTopLevelSeeds())
                    {
                        queue.Enqueue(top);
                    }
                }

                // Guard against pathological loops; real menu trees are far smaller.
                int guard = 0;
                const int maxExpansions = 20000;

                while (queue.Count > 0 && guard++ < maxExpansions)
                {
                    string q = queue.Dequeue();
                    if (!expanded.Add(q)) continue;

                    string[] childPaths = InvokeGetMenuItems(q, includeSeparators);
                    if (childPaths == null || childPaths.Length == 0) continue;

                    // GetMenuItems may return either the full subtree (deep paths) or only the
                    // immediate children of 'q'. Detect which by checking relative depth.
                    string prefix = q.Length == 0 ? string.Empty : q + "/";
                    bool returnedSubtree = false;

                    foreach (string p in childPaths)
                    {
                        if (string.IsNullOrEmpty(p)) continue;
                        outPaths.Add(p);

                        string rel = (prefix.Length > 0 && p.StartsWith(prefix, StringComparison.Ordinal))
                            ? p.Substring(prefix.Length)
                            : p;
                        if (rel.IndexOf('/') >= 0) returnedSubtree = true;
                    }

                    // Only walk deeper when we got immediate children; otherwise everything
                    // beneath 'q' is already collected.
                    if (!returnedSubtree)
                    {
                        foreach (string p in childPaths)
                        {
                            if (!string.IsNullOrEmpty(p)) queue.Enqueue(p);
                        }
                    }
                }

                return null;
            }
            catch (Exception e)
            {
                CodelyLogger.LogWarning($"[ExecuteMenuItem] Reflection enumeration failed: {e.Message}");
                return e.Message;
            }
        }

        /// <summary>
        /// Invokes the cached GetMenuItems method for a single menu path and returns the
        /// reported item paths. Returns an empty array if nothing is found.
        /// </summary>
        private static string[] InvokeGetMenuItems(string menuPath, bool includeSeparators)
        {
            System.Array items;
            try
            {
                items = _getMenuItemsMethod.Invoke(
                    null,
                    new object[] { menuPath ?? string.Empty, includeSeparators, false }
                ) as System.Array;
            }
            catch (Exception)
            {
                return null;
            }

            if (items == null || items.Length == 0) return Array.Empty<string>();

            var result = new List<string>(items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                object item = items.GetValue(i);
                if (item == null) continue;

                Type itemType = item.GetType();
                if (!_pathPropByType.TryGetValue(itemType, out PropertyInfo pathProp))
                {
                    pathProp = itemType.GetProperty(
                        "path",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    );
                    _pathPropByType[itemType] = pathProp;
                }

                string path = pathProp?.GetValue(item) as string;
                if (!string.IsNullOrEmpty(path)) result.Add(path);
            }

            return result.ToArray();
        }

        /// <summary>
        /// Returns the set of top-level menu names used to seed enumeration when the
        /// empty-root query is not honored. Combines the standard built-in menus (native,
        /// hence invisible to TypeCache) with the first path segment of every [MenuItem]
        /// discovered via TypeCache, so custom top-level menus (e.g. "AI/...") are not missed.
        /// </summary>
        private static IEnumerable<string> EnumerateTopLevelSeeds()
        {
            var seeds = new HashSet<string>(_topLevelMenus, StringComparer.Ordinal);

            try
            {
                foreach (MethodInfo method in TypeCache.GetMethodsWithAttribute<MenuItem>())
                {
                    foreach (MenuItem attr in method.GetCustomAttributes(typeof(MenuItem), false))
                    {
                        if (attr == null || attr.validate) continue;

                        string menuPath = StripMenuShortcut(attr.menuItem);
                        if (string.IsNullOrEmpty(menuPath)) continue;
                        if (menuPath.StartsWith("CONTEXT/", StringComparison.OrdinalIgnoreCase)) continue;

                        int slash = menuPath.IndexOf('/');
                        string top = slash >= 0 ? menuPath.Substring(0, slash) : menuPath;
                        if (!string.IsNullOrEmpty(top)) seeds.Add(top);
                    }
                }
            }
            catch (Exception e)
            {
                CodelyLogger.LogWarning($"[ExecuteMenuItem] Failed to derive dynamic menu seeds: {e.Message}");
            }

            return seeds;
        }

        /// <summary>
        /// Resolves and caches the UnityEditor.Menu.GetMenuItems reflection handle once.
        /// </summary>
        private static void EnsureMenuReflection()
        {
            if (_menuReflectionResolved) return;
            _menuReflectionResolved = true;

            try
            {
                System.Type menuType = typeof(EditorWindow).Assembly.GetType("UnityEditor.Menu");
                if (menuType == null) return;

                _getMenuItemsMethod = menuType.GetMethod(
                    "GetMenuItems",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    new System.Type[] { typeof(string), typeof(bool), typeof(bool) },
                    null
                );
            }
            catch (Exception e)
            {
                CodelyLogger.LogWarning($"[ExecuteMenuItem] Failed to resolve Menu.GetMenuItems: {e.Message}");
            }
        }

        /// <summary>
        /// Supplemental enumeration via [MenuItem]-attributed methods, filtered to the requested
        /// root path and merged into <paramref name="outPaths"/>. Catches script-defined items
        /// the reflection pass may miss; misses purely native items but works on any Unity version.
        /// </summary>
        private static void CollectMenuPathsViaTypeCache(string rootPath, HashSet<string> outPaths)
        {
            try
            {
                string prefix = string.IsNullOrEmpty(rootPath) ? null : rootPath;

                foreach (MethodInfo method in TypeCache.GetMethodsWithAttribute<MenuItem>())
                {
                    foreach (MenuItem attr in method.GetCustomAttributes(typeof(MenuItem), false))
                    {
                        if (attr == null || attr.validate) continue;

                        string menuPath = StripMenuShortcut(attr.menuItem);
                        if (string.IsNullOrEmpty(menuPath)) continue;

                        // Context-menu entries (CONTEXT/...) are not invokable top-level menus.
                        if (menuPath.StartsWith("CONTEXT/", StringComparison.OrdinalIgnoreCase)) continue;

                        if (prefix != null && !menuPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            continue;

                        outPaths.Add(menuPath);
                    }
                }
            }
            catch (Exception e)
            {
                CodelyLogger.LogWarning($"[ExecuteMenuItem] TypeCache enumeration failed: {e.Message}");
            }
        }

        // Matches a complete Unity menu hotkey token: any combination of modifier markers
        // (% Ctrl/Cmd, # Shift, & Alt) and an optional '_' (no-modifier) prefix, followed by a
        // single key — an alphanumeric character, an F-key (F1-F12), or a named special key.
        private static readonly Regex _shortcutTailPattern = new Regex(
            @"^[%#&]*_?(?:[A-Za-z0-9]|F(?:[1-9]|1[0-2])|LEFT|RIGHT|UP|DOWN|HOME|END|PGUP|PGDN|INS|DEL|TAB|SPACE|BACKSPACE|RETURN|ENTER|KP[0-9])$",
            RegexOptions.Compiled);

        /// <summary>
        /// Strips a trailing keyboard-shortcut token from a [MenuItem] path
        /// (e.g. "Tools/Do It %#g" -> "Tools/Do It") so it matches the path
        /// EditorApplication.ExecuteMenuItem expects.
        /// </summary>
        private static string StripMenuShortcut(string menuItem)
        {
            if (string.IsNullOrEmpty(menuItem)) return menuItem;

            int lastSpace = menuItem.LastIndexOf(' ');
            if (lastSpace <= 0) return menuItem.Trim();

            string tail = menuItem.Substring(lastSpace + 1);

            // A shortcut token must begin with a marker (% # & or _) AND match the full hotkey
            // grammar. Requiring the full match prevents stripping a legitimate trailing word
            // that merely starts with one of those characters, e.g. "Tools/Do _Thing" or a name
            // ending in "#5x" — only "modifiers + single key" tokens like "%#g" or "_F5" qualify.
            bool startsWithMarker = tail.Length > 0 &&
                (tail[0] == '%' || tail[0] == '#' || tail[0] == '&' || tail[0] == '_');

            if (startsWithMarker && _shortcutTailPattern.IsMatch(tail))
            {
                return menuItem.Substring(0, lastSpace).TrimEnd();
            }

            return menuItem.Trim();
        }
    }
}

