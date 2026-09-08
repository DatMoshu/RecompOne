namespace RecompOne.Runtime.Assets;

/// <summary>One browsable thing: a disc range, an archive entry, a model, an animation,
/// a texture page. Everything is lazy so registering ten thousand rooms costs nothing until
/// one is expanded. A node may supply any subset of: children, key/value details, raw
/// bytes (the Asset Viewer offers to export them) and an RGBA image preview.</summary>
public sealed class AssetNode(string name, string? summary = null)
{
    public string Name { get; } = name;
    public string? Summary { get; } = summary;
    public Func<IReadOnlyList<AssetNode>>? Children { get; init; }
    public Func<IEnumerable<(string Key, string Value)>>? Details { get; init; }
    public Func<byte[]?>? Bytes { get; init; }
    /// <summary>(width, height, RGBA8 bytes). Decoded on demand; the panel caches the upload.</summary>
    public Func<(int W, int H, byte[] Rgba)?>? Image { get; init; }
    /// <summary>Suggested file name for the byte export (no directory).</summary>
    public string? ExportName { get; init; }

    IReadOnlyList<AssetNode>? _children;
    public IReadOnlyList<AssetNode> GetChildren()
    {
        if (_children != null) return _children;
        try { _children = Children?.Invoke() ?? []; }
        catch (Exception e) { _children = [new AssetNode("(error)", e.Message)]; }
        return _children;
    }
    public bool HasChildren => Children != null;
}

/// <summary>Game-agnostic registry the Asset Viewer panel renders. A port registers its
/// roots at startup (lambdas only, no I/O) and the panel does the rest: tree, details, hex
/// preview, image preview, export. Nothing in the engine knows what a "room" or an
/// "outfit" is — those names come from the port.</summary>
public static class AssetCatalog
{
    static readonly List<AssetNode> _roots = [];
    public static IReadOnlyList<AssetNode> Roots => _roots;
    public static void Register(AssetNode root) => _roots.Add(root);
    public static void Clear() => _roots.Clear();
}
