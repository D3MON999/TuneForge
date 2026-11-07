using System.Text.Json.Serialization;

namespace TuneForge.Contracts;

public static class TweakStates
{
    public const string Applied = "applied";
    public const string NotApplied = "not-applied";
    public const string Unsupported = "unsupported";
}

public sealed record TweakStatus(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("group")] string? Group,
    [property: JsonPropertyName("risk")] string Risk,
    [property: JsonPropertyName("impact")] string? Impact,
    [property: JsonPropertyName("restart")] string Restart,
    [property: JsonPropertyName("presets")] IReadOnlyList<string> Presets,
    [property: JsonPropertyName("requiresAdmin")] bool RequiresAdmin,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("description")] string Description = "",
    [property: JsonPropertyName("hazard")] bool Hazard = false,
    [property: JsonPropertyName("locked")] bool Locked = false);

public sealed record RemoveLocksRequest(
    [property: JsonPropertyName("ids")] IReadOnlyList<string> Ids);

public sealed record TweakChange(
    [property: JsonPropertyName("setting")] string Setting,
    [property: JsonPropertyName("before")] string Before,
    [property: JsonPropertyName("after")] string After);

public sealed record TweakPreview(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("restart")] string Restart,
    [property: JsonPropertyName("impact")] string? Impact,
    [property: JsonPropertyName("changes")] IReadOnlyList<TweakChange> Changes,
    [property: JsonPropertyName("hazard")] bool Hazard = false);

public sealed record ApplyRequest(
    [property: JsonPropertyName("ids")] IReadOnlyList<string> Ids,
    [property: JsonPropertyName("createRestorePoint")] bool CreateRestorePoint,
    [property: JsonPropertyName("acceptHazards")] bool AcceptHazards = false);

public sealed record TweakResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string? Message);

public sealed record ApplyResult(
    [property: JsonPropertyName("changeSetId")] long ChangeSetId,
    [property: JsonPropertyName("restorePoint")] string RestorePoint,
    [property: JsonPropertyName("results")] IReadOnlyList<TweakResult> Results);

public sealed record UndoRequest(
    [property: JsonPropertyName("changeSetId")] long ChangeSetId);

public sealed record UndoResult(
    [property: JsonPropertyName("changeSetId")] long ChangeSetId,
    [property: JsonPropertyName("restored")] int Restored,
    [property: JsonPropertyName("problems")] IReadOnlyList<string> Problems);
