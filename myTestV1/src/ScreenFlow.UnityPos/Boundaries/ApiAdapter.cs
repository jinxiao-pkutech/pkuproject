using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.UnityPos.Boundaries;

/// <summary>The <c>api</c> section of <c>unitypos.json</c> (02 §4.2).</summary>
public sealed record ApiSection
{
    [JsonPropertyName("connectorType")] public required AnchorSpec ConnectorType { get; init; }

    [JsonPropertyName("sendMethods")]
    public IReadOnlyList<string> SendMethods { get; init; } = Array.Empty<string>();

    [JsonPropertyName("requestType")] public AnchorSpec? RequestType { get; init; }

    [JsonPropertyName("apiKeyParamNames")]
    public IReadOnlyList<string> ApiKeyParamNames { get; init; } = Array.Empty<string>();

    [JsonPropertyName("localMockDir")] public string? LocalMockDir { get; init; }
}

/// <summary>
/// Microservice call boundaries (02 §9.8; 03 §18).
/// <para>
/// The service is not in this project, so the chain ends at the request. What a reader needs is
/// the api key it was called with — and whether a local mock answers it, which is the one way to
/// see the response shape without the server.
/// </para>
/// </summary>
public sealed class ApiAdapter : IDomainAdapter
{
    public const string Feature = "api";

    public string Id => AdapterOrder.Api;

    public void Run(DomainContext context)
    {
        var config = context.Section<ApiSection>(context.Domain.Api, "api");

        var connector = context.Anchors.Resolve(config.ConnectorType);
        if (context.AnchorFailed(connector, Id, Feature)) return;

        var connectorType = connector.Require();
        var mocks = Mocks(context, config);

        foreach (var name in config.SendMethods)
        foreach (var method in context.Reader.SymbolsByName(name)
                     .Where(symbol => symbol.Kind == SymbolKinds.Method)
                     .Where(symbol => symbol.ContainerId is not null
                                      && (symbol.ContainerId == connectorType.Id
                                          || context.Resolution.IsSubtypeOf(symbol.ContainerId, connectorType.Id)))
                     .OrderBy(symbol => symbol.Id, StringComparer.Ordinal))
        foreach (var call in context.Reader.CallsitesTargeting(method.Id)
                     .Where(call => call.Reachable)
                     .OrderBy(call => call.Id, StringComparer.Ordinal))
        {
            var keys = ApiKeys(context, config, call);

            if (keys.Count == 0)
            {
                // No key means no contract to name, and naming one anyway would invent an endpoint.
                BoundaryWriter.Write(
                    context,
                    BoundaryCategories.ExternalMicroservice,
                    ContractKinds.Api,
                    "(unresolved)",
                    new JsonObject
                    {
                        ["unresolvedText"] = call.Args.FirstOrDefault()?.Value.Text ?? string.Empty,
                        ["responseType"] = BoundaryWriter.ResponseType(method.ReturnType),
                        ["mockAvailable"] = false
                    },
                    call);

                continue;
            }

            foreach (var key in keys)
            {
                BoundaryWriter.Write(
                    context,
                    BoundaryCategories.ExternalMicroservice,
                    ContractKinds.Api,
                    key,
                    new JsonObject
                    {
                        ["requestTypes"] = BoundaryWriter.Of(method.Parameters.Select(p => p.Type.FqName)),
                        ["responseType"] = BoundaryWriter.ResponseType(method.ReturnType),
                        ["mockAvailable"] = mocks.Any(file =>
                            file.Contains(key, StringComparison.OrdinalIgnoreCase))
                    },
                    call);
            }
        }
    }

    /// <summary>
    /// The api keys a send was made with: the request object's key argument, or a key passed
    /// directly to the send method (02 §9.8; 03 §18).
    /// </summary>
    private static IReadOnlyList<string> ApiKeys(DomainContext context, ApiSection config, CallsiteRec call)
    {
        var keys = new List<string>();

        foreach (var argument in call.Args)
        {
            switch (Unwrap(argument.Value))
            {
                case ObjectCreationValue request:
                {
                    var key = request.Args
                        .FirstOrDefault(a => a.ParamName is not null
                                             && config.ApiKeyParamNames.Contains(a.ParamName, StringComparer.Ordinal));

                    if (key is not null)
                        keys.AddRange(context.Values.ResolveStrings(key.Value, context.Resolve()).Strings);

                    break;
                }

                default:
                    if (argument.ParamName is not null
                        && config.ApiKeyParamNames.Contains(argument.ParamName, StringComparer.Ordinal))
                    {
                        keys.AddRange(context.Values.ResolveStrings(argument.Value, context.Resolve()).Strings);
                    }

                    break;
            }
        }

        return keys.Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToList();
    }

    private static Value Unwrap(Value value)
        => value is LocalValue { Origin: not null } local ? Unwrap(local.Origin) : value;

    /// <summary>File names under the local mock directory, if the project has one (02 §9.8).</summary>
    private static IReadOnlyList<string> Mocks(DomainContext context, ApiSection config)
    {
        if (config.LocalMockDir is null) return Array.Empty<string>();

        var directory = Path.Combine(context.ProjectRoot, config.LocalMockDir);
        if (!Directory.Exists(directory)) return Array.Empty<string>();

        var files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        foreach (var file in files) context.NoteReadFile(Path.Combine(config.LocalMockDir, file));

        return files;
    }
}
