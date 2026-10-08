#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using TJGenerators.Utils;

namespace TJGenerators.UI
{
    /// <summary>
    /// 图片上传相关 UI：大图上传框、多图参考横排缩略图、单图上传。
    /// </summary>
    public static class UploadImageComponents
    {
        private const string PickImageDialogTitle = "选择图片";
        private const string DefaultAiActionLabel = "用AI生成";
        private const string PickImageFileFilter = TJGeneratorsImageAssetPathUtility.ReferenceImagePickFilter;

        private const float LargeFrameHeight = 180f;
        private const float LargeIconSize = 36f;
        private const float LargeIconTopOffset = 32f;
        private const float LargeTitleTopOffset = 79f;
        private const float LargeTitleHeight = 18f;
        private const float LargeHintHorizontalPadding = 30f;
        private const float LargeHintHeight = 28f;
        private const float LargeHintTopOffset = 106f;
        private const float LargeAiRowHeight = 22f;
        private const float LargeAiRowBottomInset = 8f;
        private const float LargeAiArrowTopInset = 8f;
        private const float LargeAiArrowWidth = 4f;
        private const float LargeAiArrowHeight = 7f;
        private const float LargeAiTextGap = 6f;
        private const float LargeAiLinkGap = 6f;
        private const float LargePreviewMaxWidth = 420f;
        private const float LargePreviewHeight = 163f;

        private const float SinglePreviewMaxWidth = 200f;
        private const float SinglePreviewHeight = 58f;
        private const float SinglePreviewTopPad = 8f;
        private const float SinglePreviewSidePad = 20f;
        private const float SingleHintBottomPad = 8f;
        private const float SingleHintHeight = 30f;

        private const float MultiRefThumbGap = 8f;
        private const float MultiRefThumbBoxSize = 60f;
        private const float UploadClearButtonSize = 14f;
        private const float MultiRefThumbHorizontalScrollBarReserve = 18f;

        private static readonly Dictionary<string, Vector2> _multiLargeUploadScrollPositions =
            new Dictionary<string, Vector2>();

        /// <summary>大图参考上传框布局高度。</summary>
        public static float ReferenceImageFrameHeight => LargeFrameHeight;

        /// <summary>
        /// 绘制新版大图上传组件（normal/hover/uploaded 三态）。
        /// <para>
        /// <paramref name="onPickDone"/> 在用户完成文件选择后被调用（<see cref="EditorApplication.delayCall"/> 延迟执行）。
        /// 调用方应在回调中将 path/tex 写回对应字段或列表。
        /// </para>
        /// </summary>
        public static void DrawLargeImageUpload(
            ref string imagePath,
            ref Texture2D uploadedImage,
            Action onAIGenClicked,
            Action repaint,
            Action onUserChanged = null,
            string aiActionLabel = null,
            Action<string, Texture2D> onPickDone = null)
        {
            Rect frameRect = AcquireLargeUploadFrameRect();
            bool hasImage = uploadedImage != null;

            DrawLargeUploadFrameBackground(frameRect, hasImage);

            Rect aiActionHitRect = Rect.zero;
            if (!hasImage)
                aiActionHitRect = DrawLargeUploadEmptyState(frameRect, onAIGenClicked, aiActionLabel);
            else if (TryHandleLargeUploadClear(
                         frameRect, uploadedImage, ref imagePath, ref uploadedImage, onUserChanged, repaint))
                return;

            HandleLargeUploadClick(
                frameRect,
                hasImage,
                aiActionHitRect,
                uploadedImage,
                onAIGenClicked,
                onUserChanged,
                repaint,
                onPickDone);
        }

        /// <summary>将延迟选图结果写入单图参考列表。</summary>
        public static void ApplySingleReferencePickResult(
            List<string> imagePaths,
            List<Texture2D> uploadedImages,
            string path,
            Texture2D texture)
        {
            if (imagePaths == null || uploadedImages == null)
                return;

            imagePaths.Clear();
            uploadedImages.Clear();
            if (string.IsNullOrEmpty(path))
                return;

            imagePaths.Add(path);
            if (texture != null)
                uploadedImages.Add(texture);
        }

