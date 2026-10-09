namespace Blackglass.AssetStudio;

/// <summary>Rules for editing several selected items at once.</summary>
public static class BatchEdit
{
    /// <summary>The value all items share, or Mixed = true (and the default value) when they differ. No items is not mixed.</summary>
    public static (bool Mixed, T? Value) Common<T>(IEnumerable<T> values)
    {
        var list = values.ToList();
        if (list.Count == 0) return (false, default);
        var first = list[0];
        return list.All(v => EqualityComparer<T>.Default.Equals(v, first)) ? (false, first) : (true, default);
    }
}
