#if UNITY_EDITOR
using Blackglass.AssetPipeline;

namespace Blackglass.Tests.AssetPipeline
{
    internal static class ImporterTestSupport
    {
        public static ImportItem Item(string profile, string sourcePath, string name, string destination = ScratchFolder.Path)
        {
            return new ImportItem { id = name, profile = profile, name = name, sourcePath = sourcePath, destinationFolder = destination };
        }

        public static string Errors(ItemResult r) => string.Join("\n", r.errors);
    }
}
#endif