        /// <summary>
        /// 绘制单图上传区域：标题 + 可点击区域 + 预览/占位 + 清除按钮 + 文件选择逻辑。
        /// <para>
        /// <paramref name="onPickDone"/> 在用户完成文件选择后被调用（<see cref="EditorApplication.delayCall"/> 延迟执行，
        /// 与 <see cref="DrawLargeImageUpload"/> 共用同一选图逻辑）。调用方应在回调中将 path/tex 写回对应字段。
        /// </para>
        /// </summary>
        public static void DrawSingleImageUpload(
            ref string imagePath,
            ref Texture2D uploadedImage,
            Action repaint,
            Action onUserChanged = null,
            Action<string, Texture2D> onPickDone = null)
        {
            Rect uploadAreaRect = AcquireSingleUploadAreaRect();

            Rect clearButtonRect = Rect.zero;
            if (uploadedImage != null)
            {
                Rect imageRect = GetSingleUploadPreviewRect(uploadAreaRect, uploadedImage);
                clearButtonRect = GetCornerCenterClearButtonRect(imageRect, UploadClearButtonSize);
            }

            HandleSingleUploadInput(
                uploadAreaRect,
                clearButtonRect,
                ref imagePath,
                ref uploadedImage,
                onUserChanged,
                repaint,
                onPickDone);

            DrawSingleUploadContent(uploadAreaRect, uploadedImage, clearButtonRect);
        }

        /// <summary>
        /// 绘制多图参考上传：标题下方横排 60×60 缩略图（超出可横向滚动），未满时显示大图上传框；已满时隐藏大图框。
        /// </summary>
        public static void DrawLargeMultiImageUpload(
            List<string> imagePaths,
            List<Texture2D> uploadedImages,
            int maxReferenceImages,
            Action repaint,
            string scrollStateKey,
            Action onAIGenClicked)
        {
            if (imagePaths == null || uploadedImages == null)
                return;

            bool hasImages = imagePaths.Count > 0;
            bool atMax = imagePaths.Count >= maxReferenceImages;

            if (hasImages)
            {
                if (DrawMultiReferenceThumbnailScrollRow(
                        imagePaths,
                        uploadedImages,
                        scrollStateKey,
                        repaint))
                {
                    // Consume GUILayout slots that were registered during the Layout pass
                    // but won't be drawn, to keep Begin/End groups balanced.
                    if (!atMax)
                    {
                        GUILayout.Space(CommonStyles.Space2);
                        AcquireLargeUploadFrameRect();
                    }
                    return;
                }

                if (!atMax)
                    GUILayout.Space(CommonStyles.Space2);
            }

            if (atMax)
                return;

            Rect frameRect = AcquireLargeUploadFrameRect();
            DrawLargeUploadFrameBackground(frameRect, false);

            Rect aiActionHitRect = DrawLargeUploadEmptyState(frameRect, onAIGenClicked, TJGeneratorsL10n.L(DefaultAiActionLabel));
            HandleLargeMultiUploadEmptyClick(
                frameRect,
                aiActionHitRect,
                onAIGenClicked,
                imagePaths,
                uploadedImages,
                maxReferenceImages,
                repaint);
        }

        private static Rect AcquireLargeUploadFrameRect()
        {
            Rect rect = GUILayoutUtility.GetRect(
                GUIContent.none,
                CommonStyles.UploadFrameNormalStyle,
                GUILayout.ExpandWidth(true),
                GUILayout.Height(LargeFrameHeight));
            return SnapRectToPixelGrid(rect);
        }

