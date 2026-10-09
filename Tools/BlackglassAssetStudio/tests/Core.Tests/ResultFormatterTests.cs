using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class ResultFormatterTests
{
    [Fact]
    public void Report_shows_assets_scale_clips_warnings_errors_and_log()
    {
        var r = new ImportResult { runId = "r", unityVersion = "6000.3.25f1", success = false };
        var ok = new ItemResult { id = "a", success = true, measuredHeight = 2.4f, targetHeight = 1.85f, appliedScale = 0.7708f, scaleSource = ScaleSources.Auto };
        ok.importedAssets.Add("Assets/Art/K.fbx");
        ok.createdPrefabs.Add("Assets/Art/K_Visual.prefab");
        ok.avatar = new AvatarReport { valid = true, isHuman = true };
        ok.warnings.Add("stray geometry");
        ok.clips.Add(new ClipReport { name = "Walk", duration = 1.03f, loop = true, bakeHeight = true });
        var bad = new ItemResult { id = "b" };
        bad.errors.Add("Avatar is not humanoid");
        r.items.Add(ok); r.items.Add(bad);

        var text = ResultFormatter.Format(r, id => id == "a" ? "Kestrel" : "Other", new[] { "new: Assets/Art/K.fbx" }, "C:\\logs\\unity.log");

        Assert.Contains("Kestrel", text);
        Assert.Contains("Assets/Art/K_Visual.prefab", text);
        Assert.Contains("2.400", text);
        Assert.Contains("0.7708", text);
        Assert.Contains("Auto", text);
        Assert.Contains("Walk", text);
        Assert.Contains("stray geometry", text);
        Assert.Contains("Avatar is not humanoid", text);
        Assert.Contains("new: Assets/Art/K.fbx", text);
        Assert.Contains("C:\\logs\\unity.log", text);
        Assert.Contains("FAILED", text);
    }
}
