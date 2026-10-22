#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using Json = LiveSplit.Bridge.Json;

namespace LiveSplit.Bridge;

internal static class JsonWebSocketRpcHandler
{
    internal static Json.RpcResponse? Handle(
        byte[] utf8,
        Func<Json.RpcRequest?, bool, Json.RpcResponse> requestHandler,
        Func<uint, string, Json.RpcResponse> invalidRequest,
        bool allowControl,
        JsonSerializerOptions options)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(utf8); }
        catch (JsonException) { return null; }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!TryReadRequestId(root, out var requestId)) return null;

            if (HasDuplicateProperties(root))
                return invalidRequest(requestId, "Duplicate JSON properties are not allowed.");

            try
            {
                if (!root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
                    return invalidRequest(requestId, "Request method is required.");
                // STJ also accepts comma-separated names for non-flags enums; RPC selects one action.
                var methodName = method.GetString()!;
                if (methodName.IndexOf(',') >= 0 || (root.TryGetProperty("params", out var arguments)
                    && arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("operation", out var operation)
                    && operation.ValueKind == JsonValueKind.String && operation.GetString()!.IndexOf(',') >= 0))
                    return invalidRequest(requestId, "A request must name a single method and operation.");
                Json.RpcRequest? request = methodName switch
                {
                    Json.TimerRequest.MethodName => root.Deserialize<Json.TimerRequest>(options),
                    Json.GameTimeRequest<Json.GameControlParams>.MethodName => IsGameTimeSet(root)
                        ? root.Deserialize<Json.GameTimeRequest<Json.GameSetParams>>(options)
                        : root.Deserialize<Json.GameTimeRequest<Json.GameControlParams>>(options),
                    _ => root.Deserialize<Json.QueryRequest>(options),
                };
                if (request == null) return invalidRequest(requestId, "Request object is required.");
                return requestHandler(request, allowControl);
            }
            catch (JsonException exception)
            {
                return invalidRequest(requestId, exception.Message);
            }
            catch (NotSupportedException exception)
            {
                return invalidRequest(requestId, exception.Message);
            }
        }
    }

    private static bool IsGameTimeSet(JsonElement request) =>
        request.TryGetProperty("params", out var arguments) && arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty("operation", out var operation) && operation.ValueKind == JsonValueKind.String
        && operation.GetString() == Json.GameSetParams.OperationName;

    private static bool TryReadRequestId(JsonElement root, out uint requestId)
    {
        requestId = 0;
        var found = false;
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, "request_id", StringComparison.Ordinal)) continue;
            if (found || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetUInt32(out requestId))
                return false;
            found = true;
        }
        return found;
    }

    private static bool HasDuplicateProperties(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
                }
                break;
            case JsonValueKind.Array:
                foreach (var element in value.EnumerateArray())
                    if (HasDuplicateProperties(element)) return true;
                break;
        }
        return false;
    }
}