        private static void DrawLargeUploadFrameBackground(Rect frameRect, bool hasImage)
        {
            bool isHover = frameRect.Contains(Event.current.mousePosition);
            GUIStyle frameStyle = hasImage
                ? CommonStyles.UploadFrameUploadedStyle
                : (isHover ? CommonStyles.UploadFrameHoverStyle : CommonStyles.UploadFrameNormalStyle);

            Texture2D frameBg = frameStyle.normal.background;
            if (frameBg == null)
                return;

            UIComponents.DrawNineSlice(
                frameRect,
                frameBg,
                frameStyle.border.left,
                frameBg.height,
                fixedDestBorder: -1,
                overlapPx: 0f);
        }

        private static Rect DrawLargeUploadEmptyState(Rect frameRect, Action onAIGenClicked, string aiActionLabel)
        {
            DrawLargeUploadIcon(frameRect);
            DrawLargeUploadTitleAndHint(frameRect);
            return DrawLargeUploadAiActionRow(frameRect, onAIGenClicked, aiActionLabel);
        }

        private static void DrawLargeUploadIcon(Rect frameRect)
        {
            Texture2D uploadIcon = CommonStyles.UploadImageIconTexture;
            if (uploadIcon == null)
                return;

            Rect iconRect = new Rect(
                frameRect.x + (frameRect.width - LargeIconSize) * 0.5f,
                frameRect.y + LargeIconTopOffset,
                LargeIconSize,
                LargeIconSize);
            GUI.DrawTexture(iconRect, uploadIcon, ScaleMode.ScaleToFit, true);
        }

        private static void DrawLargeUploadTitleAndHint(Rect frameRect)
        {
            Rect titleRect = new Rect(
                frameRect.x,
                frameRect.y + LargeTitleTopOffset,
                frameRect.width,
                LargeTitleHeight);
            GUI.Label(titleRect, TJGeneratorsL10n.L("点击上传图片"), CommonStyles.UploadTitleStyle);

            float hintWidth = Mathf.Max(0f, frameRect.width - LargeHintHorizontalPadding * 2f);
            float hintX = frameRect.x + (frameRect.width - hintWidth) * 0.5f;
            Rect hintRect = new Rect(
                hintX,
                frameRect.y + LargeHintTopOffset,
                hintWidth,
                LargeHintHeight);
            GUI.Label(
                hintRect,
                TJGeneratorsL10n.L("支持png/jpg/jpeg，文件大小最大不超过10M，分辨率最低要求128*128，最高限制4096*4096"),
                CommonStyles.UploadHintStyle);
        }

        private static Rect DrawLargeUploadAiActionRow(Rect frameRect, Action onAIGenClicked, string aiActionLabel)
        {
            if (onAIGenClicked == null)
                return Rect.zero;

            string resolvedLabel = aiActionLabel ?? TJGeneratorsL10n.L(DefaultAiActionLabel);

            float rowY = frameRect.yMax - LargeAiRowBottomInset - LargeAiRowHeight;

            float noImageWidth = CommonStyles.UploadNoImageStyle.CalcSize(new GUIContent(TJGeneratorsL10n.L("没有图片？"))).x;
            float aiGenWidth = CommonStyles.UploadAIGenLinkStyle.CalcSize(new GUIContent(resolvedLabel)).x;
            float totalWidth = noImageWidth + LargeAiTextGap + aiGenWidth + LargeAiLinkGap + LargeAiArrowWidth;
            float startX = frameRect.x + (frameRect.width - totalWidth) * 0.5f;

            Rect noImageRect = new Rect(startX, rowY, noImageWidth, LargeAiRowHeight);
            Rect aiTextRect = new Rect(noImageRect.xMax + LargeAiTextGap, rowY, aiGenWidth, LargeAiRowHeight);
            Rect arrowRect = new Rect(
                aiTextRect.xMax + LargeAiLinkGap,
                rowY + LargeAiArrowTopInset,
                LargeAiArrowWidth,
                LargeAiArrowHeight);
            Rect hitRect = new Rect(startX, rowY, totalWidth, LargeAiRowHeight);

            GUI.Label(noImageRect, TJGeneratorsL10n.L("没有图片？"), CommonStyles.UploadNoImageStyle);
            GUI.Label(aiTextRect, resolvedLabel, CommonStyles.UploadAIGenLinkStyle);

            Texture2D greenArrow = CommonStyles.ArrowGreen4xTexture;
            if (greenArrow != null)
                GUI.DrawTexture(arrowRect, greenArrow, ScaleMode.ScaleToFit, true);

            EditorGUIUtility.AddCursorRect(hitRect, MouseCursor.Link);
            return hitRect;
        }

