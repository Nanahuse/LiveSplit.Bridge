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
                var request = JsonSerializer.Deserialize<Json.RpcRequest>(utf8, options);
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
