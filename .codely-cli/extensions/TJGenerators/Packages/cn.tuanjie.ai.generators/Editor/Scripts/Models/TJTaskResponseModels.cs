#if UNITY_EDITOR
using System;

namespace TJGenerators
{
    /// <summary>
    /// 平台任务创建/状态响应数据类（JSON 反序列化），供 GenerationPipeline、DynamicGenerator 等使用。
    /// </summary>
    [Serializable]
    public class TJTaskResponse
    {
        public string message;
        public string status;
        public string taskId;
    }

    [Serializable]
    public class TJTaskStatusResponse
    {
        public long createTime;
        public long endTime;
        public TJTaskInput input;
        public TJTaskOutput output;
        public int progress;
        public long startTime;
        public string status;
        public string taskId;
        public string type;
        public string error;
        public string errorCode;
        public string message;
    }

    [Serializable]
    public class TJTaskInput
    {
    }

    [Serializable]
    public class TJTaskOutput
    {
        public TJTaskOutputData data;
    }

    [Serializable]
    public class TJTaskOutputData
    {
        /// <summary>嵌套结构：output.data.result（模型/多图等）</summary>
        public TJTaskResult result;

        public string[] imageUrls;

        /// <summary>扁平结构：后端直接返回 output.data = { audio_url, duration, ... }（文生音频等）</summary>
        public string audio_url;
        public string audioUrl;
        /// <summary>混元3.1 等：后端直接返回 output.data.resultFiles（与 result.resultFiles 二选一）</summary>
        public TencentResultFile3D[] resultFiles;
        public float duration;
        public string genre;
        public string lyrics;
        public string mood;

        /// <summary>Voice clone output: the cloned voice ID for use with generate_tts.</summary>
        public string customVoiceId;
        /// <summary>Voice clone output: preview audio URL.</summary>
        public string previewAudioUrl;

        /// <summary>WorldLabs 世界生成输出：output.data.assets</summary>
        public WorldAssets assets;
    }

    /// <summary>
    /// WorldLabs Marble 世界生成输出资产
    /// </summary>
    [Serializable]
    public class WorldAssets
    {
        public string thumbnailUrl;
        public string caption;
        public string panoUrl;
        public string colliderMeshUrl;
        public SpzUrls spzUrls;
    }

    /// <summary>
    /// Gaussian Splatting 数据文件 URL（不同精度级别）
    /// </summary>
    [Serializable]
    public class SpzUrls
    {
        public string full_res;
        public string _100k;
        public string _150k;
        public string _500k;
    }

    [Serializable]
    public class TJTaskResult
    {
        // Tripo/Rodin 通用字段
        public string generated_image;
        public string pbr_model;
        public string base_model;
        public string rendered_image;
        public string model;
        public string @base;
        public string base_basic_pbr;
        public string base_basic_shaded; // Rodin
        public string preview;
        public string render; // Rodin预览图（jpg格式）
        public string shaded; // Rodin
        public string texture_diffuse;
        public string texture_metallic;
        public string texture_normal;
        public string texture_pbr;
        public string texture_roughness;

        // 天空盒（Rodin Skybox 等）
        public string skybox_basic;

        // sprite（Sprite Generator 等）
        public string[] image_urls;

        // fal 生图 / frontier-game-design（部分响应嵌套在 result 下）
        public string[] imageUrls;

        // seedance 2.0
        public string last_frame_url;
        public string video_url;

        // UniRig 绑骨结果
        public string model_url;

        // 混元 Motion 动画结果
        public string[] urls;

        // 混元3.1（tencent-generation）
        public TencentResultFile3D[] resultFiles;

        public string thumbnail_url;
        public string preview_url;
        public int seed;
    }

    [Serializable]
    public class TencentResultFile3D
    {
        public string type; // FBX / OBJ / STL ...
        public string url;
        public string previewImageUrl;
    }
}
#endif
