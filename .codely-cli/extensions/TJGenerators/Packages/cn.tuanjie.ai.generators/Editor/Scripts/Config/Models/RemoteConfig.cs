using System;
using System.Collections.Generic;

namespace TJGenerators.Config
{
    [Serializable]
    public class RemoteConfig
    {
        public string version;
        public string apiBaseUrl;
        public string codelyBaseUrl;
        public string debugApiBaseUrl;
        public PollConfig pollConfig;
        public GlobalEndpointsConfig globalEndpoints;
        public RequestHeadersConfig requestHeaders;

        public List<GeneratorConfig> generators;
        public List<ImageGeneratorConfig> referenceImageGenerators;
        public List<GeneratorConfig> imageGenerators;
        public List<GeneratorConfig> skyboxGenerators;
        public List<GeneratorConfig> spriteGenerators;
        public List<GeneratorConfig> materialGenerators;
        public List<GeneratorConfig> musicGenerators;
        public List<GeneratorConfig> videoGenerators;
        public List<GeneratorConfig> spriteSequenceGenerators;
        public List<GeneratorConfig> worldGenerators;

        public TypeSelectorConfig spriteTypeSelector;
        public StyleSelectorConfig spriteStyleSelector;
        public string defaultModelId;
    }

    [Serializable]
    public class GlobalEndpointsConfig
    {
        public string userInfo = "user/me";
        public string pollStatus = "task/{taskId}/id-status";
    }

    [Serializable]
    public class PollConfig
    {
        public int maxRetries = 360;
        public float intervalSeconds = 8f;
        public float requestTimeoutSeconds = 30f;
        public float downloadTimeoutSeconds = 300f;
        public float apiTimeoutSeconds = 60f;
    }

    [Serializable]
    public class RequestHeadersConfig
    {
        public string source = "codely";
    }
}
