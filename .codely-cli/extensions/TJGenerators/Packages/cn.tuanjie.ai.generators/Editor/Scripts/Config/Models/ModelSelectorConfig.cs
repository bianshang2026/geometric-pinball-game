using System;
using System.Collections.Generic;
using UnityEngine;

namespace TJGenerators.Config
{
    [Serializable]
    public class ModelSelectorConfig
    {
        public string name;
        public string description;
        public List<string> functionTags;
        public List<string> vendorTags;
        public string iconPath;
        public bool pinned = false;
    }

    /// <summary>
    /// AI模型信息
    /// </summary>
    public class AIModelInfo
    {
        public string Id;
        public string Name;
        public string Description;
        public string[] FunctionTags;
        public string[] VendorTags;
        public Texture2D Icon;
        public bool IsPinned;
        public DateTime LastUsed;
        public int ConfigOrder;
    }

    [Serializable]
    internal class TJGeneratorsModelPreferenceCollection
    {
        public List<TJGeneratorsModelPreferenceItem> items = new List<TJGeneratorsModelPreferenceItem>();
    }

    [Serializable]
    internal class TJGeneratorsModelPreferenceItem
    {
        public string id;
        public bool isPinned;
        public long lastUsedTicks;
    }
}
