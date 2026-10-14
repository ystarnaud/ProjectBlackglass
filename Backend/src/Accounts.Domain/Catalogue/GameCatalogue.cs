namespace Blackglass.Accounts.Domain.Catalogue;

/// <summary>
/// A validated catalogue and its catalogueVersion. Load accepts only the bytes Unity's exporter writes: no BOM, no CR,
/// strictly the known structure, every semantic rule, and byte-for-byte equal to the canonical form of what was read (so
/// spacing, member order, number forms and duplicate members can't vary). Version is "sha256:" and the lowercase hex
/// SHA-256 of those bytes, the value a mission process must report (spec §4.6).
/// </summary>
public sealed class GameCatalogue
{
    static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    static readonly Lazy<GameCatalogue> embedded = new(() => Load(EmbeddedBytes()));

    readonly Dictionary<string, CatalogueItem> items;
    readonly Dictionary<string, OperativeDefinition> operatives;

    GameCatalogue(string version, CatalogueDocument document)
    {
        Version = version;
        Document = document;
        items = document.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        operatives = document.Operatives.ToDictionary(o => o.Id, StringComparer.Ordinal);
    }

    public string Version { get; }
    public CatalogueDocument Document { get; }

    public CatalogueItem? FindItem(string id) => items.GetValueOrDefault(id);
    public OperativeDefinition? FindOperative(string id) => operatives.GetValueOrDefault(id);

    /// <summary>The catalogue this build embeds (Backend/catalogue/catalogue.json); throws CatalogueException when invalid.</summary>
    public static GameCatalogue Embedded => embedded.Value;

    public static byte[] EmbeddedBytes()
    {
        using var stream = typeof(GameCatalogue).Assembly.GetManifestResourceStream("catalogue.json")
            ?? throw new InvalidOperationException("The embedded catalogue.json resource is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    public static GameCatalogue Load(ReadOnlyMemory<byte> bytes)
    {
        var errors = new List<string>();
        var span = bytes.Span;
        if (span.StartsWith(Utf8Bom)) errors.Add("malformed: the file starts with a byte-order mark");
        if (span.IndexOf((byte)'\r') >= 0) errors.Add("malformed: the file contains a carriage return; it must use LF line ends");
        if (errors.Count > 0) throw new CatalogueException(errors);

        var document = CatalogueReader.Read(bytes, errors) ?? throw new CatalogueException(errors);
        CatalogueValidator.Validate(document, errors);
        if (errors.Count > 0) throw new CatalogueException(errors);
        if (!span.SequenceEqual(CanonicalCatalogueJson.ToBytes(document)))
            throw new CatalogueException(["malformed: the file is not in canonical form (spacing, member order or number form differ); re-export it from Unity"]);
        return new GameCatalogue(CatalogueVersion.Of(span), document);
    }
}
