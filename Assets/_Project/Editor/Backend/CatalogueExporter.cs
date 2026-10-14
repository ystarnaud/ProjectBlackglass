using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Blackglass.BackendTools
{
    /// <summary>
    /// Writes Backend/catalogue/catalogue.json from the project's authored data (Blackglass > Backend > Export Catalogue,
    /// or Tools/run-method.sh Blackglass.BackendTools.CatalogueExporter.ExportFromCommandLine with the Editor closed).
    /// Nothing is written when CatalogueBuilder refuses; every problem is logged. CatalogueExportTests fails while the
    /// committed file differs from a fresh export, and the backend refuses a file that isn't in canonical form.
    /// </summary>
    public static class CatalogueExporter
    {
        public static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        public static string CommittedPath => Path.Combine(ProjectRoot, "Backend", "catalogue", "catalogue.json");

        /// <summary>The canonical bytes of a fresh export, or null with every problem added to errors.</summary>
        public static byte[] Export(CatalogueSources sources, List<string> errors)
        {
            var document = CatalogueBuilder.Build(sources, errors);
            return document == null ? null : CanonicalCatalogueWriter.ToBytes(document);
        }

        [MenuItem("Blackglass/Backend/Export Catalogue")]
        static void ExportMenu()
        {
            if (TryExportProject(out var message))
                Debug.Log(message);
            else
                Debug.LogError(message);
        }

        /// <summary>Unity -batchmode -executeMethod entry: exit 0 after writing the file, 1 when the export is refused.</summary>
        public static void ExportFromCommandLine()
        {
            var ok = TryExportProject(out var message);
            if (ok)
                Debug.Log(message);
            else
                Debug.LogError(message);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        static bool TryExportProject(out string message)
        {
            var errors = new List<string>();
            var bytes = Export(CatalogueSources.LoadProject(), errors);
            if (bytes == null)
            {
                message = "Catalogue export refused; nothing was written:\n" + string.Join("\n", errors);
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(CommittedPath));
            var temporary = CommittedPath + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(CommittedPath))
                File.Replace(temporary, CommittedPath, null);
            else
                File.Move(temporary, CommittedPath);
            message = $"Catalogue exported to {CommittedPath}: {CanonicalCatalogueWriter.VersionOf(bytes)}";
            return true;
        }
    }
}
