using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using LiveSplit.Bridge.Json;

if (args.Length > 0 && args[0] == "--samples")
{
    var sampleDirectory = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "out");
    Directory.CreateDirectory(sampleDirectory);
    ContractSamples.Write(sampleDirectory);
    return;
}

const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";

var jsonOptions = BridgeJsonOptions.Create();

var exporterOptions = new JsonSchemaExporterOptions
{
    TreatNullObliviousAsNonNullable = true,
    TransformSchemaNode = (context, schema) =>
    {
        var property = context.PropertyInfo;
        if (property != null && schema is JsonObject objectSchema)
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (type == typeof(uint))
            {
                objectSchema["minimum"] = 0;
                objectSchema["maximum"] = uint.MaxValue;
            }
            else if (type == typeof(int))
            {
                objectSchema["minimum"] = int.MinValue;
                objectSchema["maximum"] = int.MaxValue;
            }
            else if (type == typeof(long))
            {
                objectSchema["minimum"] = long.MinValue;
                objectSchema["maximum"] = long.MaxValue;
            }

            if (property.Name is "session_id" or "event_sequence")
            {
                objectSchema["minimum"] = 1;
            }

            if (property.PropertyType == typeof(byte[]))
            {
                objectSchema["contentEncoding"] = "base64";
                objectSchema["contentMediaType"] = "image/png";
            }
        }

        var attributeProvider = property?.AttributeProvider as ICustomAttributeProvider;
        if (attributeProvider?.GetCustomAttributes(inherit: true).OfType<SchemaConstNullAttribute>().Any() == true)
        {
            if (schema is JsonObject resultSchema) resultSchema["const"] = null;
            else schema = new JsonObject { ["const"] = null };
        }
        return schema;
    },
};

var outputDirectory = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "out");
Directory.CreateDirectory(outputDirectory);
foreach (var name in new[] { "rpc-request.schema.json", "rpc-response.schema.json", "bridge-event.schema.json" })
    WriteSchema(name);

void WriteSchema(string fileName)
{
    Type type = fileName switch
    {
        "rpc-request.schema.json" => typeof(RpcRequest),
        "rpc-response.schema.json" => typeof(RpcResponse),
        _ => typeof(BridgeEvent),
    };
    var node = jsonOptions.GetJsonSchemaAsNode(type, exporterOptions);
    if (node is JsonObject root) root["$schema"] = Draft202012;
    File.WriteAllText(Path.Combine(outputDirectory, fileName), node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
}
