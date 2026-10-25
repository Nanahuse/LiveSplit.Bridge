using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;
using Json.Schema.Generation;
using Json.Schema.Generation.DataAnnotations;
using Json.Schema.Generation.Intents;
using LiveSplit.Bridge.Json;

var outputDirectory = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "out");
Directory.CreateDirectory(outputDirectory);
DataAnnotationsSupport.AddDataAnnotations();
var dialect = new Uri("https://json-schema.org/draft/2020-12/schema");
var configuration = new SchemaGeneratorConfiguration
{
    DefaultDialect = dialect,
    PropertyNameResolver = PropertyNameResolvers.LowerSnakeCase,
    TypeNameGenerator = new DefinitionNameGenerator(),
};
configuration.Refiners.Add(new NullableDtoReferenceRefiner());
var families = new (string File, string RootName, Type[] Types)[]
{
    ("rpc-request.schema.json", "RpcRequest", [typeof(QueryRequest), typeof(TimerRequest), typeof(GameTimeRequest<GameControlParams>), typeof(GameTimeRequest<GameSetParams>)]),
    ("rpc-response.schema.json", "RpcResponse", [typeof(Success<TimerState>), typeof(Success<RunState>), typeof(Success<RunIcons>), typeof(Success<AttemptState>), typeof(Success<ContextState>), typeof(Success<CompletedCount>), typeof(ControlSuccessResponse), typeof(ErrorResponse)]),
    ("bridge-event.schema.json", "BridgeEvent", [typeof(TimerEvent), typeof(ResetEvent), typeof(ChangedEvent)]),
};
foreach (var (file, rootName, types) in families)
{
    // FromType sets the thread-static configuration and clears the generation cache.
    _ = new JsonSchemaBuilder().FromType(typeof(SchemaGenerationSeed), configuration);

    // Generate each reachable DTO through the cache so all roots share the same
    // named definitions instead of inlining the types separately per root.
    var definitions = GetDtoTypes(types)
        .OrderBy(SchemaNames.DefinitionName, StringComparer.Ordinal)
        .ToDictionary(SchemaNames.DefinitionName, CreateDefinition, StringComparer.Ordinal);
    var schema = new JsonSchemaBuilder()
        .Schema(dialect)
        .Title(rootName)
        .OneOf(types.Select(type => new JsonSchemaBuilder().Ref($"#/$defs/{SchemaNames.DefinitionName(type)}")))
        .Defs(definitions)
        .Build();
    File.WriteAllText(Path.Combine(outputDirectory, file),
        JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }) + "\n");
}

static IEnumerable<Type> GetDtoTypes(IEnumerable<Type> roots)
{
    var pending = new Stack<Type>(roots);
    var seen = new HashSet<Type>();
    while (pending.Count > 0)
    {
        var type = pending.Pop();
        if (!seen.Add(type) || type == typeof(string) || type == typeof(object))
        {
            continue;
        }

        if (type.IsArray)
        {
            pending.Push(type.GetElementType()!);
            yield return type;
            continue;
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                pending.Push(argument);
            }

            if (type.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                continue;
            }

            if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            {
                yield return type;
                continue;
            }
        }

        if (type.IsEnum)
        {
            yield return type;
            continue;
        }

        if (type.Namespace != typeof(QueryRequest).Namespace || !type.IsClass || type.IsAbstract)
        {
            continue;
        }

        yield return type;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            pending.Push(property.PropertyType);
        }
    }
}

static JsonSchemaBuilder CreateDefinition(Type type)
{
    if (!type.IsEnum)
    {
        return SchemaGenerationContextCache.Get(type).Apply();
    }

    // Generation 7.3.11 emits string-valued enums without a type keyword.
    // Add the CLR string type explicitly so downstream generators can use it.
    var values = Enum.GetValues(type).Cast<object>().Select(value =>
    {
        var member = type.GetField(value.ToString()!);
        return member?.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? value.ToString()!;
    });
    return new JsonSchemaBuilder().Type(SchemaValueType.String).Enum(values);
}

static class SchemaNames
{
    public static string DefinitionName(Type type)
    {
        // The public contract names these discriminator enums after the JSON `type`
        // field rather than the C# `Kind` suffix.
        if (type == typeof(TimerEventKind))
        {
            return "TimerEventType";
        }

        if (type == typeof(ChangedEventKind))
        {
            return "ChangedEventType";
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var arguments = type.GetGenericArguments();
        if (type.GetGenericTypeDefinition() == typeof(Success<>))
        {
            return DefinitionName(arguments[0]) + "Response";
        }

        if (type.GetGenericTypeDefinition() == typeof(GameTimeRequest<>))
        {
            return arguments[0].Name switch
            {
                nameof(GameControlParams) => "GameControlRequest",
                nameof(GameSetParams) => "GameSetRequest",
                _ => DefinitionName(arguments[0]) + "GameTimeRequest",
            };
        }

        return type.Name[..type.Name.IndexOf('`')] + "Of" + string.Join("And", arguments.Select(DefinitionName));
    }
}

sealed class DefinitionNameGenerator : ITypeNameGenerator
{
    public string GenerateName(Type type) => SchemaNames.DefinitionName(type);
}

// Generation 7.3.11 inlines a nullable reference property instead of attaching
// null to a $ref. Rebuild those properties as anyOf[$ref, null] so clients keep
// the named DTO and its nullability at the same time.
sealed class NullableDtoReferenceRefiner : ISchemaRefiner
{
    public bool ShouldRun(SchemaGenerationContextBase context)
        => context is MemberGenerationContext member
           && IsNullable(member)
           && IsNamedDto(member.Type);

    public void Run(SchemaGenerationContextBase context)
    {
        var member = (MemberGenerationContext)context;
        var reference = new Uri($"#/$defs/{SchemaNames.DefinitionName(member.Type)}", UriKind.Relative);
        context.Intents.RemoveAll(intent => intent is TypeIntent or PropertiesIntent or RequiredIntent
            or AdditionalPropertiesIntent or IfIntent or ThenIntent or AllOfIntent or AnyOfIntent
            or RefIntent or DefsIntent);
        context.Intents.Add(new AnyOfIntent(
            [new RefIntent(reference)],
            [new TypeIntent(SchemaValueType.Null)]));
    }

    private static bool IsNullable(MemberGenerationContext member)
        => member.NullableRef
           || member.Attributes.OfType<NullableAttribute>()
               .Any(attribute => attribute.GenericParameter < 0 && attribute.IsNullable);

    private static bool IsNamedDto(Type type)
        => type != typeof(string)
           && type.Namespace == typeof(QueryRequest).Namespace
           && type.IsClass
           && !type.IsAbstract;
}

sealed class SchemaGenerationSeed { }
