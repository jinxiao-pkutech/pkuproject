using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.Core.Resolution;

/// <summary>
/// Resolves a recorded value to the concrete types, delegates or strings it can be (02 §8.2).
/// <para>
/// The extractor records what the source says; this answers what it can mean. Both halves are
/// needed and neither may guess: an event path that cannot be resolved completely is reported as
/// incomplete, because a plausible-looking string would create a screen edge that does not exist.
/// </para>
/// </summary>
public sealed class ValueResolver
{
    private readonly IndexReader _reader;
    private readonly ResolutionIndex _index;
    private readonly FieldPointsTo _fields;

    public ValueResolver(IndexReader reader, ResolutionIndex index, FieldPointsTo fields)
    {
        _reader = reader;
        _index = index;
        _fields = fields;
    }

    /// <summary>The concrete types and delegate values the expression can hold (02 §8.2).</summary>
    public ValueResolution ResolveConcrete(Value value, ResolveContext context)
        => Resolve(value, context, strings: false);

    /// <summary>The strings the expression can be (02 §8.2).</summary>
    public ValueResolution ResolveStrings(Value value, ResolveContext context)
        => Resolve(value, context, strings: true);

    private ValueResolution Resolve(Value value, ResolveContext context, bool strings)
    {
        if (context.TooDeep) return ValueResolution.LimitedBy(ResolutionLimits.Depth);

        switch (value)
        {
            // ---------------------------------------------------------- leaves

            case ConstValue constant:
                return strings
                    ? Strings(constant.FoldedStrings(), complete: true)
                    : ValueResolution.Nothing;

            case StringsValue set:
                return strings ? Strings(set.Values, complete: true) : ValueResolution.Nothing;

            // A template is a shape, not a value: it is deliberately not completable (02 §5.4).
            case TemplateValue:
                return ValueResolution.Unresolved;

            case ThisValue self:
                var typeId = self.TypeId ?? context.CurrentTypeId;
                return strings || typeId is null
                    ? ValueResolution.Unresolved
                    : ValueResolution.OfTypes(typeId);

            case ObjectCreationValue creation:
                return strings || creation.TypeId is null
                    ? ValueResolution.Unresolved
                    : ValueResolution.OfTypes(creation.TypeId);

            case LambdaValue or MethodRefValue:
                return strings ? ValueResolution.Unresolved : ValueResolution.OfValues(value);

            case TypeOfValue typeOf:
                return strings || typeOf.TypeId is null
                    ? ValueResolution.Unresolved
                    : ValueResolution.OfTypes(typeOf.TypeId);

            // ---------------------------------------------------------- recursive

            case ConditionalValue conditional:
                return Resolve(conditional.WhenTrue, context.Deeper(), strings)
                    .Merge(Resolve(conditional.WhenFalse, context.Deeper(), strings));

            case LocalValue local:
                return local.Origin is null
                    ? ValueResolution.Unresolved
                    : Resolve(local.Origin, context.Deeper(), strings);

            case MemberAccessValue member:
            {
                // A provider may know the member outright: ScreenID.Id reached through a parameter
                // is a screen id, and chasing the Id property's own assignments would instead walk
                // into the ScreenID constructor (02 §8.2, §9.1 step 4).
                var provided = FromProviders(value, context);
                if (provided.Complete && !provided.IsEmpty) return provided;

                return ResolveField(member.SymbolId, FieldPointsTo.ReceiverTypeOf(member), context, strings);
            }

            case StaticMemberValue staticMember:
                // A trailing path is domain knowledge: ScreenID.X.Id is a screen id, which only the
                // POS adapter can say (02 §8.2).
                return staticMember.Path.Count > 0
                    ? FromProviders(value, context)
                    : ResolveField(staticMember.SymbolId, receiverTypeId: null, context, strings);

            case ParamValue parameter:
                return ResolveParameter(parameter, context, strings);

            case CallResultValue:
                return FromProviders(value, context);

            default:
                return ValueResolution.Unresolved;
        }
    }

    /// <summary>
    /// A field or property: every value assigned to it, including its initialiser (02 §8.2). A field
    /// with nothing assigned anywhere resolves to nothing and says so.
    /// </summary>
    private ValueResolution ResolveField(
        string? symbolId,
        string? receiverTypeId,
        ResolveContext context,
        bool strings)
    {
        if (symbolId is null) return ValueResolution.Unresolved;

        var key = "field:" + symbolId;
        if (!context.Enter(key)) return ValueResolution.LimitedBy(ResolutionLimits.Cycle);

        try
        {
            var targets = _fields.TargetsOf(symbolId, receiverTypeId);
            if (targets.IsEmpty) return ValueResolution.Unresolved;

            var result = ValueResolution.Nothing;
            foreach (var assigned in targets.Values)
                result = result.Merge(Resolve(assigned, context.Deeper(), strings));

            return result;
        }
        finally
        {
            context.Leave(key);
        }
    }

    /// <summary>
    /// A parameter, followed back into every caller that passes it (02 §8.2). The callsites it came
    /// from travel with the answer, so a report can show where the value was decided.
    /// </summary>
    private ValueResolution ResolveParameter(ParamValue parameter, ResolveContext context, bool strings)
    {
        if (!context.AllowParamPropagation) return ValueResolution.Unresolved;
        if (context.ParamLimitReached) return ValueResolution.LimitedBy(ResolutionLimits.ParamPropagation);

        var key = "param:" + parameter.MethodId + ":" + parameter.Index;
        if (!context.Enter(key)) return ValueResolution.LimitedBy(ResolutionLimits.Cycle);

        try
        {
            var callsites = _reader.CallsitesTargeting(parameter.MethodId);
            if (callsites.Count == 0) return ValueResolution.Unresolved;

            var result = ValueResolution.Nothing;
            var found = false;

            foreach (var callsite in callsites.OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                var argument = callsite.Args.FirstOrDefault(a => a.ParamIndex == parameter.Index);
                if (argument is null) continue;

                found = true;
                var inner = Resolve(argument.Value, context.IntoCaller(), strings);

                result = result.Merge(inner with
                {
                    SourceCallsites = inner.SourceCallsites.Concat(new[] { callsite.Id }).ToList()
                });
            }

            // Every caller omitted the argument, so its value is the declared default, which the
            // extractor does not record as an argument (02 §5.4).
            return found ? result : ValueResolution.Unresolved;
        }
        finally
        {
            context.Leave(key);
        }
    }

    private static ValueResolution FromProviders(Value value, ResolveContext context)
    {
        foreach (var provider in context.Providers)
        {
            var resolution = provider.TryResolve(value, context);
            if (resolution is not null) return resolution;
        }

        return ValueResolution.LimitedBy(ResolutionLimits.NoProvider);
    }

    private static ValueResolution Strings(IReadOnlyList<string> values, bool complete)
        => values.Count == 0 ? ValueResolution.Unresolved : ValueResolution.OfStrings(values, complete);

    /// <summary>The type a symbol declares, used to seed <c>this</c> when resolving inside a method.</summary>
    public string? DeclaringTypeOf(string methodId) => _index.DeclaringTypeOf(methodId);
}
