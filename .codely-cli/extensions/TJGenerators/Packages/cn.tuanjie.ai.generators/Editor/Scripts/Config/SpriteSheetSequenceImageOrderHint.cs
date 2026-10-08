#if UNITY_EDITOR
namespace TJGenerators.Config
{
    /// <summary>
    /// 精灵表序列帧：在 prompt 中说明 images 数组下标与参考图角色优先级。
    /// </summary>
    public static class SpriteSheetSequenceImageOrderHint
    {
        /// <summary>
        /// 在最终 prompt 末尾追加「提交顺序说明」。
        /// totalImages 为本次请求 images 数量；userReferenceCount 为其中前若干张为用户上传（身份参考），其余为布局参考（knowledge）。
        /// </summary>
        public static string AppendToPrompt(string prompt, int totalImages, int userReferenceCount)
        {
            if (totalImages <= 0)
                return prompt ?? "";

            int u = userReferenceCount;
            if (u < 0) u = 0;
            if (u > totalImages) u = totalImages;
            int k = totalImages - u;

            string block;
            if (u > 0 && k > 0)
            {
                block =
                    "\n\n【多参考图提交顺序（与 images 数组下标一致，从 0 开始）】\n" +
                    $"下标 0～{u - 1}：用户上传参考图 → 角色身份、服装、配色、体型、发型以该组为最高优先级。\n" +
                    $"下标 {u}～{totalImages - 1}：布局参考图（knowledge）→ 仅用于行列网格、帧位对齐与切片排布；禁止参考其中角色、画风、配色或动作外观。";
            }
            else if (u > 0 && k == 0)
            {
                block =
                    "\n\n【多参考图提交顺序（与 images 数组下标一致，从 0 开始）】\n" +
                    $"下标 0～{u - 1}：用户上传参考图 → 角色身份与外观以该组为准。本次请求未附带布局参考图。";
            }
            else
            {
                block =
                    "\n\n【多参考图提交顺序（与 images 数组下标一致，从 0 开始）】\n" +
                    $"下标 0～{totalImages - 1}：布局参考图（knowledge）→ 仅用于行列网格与切片排布；角色与动作须完全由上文文本与用户参考图决定（本次无用户上传参考图）。";
            }

            string p = prompt ?? "";
            return p.TrimEnd() + block;
        }
    }
}
#endif