        private static bool TryHandleLargeUploadClear(
            Rect frameRect,
            Texture2D uploadedImage,
            ref string imagePath,
            ref Texture2D uploadedImageRef,
            Action onUserChanged,
            Action repaint)
        {
            Rect imageRect = GetLargeUploadPreviewRect(
                frameRect, uploadedImage, LargePreviewMaxWidth, LargePreviewHeight);
            Rect clearBtnRect = GetCornerCenterClearButtonRect(imageRect, UploadClearButtonSize);

            GUI.DrawTexture(imageRect, uploadedImage, ScaleMode.ScaleToFit, true);
            DrawCloseIcon(clearBtnRect);

            if (!IsLeftMouseDownIn(clearBtnRect))
                return false;

            ClearUploadedImage(ref imagePath, ref uploadedImageRef, onUserChanged);
            repaint?.Invoke();
            return true;
        }

        private static Rect GetLargeUploadPreviewRect(
            Rect frameRect,
            Texture2D image,
            float previewMaxWidth,
            float previewHeight)
        {
            // Clear button is centered on the image top-right; reserve one full button size on each side
            // so the button stays inside the dashed upload frame when the image fills the container.
            float clearInset = UploadClearButtonSize;
            float maxWidth = Mathf.Min(previewMaxWidth, Mathf.Max(0f, frameRect.width - clearInset * 2f));
            float maxHeight = Mathf.Min(previewHeight, Mathf.Max(0f, frameRect.height - clearInset * 2f));
            Rect container = new Rect(
                frameRect.x + (frameRect.width - maxWidth) * 0.5f,
                frameRect.y + (frameRect.height - maxHeight) * 0.5f,
                maxWidth,
                maxHeight);
            return GetScaleToFitRect(container, image);
        }

        private static void HandleLargeUploadClick(
            Rect frameRect,
            bool hasImage,
            Rect aiActionHitRect,
            Texture2D uploadedImage,
            Action onAIGenClicked,
            Action onUserChanged,
            Action repaint,
            Action<string, Texture2D> onPickDone)
        {
            if (!IsLeftMouseDownIn(frameRect))
                return;

            if (!hasImage && TryHandleAiActionClick(aiActionHitRect, onAIGenClicked, repaint))
                return;

            Event.current.Use();
            ScheduleDeferredImagePick(uploadedImage, onPickDone, onUserChanged, repaint);
        }

