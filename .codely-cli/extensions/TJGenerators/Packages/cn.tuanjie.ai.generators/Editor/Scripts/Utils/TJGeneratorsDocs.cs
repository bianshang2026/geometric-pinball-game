using UnityEngine;

namespace TJGenerators.Utils
{
    /// <summary>
    /// TJGenerators 使用文档入口统一封装：菜单、窗口标题栏帮助图标、空状态软引导等均调用此处。
    /// </summary>
    public static class TJGeneratorsDocs
    {
        /// <summary>AI 生成工具使用文档地址。</summary>
        public const string DocumentationUrl = "https://codely-docs.tuanjie.cn/ai-generation-tools/play-guide";
        public const string DocumentationUrlEn = "https://codely-docs.tuanjie.cn/en/ai-generation-tools/play-guide/";

        /// <summary>
        /// 在默认浏览器中打开使用文档。
        /// </summary>
        public static void OpenDocumentation()
        {
            Application.OpenURL(TJGeneratorsL10n.IsEnglish ? DocumentationUrlEn : DocumentationUrl);
        }
    }
}
