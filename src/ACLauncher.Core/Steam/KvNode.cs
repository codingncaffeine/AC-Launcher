namespace ACLauncher.Core.Steam;

/// <summary>
/// One node of a Valve KeyValues tree (text <c>.vdf</c>/<c>.acf</c> or binary <c>shortcuts.vdf</c>).
/// A node is either a section holding children or a leaf holding a string or an integer.
/// Steam treats key names case-insensitively, and so does every lookup here.
/// </summary>
public sealed class KvNode
{
    private readonly List<KvNode> _children = [];

    private KvNode(string name, bool isSection, string? text, long? integer)
    {
        Name = name;
        IsSection = isSection;
        Text = text;
        Integer = integer;
    }

    public string Name { get; }
    public bool IsSection { get; }
    public string? Text { get; }
    public long? Integer { get; }
    public IReadOnlyList<KvNode> Children => _children;

    public static KvNode Section(string name) => new(name, true, null, null);
    public static KvNode Leaf(string name, string text) => new(name, false, text, null);
    public static KvNode Leaf(string name, long integer) => new(name, false, null, integer);

    internal void Add(KvNode child) => _children.Add(child);

    /// <summary>The first child with this name, or null.</summary>
    public KvNode? this[string name] =>
        _children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Follows a path of child names, e.g. <c>Find("libraryfolders", "0", "path")</c>.</summary>
    public KvNode? Find(params string[] path)
    {
        KvNode? node = this;
        foreach (var part in path)
        {
            node = node[part];
            if (node is null) return null;
        }
        return node;
    }

    /// <summary>A leaf's value as text; integers are formatted invariantly.</summary>
    public string? GetString(string name)
    {
        var child = this[name];
        if (child is null || child.IsSection) return null;
        return child.Text ?? child.Integer?.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A leaf's value as an integer; numeric text is parsed.</summary>
    public long? GetInteger(string name)
    {
        var child = this[name];
        if (child is null || child.IsSection) return null;
        if (child.Integer is { } i) return i;
        return long.TryParse(child.Text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}