        private static Rect AcquireSingleUploadAreaRect()
        {
            Rect rect = GUILayoutUtility.GetRect(
                GUIContent.none,
                CommonStyles.ImageUploadAreaStyle,
                GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                GUI.Box(rect, GUIContent.none, CommonStyles.ImageUploadAreaStyle);
            return rect;
        }

        private static void HandleSingleUploadInput(
            Rect uploadAreaRect,
            Rect clearButtonRect,
            ref string imagePath,
            ref Texture2D uploadedImage,
            Action onUserChanged,
            Action repaint,
            Action<string, Texture2D> onPickDone)
        {
            Event evt = Event.current;
            if (evt.type != EventType.MouseDown || evt.button != 0)
                return;

            if (clearButtonRect != Rect.zero && clearButtonRect.Contains(evt.mousePosition))
            {
                ClearUploadedImage(ref imagePath, ref uploadedImage, onUserChanged);
                evt.Use();
                repaint?.Invoke();
                return;
            }

            if (!uploadAreaRect.Contains(evt.mousePosition))
                return;

            evt.Use();
            ScheduleDeferredImagePick(uploadedImage, onPickDone, onUserChanged, repaint);
        }

        /// <summary>
        /// 延迟打开文件对话框，避免原生对话框在 OnGUI 内触发重入导致 BeginLayoutGroup 堆栈错乱。
        /// </summary>
        private static void ScheduleDeferredImagePick(
            Texture2D textureToReplace,
            Action<string, Texture2D> onPickDone,
            Action onUserChanged,
            Action repaint)
        {
            EditorApplication.delayCall += () =>
            {
                string path = EditorUtility.OpenFilePanel(TJGeneratorsL10n.L(PickImageDialogTitle), "", PickImageFileFilter);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return;

                DestroyRuntimeTexture(textureToReplace);
                Texture2D loaded = TryLoadTextureFromDisk(path);
                if (loaded == null)
                    return;

                onPickDone?.Invoke(path, loaded);
                onUserChanged?.Invoke();
                repaint?.Invoke();
            };
        }

        private static void DrawSingleUploadContent(
            Rect uploadAreaRect,
            Texture2D uploadedImage,
            Rect clearButtonRect)
        {
            if (uploadedImage != null)
            {
                Rect imageRect = GetSingleUploadPreviewRect(uploadAreaRect, uploadedImage);
                GUI.DrawTexture(imageRect, uploadedImage, ScaleMode.ScaleToFit, true);
                DrawCloseIcon(clearButtonRect);
                return;
            }

            DrawSingleUploadPlaceholder(uploadAreaRect);
        }

        private static Rect GetSingleUploadPreviewContainer(Rect uploadAreaRect)
        {
            // Reserve UploadClearButtonSize * 0.5f on the right so the clear button
            // (centered on imageRect.xMax) never overflows the upload area boundary.
            float maxWidth = Mathf.Max(0f, uploadAreaRect.width - SinglePreviewSidePad - UploadClearButtonSize * 0.5f);
            float maxHeight = Mathf.Max(0f, uploadAreaRect.height - SinglePreviewTopPad * 2f);
            return new Rect(
                uploadAreaRect.x + (uploadAreaRect.width - maxWidth) * 0.5f,
                uploadAreaRect.y + SinglePreviewTopPad,
                maxWidth,
                maxHeight);
        }

        private static Rect GetSingleUploadPreviewRect(Rect uploadAreaRect, Texture2D image)
        {
            return GetScaleToFitRect(GetSingleUploadPreviewContainer(uploadAreaRect), image);
        }

        private static void DrawSingleUploadPlaceholder(Rect uploadAreaRect)
        {
            Texture2D placeholder = CommonStyles.PreviewImageDefaultTexture;
            if (placeholder == null)
                return;

            float scaledWidth = Mathf.Min(SinglePreviewMaxWidth, uploadAreaRect.width - SinglePreviewSidePad);

            Rect imageRect = new Rect(
                uploadAreaRect.x + (uploadAreaRect.width - scaledWidth) / 2,
                uploadAreaRect.y + SinglePreviewTopPad,
                scaledWidth,
                SinglePreviewHeight);
            GUI.DrawTexture(imageRect, placeholder, ScaleMode.ScaleToFit);

            Rect hintRect = new Rect(
                uploadAreaRect.x,
                uploadAreaRect.yMax - SingleHintBottomPad - SingleHintHeight,
                uploadAreaRect.width,
                SingleHintHeight);
            GUI.Label(hintRect, TJGeneratorsL10n.L("点击上传图片（支持JPG、PNG格式）"), CommonStyles.HintLabelStyle);
        }

        private static float MultiReferenceThumbRowHeight =>
            MultiRefThumbBoxSize + UploadClearButtonSize * 0.5f;

        private static float MultiReferenceThumbScrollViewHeight =>
            MultiReferenceThumbRowHeight + MultiRefThumbHorizontalScrollBarReserve;

        private static Rect GetMultiReferenceThumbBoxRect(Rect slotRect)
        {
            float topPad = UploadClearButtonSize * 0.5f;
            return new Rect(
                slotRect.x,
                slotRect.y + topPad,
                MultiRefThumbBoxSize,
                MultiRefThumbBoxSize);
        }

        private static bool DrawMultiReferenceThumbnailScrollRow(
            List<string> imagePaths,
            List<Texture2D> uploadedImages,
            string scrollStateKey,
            Action repaint)
        {
            Rect scrollViewRect = EditorGUILayout.GetControlRect(
                false, MultiReferenceThumbScrollViewHeight, GUILayout.ExpandWidth(true));

            int count = imagePaths.Count;
            // UploadClearButtonSize * 0.5f accounts for the clear button overflowing the right edge of the last thumbnail
            float contentWidth = count * MultiRefThumbBoxSize
                + Mathf.Max(0, count - 1) * MultiRefThumbGap
                + UploadClearButtonSize * 0.5f;
            Rect contentRect = new Rect(
                0f, 0f, Mathf.Max(contentWidth, scrollViewRect.width), MultiReferenceThumbRowHeight);

            string stripKey = scrollStateKey + "_strip";
            if (!_multiLargeUploadScrollPositions.TryGetValue(stripKey, out Vector2 scrollPos))
                scrollPos = Vector2.zero;

            scrollPos = GUI.BeginScrollView(
                scrollViewRect,
                scrollPos,
                contentRect,
                contentWidth > scrollViewRect.width + 0.5f,
                false);
            _multiLargeUploadScrollPositions[stripKey] = scrollPos;

            bool removed = false;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Rect slotRect = new Rect(
                        i * (MultiRefThumbBoxSize + MultiRefThumbGap), 0f, MultiRefThumbBoxSize, MultiReferenceThumbRowHeight);
                    if (DrawMultiReferenceThumbnailItem(
                            i,
                            slotRect,
                            imagePaths,
                            uploadedImages,
                            UploadClearButtonSize,
                            repaint))
                    {
                        removed = true;
                        break;
                    }
                }
            }
            finally
            {
                GUI.EndScrollView();
            }

