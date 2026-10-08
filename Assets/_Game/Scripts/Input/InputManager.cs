using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 输入抽象（架构文档 §12）：Mouse/Touch 统一为指针语义，
    /// Gameplay 层禁止直接读 UnityEngine.Input —— 一切经本类。
    /// 测试/回放可注入模拟指针（Simulate），不依赖真输入设备。
    /// </summary>
    public class InputManager : MonoBehaviour
    {
        public static InputManager I { get; private set; }

        Camera _cam;

        // 模拟输入状态（测试注入）
        bool _simActive;
        bool _simPressedDown, _simPressed;
        Vector2 _simWorld;

        public void Init(Camera cam)
        {
            I = this;
            _cam = cam;
        }

        void OnDestroy() { if (I == this) I = null; }

        /// <summary>本帧按下（触摸存在或鼠标按下——语义与 P3 前旧实现完全一致）。</summary>
        public static bool PressedDown()
        {
            if (I != null && I._simActive) return I._simPressedDown;
            return Input.touchCount > 0 || Input.GetMouseButtonDown(0);
        }

        /// <summary>按住中（任一触摸或鼠标左键）。</summary>
        public static bool Pressed()
        {
            if (I != null && I._simActive) return I._simPressed;
            return Input.touchCount > 0 || Input.GetMouseButton(0);
        }

        /// <summary>当前指针世界坐标（触摸优先；模拟态直接返回注入值）。</summary>
        public static Vector2 PointerWorld()
        {
            if (I == null) return Vector2.zero;
            if (I._simActive) return I._simWorld;
            Vector2 sp = Input.touchCount > 0
                ? (Vector2)Input.GetTouch(0).position
                : (Vector2)Input.mousePosition;
            return I._cam.ScreenToWorldPoint(new Vector3(sp.x, sp.y, -I._cam.transform.position.z));
        }

        // ---------------- 模拟注入（测试/回放专用） ----------------

        /// <summary>注入一帧模拟输入：pressedDown=本帧按下，pressed=按住，worldPos=指针世界坐标。</summary>
        public static void Simulate(bool pressedDown, bool pressed, Vector2 worldPos)
        {
            if (I == null) return;
            I._simActive = true;
            I._simPressedDown = pressedDown;
            I._simPressed = pressed;
            I._simWorld = worldPos;
        }

        /// <summary>结束模拟，恢复真实输入。</summary>
        public static void EndSimulation()
        {
            if (I != null) I._simActive = false;
        }
    }
}
