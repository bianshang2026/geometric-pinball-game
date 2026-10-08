#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace TJGenerators.UI
{
    /// <summary>
    /// 图片预览组件 - 支持滚轮缩放、拖拽平移
    /// </summary>
    [Serializable]
    public class ImagePreview
    {
        private const float MinZoom = 1f;
        private const float MaxZoom = 6f;

        [SerializeField]
        [Range(MinZoom, MaxZoom)]
        private float zoom = MinZoom;

        [SerializeField]
        private Vector2 pan = Vector2.zero;

        private bool isDragging;

        /// <summary>
        /// 绘制支持缩放/平移的图片预览，返回预览块总高度（含间距）。
        /// </summary>
        /// <param name="drawOverlay">可选叠加层（如序列帧切割线），参数为 drawRect 与 texCoords。</param>
        public float Draw(
            Texture2D previewTex,
            float panelWidth,
            float windowHeight,
            bool isVerticalLayout,
            Action repaintCallback,
            Action<Rect, Rect> drawOverlay = null)
        {
            float previewWidth = Mathf.Max(140f, panelWidth - 12f);
            float previewHeight = Mathf.Max(
                140f,
                isVerticalLayout ? windowHeight * 0.32f : windowHeight * 0.56f
            );
            Rect areaRect = GUILayoutUtility.GetRect(
                previewWidth,
                previewHeight,
                GUILayout.ExpandWidth(true)
            );

            Event evt = Event.current;
            EditorGUI.DrawRect(areaRect, new Color(0.12f, 0.12f, 0.12f, 1f));

            if (previewTex == null)
                return areaRect.height + 8f;

            Rect drawRect = FitRectKeepAspect(areaRect, previewTex.width, previewTex.height);
            var texCoords = HandleZoomAndPanInput(drawRect, evt, repaintCallback);
            GUI.DrawTextureWithTexCoords(drawRect, previewTex, texCoords, true);
            drawOverlay?.Invoke(drawRect, texCoords);

            return areaRect.height + 8f;
        }

        /// <summary>
        /// 绘制缩放滑块。
        /// </summary>
        public void DrawZoomSlider(float sliderWidth)
        {
            zoom = GUILayout.HorizontalSlider(zoom, MinZoom, MaxZoom, GUILayout.Width(sliderWidth));
        }

        /// <summary>
        /// 绘制序列帧切割网格叠加层。
        /// </summary>
        public static void DrawSliceGridOverlay(Rect drawRect, int cols, int rows, Rect texCoords)
        {
            if (cols <= 1 && rows <= 1)
                return;

            Handles.BeginGUI();
            Color prevColor = Handles.color;
            Handles.color = new Color(1f, 0f, 0f, 0.9f);

            for (int c = 1; c < cols; c++)
            {
                float u = c / (float)cols;
                float nx = (u - texCoords.x) / texCoords.width;
                if (nx <= 0f || nx >= 1f)
                    continue;
                float x = drawRect.x + drawRect.width * nx;
                Handles.DrawLine(new Vector2(x, drawRect.y), new Vector2(x, drawRect.yMax));
            }

            for (int r = 1; r < rows; r++)
            {
                float v = r / (float)rows;
                float ny = (v - texCoords.y) / texCoords.height;
                if (ny <= 0f || ny >= 1f)
                    continue;
                float y = drawRect.y + drawRect.height * ny;
                Handles.DrawLine(new Vector2(drawRect.x, y), new Vector2(drawRect.xMax, y));
            }

            Handles.color = prevColor;
            Handles.EndGUI();
        }

        private Rect HandleZoomAndPanInput(Rect drawRect, Event evt, Action repaintCallback)
        {
            zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom);
            float visibleW = 1f / zoom;
            float visibleH = 1f / zoom;

            pan.x = Mathf.Clamp(pan.x, 0f, 1f - visibleW);
            pan.y = Mathf.Clamp(pan.y, 0f, 1f - visibleH);

            if (evt != null)
            {
                if (evt.type == EventType.ScrollWheel && drawRect.Contains(evt.mousePosition))
                {
                    float oldZoom = zoom;
                    float relX = Mathf.Clamp01((evt.mousePosition.x - drawRect.x) / Mathf.Max(1f, drawRect.width));
                    float relY = Mathf.Clamp01((evt.mousePosition.y - drawRect.y) / Mathf.Max(1f, drawRect.height));
                    float relYFromBottom = 1f - relY;
                    float focusU = pan.x + relX * (1f / oldZoom);
                    float focusV = pan.y + relYFromBottom * (1f / oldZoom);

                    zoom = Mathf.Clamp(oldZoom + (-evt.delta.y * 0.12f), MinZoom, MaxZoom);

                    float newVisibleW = 1f / zoom;
                    float newVisibleH = 1f / zoom;
                    pan.x = Mathf.Clamp(focusU - relX * newVisibleW, 0f, 1f - newVisibleW);
                    pan.y = Mathf.Clamp(focusV - relYFromBottom * newVisibleH, 0f, 1f - newVisibleH);

                    evt.Use();
                    repaintCallback?.Invoke();
                }
                else if (evt.type == EventType.MouseDown && evt.button == 0 && drawRect.Contains(evt.mousePosition) && zoom > 1.001f)
                {
                    isDragging = true;
                    evt.Use();
                }
                else if (evt.type == EventType.MouseDrag && evt.button == 0 && isDragging && zoom > 1.001f)
                {
                    float panStepX = evt.delta.x / Mathf.Max(1f, drawRect.width) * (1f / zoom);
                    float panStepY = evt.delta.y / Mathf.Max(1f, drawRect.height) * (1f / zoom);
                    pan.x = Mathf.Clamp(pan.x - panStepX, 0f, 1f - (1f / zoom));
                    pan.y = Mathf.Clamp(pan.y + panStepY, 0f, 1f - (1f / zoom));
                    evt.Use();
                    repaintCallback?.Invoke();
                }
                else if (evt.type == EventType.MouseUp || evt.rawType == EventType.MouseUp)
                {
                    isDragging = false;
                }
            }

            if (zoom <= 1.001f)
                pan = Vector2.zero;

            return new Rect(pan.x, pan.y, 1f / zoom, 1f / zoom);
        }

        private static Rect FitRectKeepAspect(Rect outer, int texW, int texH)
        {
            float srcAspect = texW / Mathf.Max(1f, texH);
            float dstAspect = outer.width / Mathf.Max(1f, outer.height);

            if (dstAspect > srcAspect)
            {
                float w = outer.height * srcAspect;
                float x = outer.x + (outer.width - w) * 0.5f;
                return new Rect(x, outer.y, w, outer.height);
            }

            float h = outer.width / Mathf.Max(0.01f, srcAspect);
            float y = outer.y + (outer.height - h) * 0.5f;
            return new Rect(outer.x, y, outer.width, h);
        }
    }
}
#endif