            return removed;
        }

        /// <summary>
        /// 绘制单张多图参考缩略图（预览 + 清除），清除点击在 ScrollView 内容坐标系内处理。
        /// </summary>
        private static bool DrawMultiReferenceThumbnailItem(
            int index,
            Rect slotRect,
            List<string> imagePaths,
            List<Texture2D> uploadedImages,
            float clearSize,
            Action repaint)
        {
            Texture2D thumb = index < uploadedImages.Count ? uploadedImages[index] : null;
            Rect boxRect = GetMultiReferenceThumbBoxRect(slotRect);

            if (thumb != null)
            {
                Rect imageRect = GetScaleToFitRect(boxRect, thumb);
                GUI.DrawTexture(imageRect, thumb, ScaleMode.ScaleToFit, true);
            }
            else
                GUI.Box(boxRect, GUIContent.none);

            Rect clearRect = GetCornerCenterClearButtonRect(boxRect, clearSize);
            if (!DrawCloseIconButton(clearRect))
                return false;

            RemoveReferenceImageAt(index, imagePaths, uploadedImages);
            repaint?.Invoke();
            return true;
        }

        private static void TryAddReferenceImage(
            List<string> imagePaths,
            List<Texture2D> uploadedImages,
            int maxReferenceImages,
            string openFileTitle,
            string openFileFilter,
            string maxDialogMessage,
            Action repaint)
        {
            string path = EditorUtility.OpenFilePanel(openFileTitle, "", openFileFilter);
            if (string.IsNullOrEmpty(path))
                return;

            if (IsDuplicateReferenceImagePath(imagePaths, path))
                return;

            if (imagePaths.Count >= maxReferenceImages)
            {
                Debug.LogWarning($"[TJGenerators] {maxDialogMessage}");
                return;
            }

            AddReferenceImage(imagePaths, uploadedImages, path, null, repaint);
        }

