using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TuneForge.Engine.Core.Catalog;

public sealed record RegistryAction(
    [property: JsonPropertyName("hive")] string Hive,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string? Kind = null,
    [property: JsonPropertyName("value")] JsonElement? Value = null,
    [property: JsonPropertyName("delete")] bool Delete = false)
{
    public string Display => $@"{Hive}\{Path}\{(Name.Length == 0 ? "(Default)" : Name)}";

    /// <summary>A Windows policy locks a setting. With the value gone, Windows goes back to its own default.</summary>
    public bool IsPolicy =>
        Path.Contains(@"\Policies\", StringComparison.OrdinalIgnoreCase) || Path.StartsWith(@"Policies\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The value this action leaves behind, or null when it removes the value.</summary>
    public RegistryValue? Target
    {
        get
        {
            if (Delete)
            {
                return null;
            }

            var value = Value ?? throw new InvalidDataException($"{Display} has no value.");
            var data = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            return new RegistryValue(Kind ?? throw new InvalidDataException($"{Display} has no kind."), data);
        }
    }
}

public sealed record TweakItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("group")] string? Group,
    [property: JsonPropertyName("risk")] string Risk,
    [property: JsonPropertyName("impact")] string? Impact,
    [property: JsonPropertyName("restart")] string Restart,
    [property: JsonPropertyName("presets")] IReadOnlyList<string> Presets,
    [property: JsonPropertyName("minBuild")] int? MinBuild,
    [property: JsonPropertyName("maxBuild")] int? MaxBuild,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("actions")] IReadOnlyList<RegistryAction> Actions,
    [property: JsonPropertyName("description")] string Description = "",
    [property: JsonPropertyName("hazard")] bool Hazard = false)
{
    public bool RequiresAdmin => Actions.Any(action => action.Hive == "HKLM");

    public bool Supports(int build) => (MinBuild is null || build >= MinBuild) && (MaxBuild is null || build <= MaxBuild);
}

public sealed record TweakGroup(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title);

public sealed record TweakCatalogDocument(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("groups")] IReadOnlyList<TweakGroup> Groups,
    [property: JsonPropertyName("items")] IReadOnlyList<TweakItem> Items);

/// <summary>The fixed list of tweaks built into the app. Callers can only name items in it.</summary>
public sealed class TweakCatalog
{
    private static readonly string[] PresetOrder = ["minimal", "standard", "advanced"];
    private readonly Dictionary<string, TweakItem> _byId;

    public TweakCatalog(TweakCatalogDocument document)
    {
        Document = document;
        _byId = document.Items.ToDictionary(item => item.Id);
    }

    public TweakCatalogDocument Document { get; }

    public IReadOnlyList<TweakItem> Items => Document.Items;

    public static TweakCatalog LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TuneForge.Catalog.json")
            ?? throw new InvalidDataException("The tweak catalog is missing from this build.");
        return new TweakCatalog(JsonSerializer.Deserialize<TweakCatalogDocument>(stream)
            ?? throw new InvalidDataException("The tweak catalog is empty."));
    }

    /// <summary>Returns the items for a request, refusing unknown names and clashing choices.</summary>
    public IReadOnlyList<TweakItem> Resolve(IEnumerable<string> ids)
    {
        var items = new List<TweakItem>();
        var groups = new HashSet<string>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            if (!_byId.TryGetValue(id, out var item))
            {
                throw new CatalogRequestException($"Unknown tweak: {id}");
            }

            if (item.Group is not null && !groups.Add(item.Group))
            {
                throw new CatalogRequestException($"Only one choice is allowed for {item.Group}.");
            }

            items.Add(item);
        }

        return items;
    }

    /// <summary>Presets build on each other, so advanced includes standard and minimal.</summary>
    public IReadOnlyList<string> PresetIds(string preset)
    {
        var index = Array.IndexOf(PresetOrder, preset);
        if (index < 0)
        {
            throw new CatalogRequestException($"Unknown preset: {preset}");
        }

        var included = PresetOrder[..(index + 1)];
        return Items.Where(item => item.Presets.Any(included.Contains)).Select(item => item.Id).ToList();
    }
}

public sealed class CatalogRequestException(string message) : Exception(message);
