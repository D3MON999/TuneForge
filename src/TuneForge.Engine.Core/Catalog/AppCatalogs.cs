using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TuneForge.Engine.Core.Catalog;

public sealed record BuiltInApp(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("note")] string Note,
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("safety")] string Safety,
    [property: JsonPropertyName("storeId")] string? StoreId);

public sealed record SoftwareItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("note")] string Note,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("choco")] string? Choco = null);

/// <summary>The fixed app lists built into the app. Callers can only name items in them.</summary>
public static class AppCatalogs
{
    private sealed record Document<T>([property: JsonPropertyName("items")] IReadOnlyList<T> Items);

    public static IReadOnlyList<BuiltInApp> LoadBuiltIn() => Load<BuiltInApp>("TuneForge.BuiltInApps.json");

    public static IReadOnlyList<SoftwareItem> LoadSoftware() => Load<SoftwareItem>("TuneForge.Software.json");

    private static IReadOnlyList<T> Load<T>(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidDataException($"{resource} is missing.");
        return JsonSerializer.Deserialize<Document<T>>(stream)?.Items
            ?? throw new InvalidDataException($"{resource} is empty.");
    }
}