        private static void RemoveReferenceImageAt(int index, List<string> imagePaths, List<Texture2D> uploadedImages)
        {
            imagePaths.RemoveAt(index);
            if (index >= uploadedImages.Count)
                return;

            DestroyRuntimeTexture(uploadedImages[index]);
            uploadedImages.RemoveAt(index);
        }

        /// <summary>清空参考图路径与运行时缩略图（不销毁 AssetDatabase 管理的纹理）。</summary>
        public static void ClearReferenceImages(
            List<string> imagePaths,
            List<Texture2D> uploadedImages)
        {
            if (imagePaths == null || uploadedImages == null)
                return;

            for (int i = uploadedImages.Count - 1; i >= 0; i--)
                DestroyRuntimeTexture(uploadedImages[i]);

            imagePaths.Clear();
            uploadedImages.Clear();
        }

        /// <summary>清空单张参考图路径与运行时缩略图。</summary>
        public static void ClearSingleReferenceImage(ref string imagePath, ref Texture2D uploadedImage)
        {
            imagePath = string.Empty;
            DestroyRuntimeTexture(uploadedImage);
            uploadedImage = null;
        }

        /// <summary>将参考图列表裁剪到配置上限。</summary>
        public static void TrimReferenceImagesToMax(
            List<string> imagePaths,
            List<Texture2D> uploadedImages,
            int maxReferenceImages)
        {
            if (imagePaths == null || uploadedImages == null)
                return;

            maxReferenceImages = Mathf.Max(1, maxReferenceImages);
            while (imagePaths.Count > maxReferenceImages)
                RemoveReferenceImageAt(imagePaths.Count - 1, imagePaths, uploadedImages);
        }

        /// <summary>将 AI 生成的参考图加入多图列表（去重并受上限约束）。</summary>
        public static bool TryAddReferenceImageFromAiResult(
            List<string> imagePaths,
            List<Texture2D> uploadedImages,
            int maxReferenceImages,
            string path,
            Texture2D texture,
            Action repaint)
        {
            if (imagePaths == null || uploadedImages == null || string.IsNullOrEmpty(path))
                return false;

            if (IsDuplicateReferenceImagePath(imagePaths, path))
                return false;

            if (imagePaths.Count >= maxReferenceImages)
                return false;

            AddReferenceImage(imagePaths, uploadedImages, path, texture, repaint);
            return true;
        }

        private static void HandleLargeMultiUploadEmptyClick(
            Rect frameRect,
            Rect aiActionHitRect,
            Action onAIGenClicked,
            List<string> imagePaths,
            List<Texture2D> uploadedImages,
            int maxReferenceImages,
            Action repaint)
        {
            if (!IsLeftMouseDownIn(frameRect))
                return;

            if (TryHandleAiActionClick(aiActionHitRect, onAIGenClicked, repaint))
                return;

            // Consume the event before opening the dialog. Calling OpenFilePanel directly
            // from inside OnGUI can trigger a re-entrant OnGUI repaint (Unity processes
            // OS window events while the native dialog is open), which resets the
            // GUILayout slot state and corrupts the group stack on return. Deferring via
            // delayCall ensures the dialog opens after the current OnGUI frame is complete.
            Event.current.Use();
            string maxMsg = string.Format(TJGeneratorsL10n.L("最多可选择 {0} 张参考图片"), maxReferenceImages);
            EditorApplication.delayCall += () =>
                TryAddReferenceImage(
                    imagePaths,
                    uploadedImages,
                    maxReferenceImages,
                    TJGeneratorsL10n.L(PickImageDialogTitle),
                    PickImageFileFilter,
                    maxMsg,
                    repaint);
        }

        private static bool TryHandleAiActionClick(Rect aiActionHitRect, Action onAIGenClicked, Action repaint)
        {
            if (onAIGenClicked == null || aiActionHitRect == Rect.zero)
                return false;

            if (!aiActionHitRect.Contains(Event.current.mousePosition))
                return false;

            Event.current.Use();
            onAIGenClicked();
            repaint?.Invoke();
            return true;
        }

