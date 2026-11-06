using System.Text.Json;
using System.Text.Json.Serialization;

namespace TuneForge.Contracts;

/// <summary>Names of every method the engine accepts. Anything else is rejected.</summary>
public static class RpcMethods
{
    public const string Ping = "engine.ping";
    public const string Shutdown = "engine.shutdown";
    public const string ApplyTweaks = "tweaks.apply";
    public const string Undo = "history.undo";
    public const string RemoveLock = "tweaks.removeLock";
    public const string Cleanup = "cleanup.run";
    public const string StartupSet = "startup.set";
    public const string ServiceSet = "service.set";
    public const string FeatureSet = "feature.set";
    public const string Repair = "repair.run";
    public const string BuiltInRemove = "builtin.remove";
    public const string DnsSet = "dns.set";
    public const string AdvancedSet = "advanced.set";
    public const string SecuritySet = "security.set";
    public const string BandwidthSet = "bandwidth.set";
    public const string BandwidthRemove = "bandwidth.remove";
    public const string BiosRestart = "bios.restart";
    public const string BiosCancel = "bios.cancel";
}

public static class RpcErrorCodes
{
    public const int ParseError = -32700;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

public sealed record RpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement? Params = null);

public sealed record RpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message);

public sealed record RpcResponse(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("result")] JsonElement? Result,
    [property: JsonPropertyName("error")] RpcError? Error);

public sealed record PingResult(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("engineVersion")] string EngineVersion,
    [property: JsonPropertyName("elevated")] bool Elevated,
    [property: JsonPropertyName("processId")] int ProcessId);

public static class RpcProtocol
{
    public const string Version = "2.0";
    public const int ProtocolVersion = 1;

    /// <summary>Largest accepted message, in bytes, excluding the line ending.</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
