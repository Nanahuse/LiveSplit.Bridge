using System.Text.Json;
using Json.Schema;
using Json.Schema.Generation;
using Json.Schema.Generation.DataAnnotations;
using LiveSplit.Bridge.Json;

var outputDirectory = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "out");
Directory.CreateDirectory(outputDirectory);
DataAnnotationsSupport.AddDataAnnotations();
var dialect = new Uri("https://json-schema.org/draft/2020-12/schema");
var configuration = new SchemaGeneratorConfiguration
{
    DefaultDialect = dialect,
    PropertyNameResolver = PropertyNameResolvers.LowerSnakeCase,
};
var families = new (string File, Type[] Types)[]
{
    ("rpc-request.schema.json", [typeof(QueryRequest), typeof(TimerRequest), typeof(GameTimeRequest<GameControlParams>), typeof(GameTimeRequest<GameSetParams>)]),
    ("rpc-response.schema.json", [typeof(Success<TimerState>), typeof(Success<RunState>), typeof(Success<AttemptState>), typeof(Success<ContextState>), typeof(Success<CompletedCount>), typeof(ControlSuccessResponse), typeof(ErrorResponse)]),
    ("bridge-event.schema.json", [typeof(TimerEvent), typeof(ResetEvent), typeof(ChangedEvent)]),
};
foreach (var (file, types) in families)
{
    // Each generated root owns its local $defs. Composition uses only standard APIs.
    var children = types.Select((type, index) => new JsonSchemaBuilder()
        .FromType(type, configuration)
        .Id(new Uri($"https://livesplit.org/bridge/v4/{file}/{index}"))).ToArray();
    var schema = new JsonSchemaBuilder().Schema(dialect).OneOf(children).Build();
    File.WriteAllText(Path.Combine(outputDirectory, file),
        JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }) + "\n");
}