        private static bool IsDuplicateReferenceImagePath(List<string> imagePaths, string path)
        {
            return imagePaths.Contains(path);
        }

        private static void AddReferenceImage(
            List<string> imagePaths,
            List<Texture2D> uploadedImages,
            string path,
            Texture2D providedTexture,
            Action repaint)
        {
            imagePaths.Add(path);
            uploadedImages.Add(providedTexture != null ? providedTexture : TryLoadTextureFromDisk(path));
            repaint?.Invoke();
        }

        private static Texture2D TryLoadTextureFromDisk(string path)
        {
            if (!File.Exists(path))
                return null;

            var texture = new Texture2D(2, 2);
            if (texture.LoadImage(File.ReadAllBytes(path)))
                return texture;

            UnityEngine.Object.DestroyImmediate(texture);
            return null;
        }

        private static void ClearUploadedImage(ref string imagePath, ref Texture2D uploadedImage, Action onUserChanged)
        {
            imagePath = string.Empty;
            DestroyRuntimeTexture(uploadedImage);
            uploadedImage = null;
            onUserChanged?.Invoke();
        }

        private static void DrawCloseIcon(Rect rect)
        {
            Texture2D icon = CommonStyles.CloseIconTexture;
            if (icon != null)
                GUI.DrawTexture(rect, icon, ScaleMode.ScaleToFit, true);
            else
                GUI.Label(rect, CommonStyles.ClearButtonSymbol, CommonStyles.ClearButtonStyle);
        }

        private static bool DrawCloseIconButton(Rect rect)
        {
            if (Event.current.type == EventType.Repaint)
                DrawCloseIcon(rect);
            return GUI.Button(rect, GUIContent.none, GUIStyle.none);
        }

        private static bool IsLeftMouseDownIn(Rect rect)
        {
            return Event.current.type == EventType.MouseDown
                && Event.current.button == 0
                && rect.Contains(Event.current.mousePosition);
        }

        /// <summary>清除按钮中心对齐图片右上角顶点。</summary>
        private static Rect GetCornerCenterClearButtonRect(Rect imageRect, float clearButtonSize)
        {
            float half = clearButtonSize * 0.5f;
            return new Rect(
                imageRect.xMax - half,
                imageRect.yMin - half,
                clearButtonSize,
                clearButtonSize);
        }

        /// <summary>与 <see cref="GUI.DrawTexture(Rect,Texture,ScaleMode,bool)"/> 的 ScaleToFit 一致的实际绘制区域。</summary>
        private static Rect GetScaleToFitRect(Rect container, Texture2D texture)
        {
            if (texture == null || texture.width <= 0 || texture.height <= 0)
                return container;

            float scale = Mathf.Min(
                container.width / texture.width,
                container.height / texture.height);
            float width = texture.width * scale;
            float height = texture.height * scale;
            return new Rect(
                container.x + (container.width - width) * 0.5f,
                container.y + (container.height - height) * 0.5f,
                width,
                height);
        }

        private static void DestroyRuntimeTexture(Texture2D texture)
        {
            if (texture != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(texture)))
                UnityEngine.Object.DestroyImmediate(texture);
        }

        private static Rect SnapRectToPixelGrid(Rect r)
        {
            float ppp = Mathf.Max(1f, EditorGUIUtility.pixelsPerPoint);
            float Snap(float v) => Mathf.Round(v * ppp) / ppp;
            float xMin = Snap(r.xMin);
            float yMin = Snap(r.yMin);
            float xMax = Snap(r.xMax);
            float yMax = Snap(r.yMax);
            float minSpan = 1f / ppp;
            if (xMax - xMin < minSpan)
                xMax = xMin + minSpan;
            if (yMax - yMin < minSpan)
                yMax = yMin + minSpan;
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }
    }
}
#endif
