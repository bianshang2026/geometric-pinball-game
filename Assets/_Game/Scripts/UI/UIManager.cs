namespace GeoBreaker
{
    /// <summary>
    /// UI 路由（架构 §20）：大厅与子页互斥切换的轻量转发层。
    /// 单场景全 Panel 架构——战斗区常驻，页面即 Panel；后续可扩展页面栈。
    /// </summary>
    public static class UIManager
    {
        public static void OpenLobby() => MainMenuScreen.I?.OpenRoot();
        public static void OpenMap() => MapScreen.I?.Open();
        public static void OpenMapAllowRetry() => MapScreen.I?.OpenAllowRetry();
        public static void CloseMap() => MapScreen.I?.Close();
    }
}
