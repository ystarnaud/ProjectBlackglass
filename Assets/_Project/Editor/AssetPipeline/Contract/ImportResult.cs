#nullable disable
using System;
using System.Collections.Generic;

namespace Blackglass.AssetPipeline
{
    [Serializable]
    public class ImportResult
    {
        public int schemaVersion = ContractInfo.SchemaVersion;
        public string runId = "";
        public string unityVersion = "";
        public bool success;
        public List<string> errors = new List<string>();
        public List<string> warnings = new List<string>();
        public List<ItemResult> items = new List<ItemResult>();
    }

    [Serializable]
    public class ItemResult
    {
        public string id = "";
        public bool success;
        public List<string> errors = new List<string>();
        public List<string> warnings = new List<string>();
        public List<string> importedAssets = new List<string>();
        public List<string> createdPrefabs = new List<string>();
        public List<string> changedAssets = new List<string>();
        public Size3 dimensions = new Size3();
        public float measuredHeight;
        public float targetHeight;
        public float appliedScale = 1f;
        public string scaleSource = ScaleSources.None;
        public AvatarReport avatar = new AvatarReport();
        public List<ClipReport> clips = new List<ClipReport>();
        public string registeredInTheme = "";
    }

    [Serializable]
    public class Size3
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    public class AvatarReport
    {
        public bool valid;
        public bool isHuman;
        public string message = "";
    }

    [Serializable]
    public class ClipReport
    {
        public string name = "";
        public float duration;
        public bool loop;
        public bool bakeRotation;
        public bool bakeHeight;
        public bool bakePositionXZ;
    }
}
