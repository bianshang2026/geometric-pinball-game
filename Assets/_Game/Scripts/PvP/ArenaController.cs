using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// PvP 竞技场（弹球对撞模式 v2.3）：**零机关**——纯球对球博弈（机关只在 PvE 关卡），
    /// 底边补实体墙（PvE 底部本是回收区）。theme 参数保留兼容（不再区分主题）。
    /// </summary>
    public static class ArenaController
    {
        public static void Build(GameConfig cfg, PhysicsMaterial2D bouncy, int theme, Transform root)
        {
            BuildBottomWall(cfg, bouncy, root);                  // PvE 底边是回收区——PvP 停用回收区后必须补实体墙（球弹回不离场）
        }

        // ---------------- PvP 底墙（与左右墙同规格：完全弹性+霓虹描边） ----------------

        static void BuildBottomWall(GameConfig cfg, PhysicsMaterial2D bouncy, Transform root)
        {
            float halfH = cfg.fieldHeight * 0.5f;
            float th = cfg.wallThickness;

            var go = new GameObject("PvpBottomWall");
            go.transform.SetParent(root, false);

            var col = go.AddComponent<BoxCollider2D>();
            col.offset = new Vector2(0f, -halfH + th * 0.5f);
            col.size = new Vector2(cfg.fieldWidth + th * 2f, th);
            col.sharedMaterial = bouncy;

            var glow = NeonStyle.MakeSprite(go.transform, "Glow", ProcSprites.Glow(), NeonStyle.Cyan, 4, true);
            glow.color = new Color(NeonStyle.Cyan.r, NeonStyle.Cyan.g, NeonStyle.Cyan.b, 0.10f);
            glow.transform.localPosition = new Vector3(0f, -halfH, 0f);
            glow.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            glow.transform.localScale = new Vector3(0.3f, cfg.fieldWidth / 4f, 1f);

            var line = NeonStyle.MakeSprite(go.transform, "Line", ProcSprites.SquareFill(), NeonStyle.Cyan, 5, false);
            line.transform.localPosition = new Vector3(0f, -halfH, 0f);
            line.transform.localScale = new Vector3(cfg.fieldWidth, 0.07f, 1f);
        }
    }
}
