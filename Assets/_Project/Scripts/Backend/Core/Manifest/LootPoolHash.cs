using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Blackglass.Backend.Core
{
    /// <summary>
    /// The registered loot pool's hash, a port of Backend LootHash.Compute (ReadyReport.cs): "blackglass-loot-pool/1\n",
    /// "table=&lt;version&gt;\n", then "&lt;containerIndex&gt;,&lt;line&gt;,&lt;definitionId&gt;,&lt;quantity&gt;\n" per line sorted by
    /// (containerIndex, line), in invariant decimal, hashed as UTF-8.
    /// </summary>
    public static class LootPoolHash
    {
        public static string Compute(int lootTableVersion, IReadOnlyList<PoolLine> pool)
        {
            var text = new StringBuilder("blackglass-loot-pool/1\n");
            text.Append("table=").Append(lootTableVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var l in pool.OrderBy(l => l.ContainerIndex).ThenBy(l => l.Line))
                text.Append(l.ContainerIndex.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(l.Line.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(l.DefinitionId).Append(',')
                    .Append(l.Quantity.ToString(CultureInfo.InvariantCulture)).Append('\n');
            return ManifestCodec.HashOf(new UTF8Encoding(false).GetBytes(text.ToString()));
        }
    }
}
