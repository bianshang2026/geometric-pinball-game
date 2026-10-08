using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace GeoBreaker.EditorTools
{
    /// <summary>
    /// UI 布局编辑器：在编辑模式下把运行时构建的大厅/地图 UI 预览到场景里，
    /// 直接在 Scene 视图拖拽 RectTransform 调整摆放，"保存摆放"导出覆盖表 JSON
    /// （Resources/UI/Layout_{屏名}.json），运行时 BuildUi 后经 UIArt.ApplyLayout 应用。
    /// 预览用 World Space Canvas 放在原点（Tuanjie Scene 视图不渲染 Overlay Canvas，
    /// 且 ScreenSpaceCamera 会强制接管相机 transform 无法定位）——
    /// 子物体的 anchoredPosition/sizeDelta 是相对 Canvas 1080×1920 框的局部坐标，
    /// 与运行时 Overlay 语义完全一致，保存值直接生效。
    /// 预览根不属场景内容——保存或关闭时自动删除，勿随场景保存。
    /// </summary>
    public static class UILayoutToolV3
    {
        const string PreviewRootName = "=== UI 预览（拖拽编辑后菜单保存，勿存场景）===";
        const string LayoutDir = "Assets/_Game/Data/Resources/UI";

        static GameObject _preview;
        static Transform _screenRoot;          // BuildUi 产物根（MainMenu/MapScreen）——覆盖表路径基准
        static string _previewName;

        [MenuItem("GeoBreaker/UI布局/预览 大厅")]
        public static void PreviewMainMenu() => BuildPreview("MainMenu");

        [MenuItem("GeoBreaker/UI布局/预览 地图")]
        public static void PreviewMap() => BuildPreview("MapScreen");

        [MenuItem("GeoBreaker/UI布局/保存摆放（预览关闭）")]
        public static void SaveLayout()
        {
            if (_preview == null || _screenRoot == null)
            {
                Debug.LogWarning("[UILayout] 无活动预览：先执行 预览 大厅/地图");
                return;
            }

            var items = new List<UIArt.UILayoutEntry>();
            Collect(_screenRoot, "", items);

            var layout = new UIArt.UILayout { items = items.ToArray() };
            if (!Directory.Exists(LayoutDir)) Directory.CreateDirectory(LayoutDir);
            var path = LayoutDir + "/Layout_" + _previewName + ".json";
            File.WriteAllText(path, JsonUtility.ToJson(layout, true));
            AssetDatabase.ImportAsset(path);
            Debug.Log("[UILayout] " + _previewName + " 摆放已保存 " + items.Count + " 项 → " + path
                + "（运行时构建自动应用；删除该文件即恢复代码默认布局）");
            ClosePreview();
        }

        [MenuItem("GeoBreaker/UI布局/关闭预览（不保存）")]
        public static void ClosePreview()
        {
            // 按名清理全部同名（防域重载/异常中断的历史残留——Find 单个会漏）
            foreach (var go in Object.FindObjectsOfType<GameObject>(true))
                if (go != null && go.name == PreviewRootName)
                    Object.DestroyImmediate(go);
            _preview = null;
            _screenRoot = null;
            _previewName = null;
        }

        static void BuildPreview(string screen)
        {
            ClosePreview();

            // World Space Canvas 居中于原点：Scene 视图默认视角直接可见可拖拽
            var rootGo = new GameObject(PreviewRootName);
            var canvas = rootGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var crt = rootGo.GetComponent<RectTransform>();
            crt.position = Vector3.zero;                            // 画布中心=原点（UI 朝 +Z）
            crt.sizeDelta = new Vector2(1080f, 1920f);              // 1080×1920 参考框=世界单位
            crt.localScale = Vector3.one;                           // 1 世界单位 = 1 参考单位（与运行时保存值一致）
            rootGo.AddComponent<GraphicRaycaster>();

            var hostGo = new GameObject("ScreenHost");           // 仅承载组件（避免与 BuildUi 产物根重名）
            hostGo.transform.SetParent(rootGo.transform, false);

            if (screen == "MainMenu")
            {
                var comp = hostGo.AddComponent<MainMenuScreen>();
                InvokeBuildUi(comp);
            }
            else
            {
                var comp = hostGo.AddComponent<MapScreen>();
                InvokeBuildUi(comp);
            }

            // BuildUi 产物根 = host 的唯一子物体（MainMenu / MapScreen）——覆盖表路径基准
            if (hostGo.transform.childCount == 0)
            {
                Debug.LogError("[UILayout] BuildUi 未产出根");
                ClosePreview();
                return;
            }
            _screenRoot = hostGo.transform.GetChild(0);

            // 若已有保存的覆盖表，先应用——在既有摆放基础上继续编辑
            UIArt.ApplyLayout(_screenRoot, screen);

            _preview = rootGo;
            _previewName = screen;
            Selection.activeGameObject = _screenRoot.gameObject;

            // Scene 视图自动对准 UI（2D 模式 + pivot 移到画布中心 + 正交尺寸覆盖 1080×1920）
            var sv = EditorWindow.GetWindow<SceneView>();
            if (sv != null)
            {
                sv.in2DMode = true;
                sv.pivot = Vector3.zero;
                sv.size = 1100f;
                sv.Repaint();
            }
            Debug.Log("[UILayout] " + screen + " 预览就绪（World Space @ 原点，视图已对准）：Scene 视图直接拖拽 RectTransform；"
                + "菜单 保存摆放 导出 JSON（预览随保存关闭）");
        }

        static void InvokeBuildUi(MonoBehaviour comp)
        {
            var mi = comp.GetType().GetMethod("BuildUi", BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null) { Debug.LogError("[UILayout] 未找到 BuildUi"); return; }
            mi.Invoke(comp, null);
        }

        /// <summary>递归收集点锚定对象的摆放（Stretch 拉伸对象跳过——语义不适用）。</summary>
        static void Collect(Transform t, string prefix, List<UIArt.UILayoutEntry> items)
        {
            foreach (Transform c in t)
            {
                var p = string.IsNullOrEmpty(prefix) ? c.name : prefix + "/" + c.name;
                if (c is RectTransform rt && rt.anchorMin == rt.anchorMax)
                {
                    items.Add(new UIArt.UILayoutEntry
                    {
                        path = p,
                        x = rt.anchoredPosition.x,
                        y = rt.anchoredPosition.y,
                        w = rt.sizeDelta.x,
                        h = rt.sizeDelta.y,
                    });
                }
                Collect(c, p, items);
            }
        }

        // 进 Play 前自动清理预览（防双份 UI 冲突/误存场景）
        [InitializeOnLoadMethod]
        static void HookPlayMode()
        {
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.ExitingEditMode) ClosePreview();
            };
        }
    }
}
