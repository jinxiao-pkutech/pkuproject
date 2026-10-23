using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.UnityPos.Boundaries;

/// <summary>The <c>sql</c> section of <c>unitypos.json</c> (02 §4.2).</summary>
public sealed record SqlSection
{
    [JsonPropertyName("statementPattern")]
    public string StatementPattern { get; init; } = @"^\s*(SELECT|INSERT|UPDATE|DELETE|REPLACE|WITH)\b";

    [JsonPropertyName("minLength")] public int MinLength { get; init; } = 12;
}

/// <summary>What a statement does to a table (02 §9.8).</summary>
public static class TableAccessModes
{
    public const string Read = "read";
    public const string Write = "write";
}

/// <summary>
/// Table access through SQL literals (02 §9.8; 03 §19).
/// <para>
/// Two pieces of code that never call each other are connected by the table between them: one
/// writes it, another reads it. The chain genuinely breaks there, and the readable answer is the
/// table name plus who writes it — which is what this records.
/// </para>
/// </summary>
public sealed class SqlAdapter : IDomainAdapter
{
    public const string Feature = "sql";
    public const string EdgeKind = "tableAccess";
    public const string EdgePrefix = "de_";

    /// <summary>
    /// The clauses a table name follows (02 §9.8). A deliberately small grammar: this is a
    /// tokeniser, not a SQL parser, and anything it cannot read it does not report.
    /// </summary>
    private static readonly Regex TableClause = new(
        @"\b(?<clause>FROM|JOIN|INTO|UPDATE|DELETE\s+FROM)\s+(?<table>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SelectStatement = new(
        @"^\s*(SELECT|WITH)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Id => AdapterOrder.Sql;

    public void Run(DomainContext context)
    {
        var config = context.Section<SqlSection>(context.Domain.Sql, "sql");
        var statement = new Regex(config.StatementPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var accesses = new List<(string MethodId, string Table, string Mode, CallsiteRec Call)>();

        foreach (var method in NativeAdapter.SourceMethods(context))
        foreach (var (call, arguments) in Sites(context, method))
        {
            foreach (var argument in arguments)
            {
                foreach (var text in argument.Value.FoldedStrings())
                {
                    if (text.Length < config.MinLength) continue;
                    if (!statement.IsMatch(text)) continue;

                    var mode = SelectStatement.IsMatch(text) ? TableAccessModes.Read : TableAccessModes.Write;
                    var tables = Tables(text);
                    if (tables.Count == 0) continue;

                    foreach (var table in tables) accesses.Add((method.Id, table, mode, call));

                    context.Writer.Add(new DomainEdgeRow
                    {
                        Id = Ids.DerivedId(EdgePrefix, new { kind = EdgeKind, callsite = call.Id, tables }),
                        Kind = EdgeKind,
                        CallsiteId = call.Id,
                        FromMethodId = call.CallerId,
                        Json = new JsonObject
                        {
                            ["tables"] = BoundaryWriter.Of(tables),
                            ["mode"] = mode,
                            ["statement"] = Value.Truncate(text)
                        }.ToJsonString()
                    });
                }
            }
        }

        WriteBoundaries(context, accesses);
    }

    /// <summary>
    /// Where a SQL literal can be: an argument of a call, or of a command object being created —
    /// the fixture builds its commands with <c>new SQLiteCommand(sql, connection)</c> (03 §19).
    /// </summary>
    private static IEnumerable<(CallsiteRec Call, IReadOnlyList<Arg> Args)> Sites(
        DomainContext context,
        SymbolRec method)
    {
        foreach (var call in context.Reader.CallsitesIn(method.Id)
                     .Where(call => call.Reachable)
                     .OrderBy(call => call.Id, StringComparer.Ordinal))
        {
            yield return (call, call.Args);
        }

        foreach (var creation in context.Reader.CreationsIn(method.Id)
                     .Where(creation => creation.Reachable)
                     .OrderBy(creation => creation.Id, StringComparer.Ordinal))
        {
            var value = creation.ObjectCreation;
            if (value is null || value.Args.Count == 0) continue;

            // A creation is not a callsite; the edge is attributed to the creating method with the
            // creation's own span, which is what an evidence lookup needs.
            yield return (
                new CallsiteRec
                {
                    Id = creation.Id,
                    CallerId = creation.MethodId,
                    TargetId = value.CtorId,
                    Order = creation.Order,
                    Span = creation.Span,
                    Reachable = creation.Reachable
                },
                value.Args);
        }
    }

    /// <summary>
    /// A read is a boundary: the data came from somewhere else, and the resume points are the
    /// methods that write the same table (02 §9.8).
    /// </summary>
    private static void WriteBoundaries(
        DomainContext context,
        IReadOnlyList<(string MethodId, string Table, string Mode, CallsiteRec Call)> accesses)
    {
        var writers = accesses
            .Where(access => access.Mode == TableAccessModes.Write)
            .GroupBy(access => access.Table, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(access => access.MethodId)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);

        foreach (var access in accesses.Where(access => access.Mode == TableAccessModes.Read))
        {
            BoundaryWriter.Write(
                context,
                BoundaryCategories.DatabaseMediated,
                ContractKinds.Table,
                access.Table,
                new JsonObject
                {
                    ["writers"] = BoundaryWriter.Of(writers.GetValueOrDefault(access.Table) ?? new List<string>()),
                    ["mode"] = access.Mode
                },
                access.Call);
        }
    }

    /// <summary>Table names in a statement (02 §9.8). Every one of them, not only the first.</summary>
    public static IReadOnlyList<string> Tables(string statement)
        => TableClause.Matches(statement)
            .Select(match => match.Groups["table"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(table => table, StringComparer.Ordinal)
            .ToList();
}
