using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 场地构建：左/右/上三面完全弹性墙 + 底部回收触发区（纯代码生成，无预制）。
    /// 竖屏 9x16，底部回收区是"飞行结束"的唯一正常出口。
    /// </summary>
    public class BattleField : MonoBehaviour
    {
        public PhysicsMaterial2D Bouncy { get; private set; }

        public void Build(GameConfig cfg)
        {
            Bouncy = new PhysicsMaterial2D("Bouncy") { bounciness = 1f, friction = 0f };

            float halfW = cfg.fieldWidth * 0.5f;
            float halfH = cfg.fieldHeight * 0.5f;
            float th = cfg.wallThickness;

            BuildWall("WallL",
                new Vector2(-halfW - th * 0.5f, 0f), new Vector2(th, cfg.fieldHeight + th * 2f),
                new Vector3(-halfW, 0f, 0f), new Vector3(0.07f, cfg.fieldHeight, 1f));
            BuildWall("WallR",
                new Vector2(halfW + th * 0.5f, 0f), new Vector2(th, cfg.fieldHeight + th * 2f),
                new Vector3(halfW, 0f, 0f), new Vector3(0.07f, cfg.fieldHeight, 1f));
            BuildWall("WallT",
                new Vector2(0f, halfH + th * 0.5f), new Vector2(cfg.fieldWidth + th * 2f, th),
                new Vector3(0f, halfH, 0f), new Vector3(cfg.fieldWidth, 0.07f, 1f));

            BuildRecycle(cfg);
        }

        void BuildWall(string name, Vector2 colOffset, Vector2 colSize, Vector3 linePos, Vector3 lineScale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);

            var col = go.AddComponent<BoxCollider2D>();
            col.offset = colOffset;
            col.size = colSize;
            col.sharedMaterial = Bouncy;

            // 霓虹视觉：Additive 光晕 + 实线
            float len = Mathf.Max(colSize.x, colSize.y) * 1.03f;
            bool horizontal = colSize.x > colSize.y;
            var glow = NeonStyle.MakeSprite(go.transform, "Glow", ProcSprites.Glow(), NeonStyle.Cyan, 4, true);
            glow.color = new Color(NeonStyle.Cyan.r, NeonStyle.Cyan.g, NeonStyle.Cyan.b, 0.10f);
            glow.transform.localPosition = linePos;
            if (horizontal) glow.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            glow.transform.localScale = new Vector3(0.3f, len / 4f, 1f);

            var line = NeonStyle.MakeSprite(go.transform, "Line", ProcSprites.SquareFill(), NeonStyle.Cyan, 5, false);
            line.transform.localPosition = linePos;
            line.transform.localScale = lineScale;
        }

        void BuildRecycle(GameConfig cfg)
        {
            float halfH = cfg.fieldHeight * 0.5f;
            float zoneCenterY = -halfH + cfg.recycleZoneHeight * 0.5f;
            float lineY = -halfH + cfg.recycleZoneHeight;

            var go = new GameObject("RecycleZone");
            go.transform.SetParent(transform, false);

            var col = go.AddComponent<BoxCollider2D>();
            col.offset = new Vector2(0f, zoneCenterY);
            col.size = new Vector2(cfg.fieldWidth, cfg.recycleZoneHeight);
            col.isTrigger = true;
            var zone = go.AddComponent<RecycleZone>();

            var glow = NeonStyle.MakeSprite(go.transform, "Glow", ProcSprites.Glow(), NeonStyle.Amber, 4, true);
            glow.color = new Color(NeonStyle.Amber.r, NeonStyle.Amber.g, NeonStyle.Amber.b, 0.12f);
            glow.transform.localPosition = new Vector3(0f, lineY, 0f);
            glow.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            glow.transform.localScale = new Vector3(0.3f, cfg.fieldWidth / 4f, 1f);

            var line = NeonStyle.MakeSprite(go.transform, "Line", ProcSprites.SquareFill(), NeonStyle.Amber, 5, false);
            line.transform.localPosition = new Vector3(0f, lineY, 0f);
            line.transform.localScale = new Vector3(cfg.fieldWidth, 0.06f, 1f);

            zone.SetVisual(line);
        }
    }
}
