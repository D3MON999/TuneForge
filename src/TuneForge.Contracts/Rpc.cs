using System.Text.Json;
using System.Text.Json.Serialization;

namespace TuneForge.Contracts;

/// <summary>Names of every method the engine accepts. Anything else is rejected.</summary>
public static class RpcMethods
{
    public const string Ping = "engine.ping";
    public const string Shutdown = "engine.shutdown";
}

public static class RpcErrorCodes
{
    public const int ParseError = -32700;
    public const int MethodNotFound = -32601;
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
    };
}
