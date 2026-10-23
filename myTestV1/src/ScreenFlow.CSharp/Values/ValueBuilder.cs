using System.Text;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using ScreenFlow.Core.Ir;
using ScreenFlow.CSharp.Walkers;

namespace ScreenFlow.CSharp.Values;

/// <summary>
/// What the expression being folded sits inside: which symbol owns it, which file it is in, and
/// what is known about the locals of the enclosing body.
/// </summary>
public sealed record ValueContext
{
    /// <summary>The method, lambda, local function or field whose code this expression is part of.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Project-root relative path, needed because a callsite id contains the file.</summary>
    public required string File { get; init; }

    /// <summary>Null for a field or property initialiser, which has no enclosing body.</summary>
    public LocalFlow? Locals { get; init; }

    /// <summary>
    /// The model of the file this expression is in. Needed where a collector has syntax in hand and
    /// has to ask what it binds to, rather than being handed the operation.
    /// </summary>
    public Microsoft.CodeAnalysis.SemanticModel? Model { get; init; }

    /// <summary>Nesting level of the value under construction; the root is 1 (02 §5.4).</summary>
    public int Depth { get; init; } = 1;

    public ValueContext Deeper() => this with { Depth = Depth + 1 };
}

/// <summary>
/// Turns an <see cref="IOperation"/> into a <see cref="Value"/> (02 §5.4).
/// <para>
/// Folding is the heart of the analysis: an event path, a screen id or a topic name is usually a
/// string assembled from literals, constants and a condition, and a hop is only traceable if that
/// string can be stated. Where it cannot, the value says <c>unknown</c> or <c>template</c> — never a
/// plausible-looking string, because a wrong string produces a screen edge that does not exist.
/// </para>
/// </summary>
public sealed class ValueBuilder
{
    /// <summary>How deep the folder follows a value through locals and static fields.</summary>
    public const int MaxFoldDepth = 8;

    private readonly SymbolCollector _symbols;
    private readonly Microsoft.CodeAnalysis.Compilation _compilation;

    public ValueBuilder(SymbolCollector symbols, Microsoft.CodeAnalysis.Compilation compilation)
    {
        _symbols = symbols;
        _compilation = compilation;
    }

    public Value Build(IOperation? operation, ValueContext context)
    {
        if (operation is null) return UnknownValue.Of();

        var value = BuildCore(operation, context);

        // At the depth limit a value has to be a leaf (02 §5.4): a deeper tree would be rejected by
        // the IR contract, and a truncated one would look like a complete answer.
        if (context.Depth >= IrValidator.MaxValueDepth && value.Children().Any())
            return UnknownValue.Of(Text(operation)) with { Type = TypeOf(operation) };

        return value;
    }

    private Value BuildCore(IOperation operation, ValueContext context)
    {
        switch (operation)
        {
            case IParenthesizedOperation parenthesized:
                return BuildCore(parenthesized.Operand, context);

            // A conversion is not a value of its own; what matters is what was converted.
            case IConversionOperation conversion:
                return BuildCore(conversion.Operand, context);

            case IAwaitOperation await:
                return BuildCore(await.Operation, context);
        }

        if (IsString(operation.Type))
        {
            var stringValue = BuildString(operation, context);
            if (stringValue is not null) return stringValue;
        }

        if (operation.ConstantValue.HasValue)
            return Constant(operation.ConstantValue.Value, Text(operation), TypeOf(operation));

        switch (operation)
        {
            case INameOfOperation nameOf:
                return Constant(nameOf.ConstantValue.Value, Text(operation), TypeOf(operation));

            case ITypeOfOperation typeOf:
                return new TypeOfValue
                {
                    TypeId = TypeSymbolId(typeOf.TypeOperand),
                    Text = Text(operation),
                    Type = TypeOf(operation)
                };

            case IInstanceReferenceOperation:
                return new ThisValue
                {
                    TypeId = operation.Type is null ? null : TypeSymbolId(operation.Type),
                    Text = Text(operation),
                    Type = TypeOf(operation)
                };

            case IParameterReferenceOperation parameter:
                return BuildParameter(parameter, context);

            case ILocalReferenceOperation local:
                return BuildLocal(local, context);

            case IFieldReferenceOperation field:
                return BuildMember(field.Field, field.Instance, operation, context);

            case IPropertyReferenceOperation { Arguments.IsEmpty: false } indexer:
                return new ElementAccessValue
                {
                    Receiver = Build(indexer.Instance, context.Deeper()),
                    Index = Build(indexer.Arguments[0].Value, context.Deeper()),
                    Text = Text(operation),
                    Type = TypeOf(operation)
                };

            case IPropertyReferenceOperation property:
                return BuildMember(property.Property, property.Instance, operation, context);

            case IEventReferenceOperation @event:
                return BuildMember(@event.Member, @event.Instance, operation, context);

            case IArrayElementReferenceOperation element:
                return new ElementAccessValue
                {
                    Receiver = Build(element.ArrayReference, context.Deeper()),
                    Index = Build(element.Indices.FirstOrDefault(), context.Deeper()),
                    Text = Text(operation),
                    Type = TypeOf(operation)
                };

            case IObjectCreationOperation creation:
                return BuildObjectCreation(creation, context);

            case IArrayCreationOperation array:
                return BuildArrayCreation(array, context);

            case ITupleOperation tuple:
                return new TupleValue
                {
                    Items = tuple.Elements.Select(e => Build(e, context.Deeper())).ToList(),
                    Text = Text(operation),
                    Type = TypeOf(operation)
                };

            case IDelegateCreationOperation delegateCreation:
                return BuildDelegateCreation(delegateCreation, context);

            case IMethodReferenceOperation methodReference:
                return BuildMethodRef(methodReference.Method, methodReference.Instance, operation, context);

            case IAnonymousFunctionOperation anonymous:
                return BuildLambda(anonymous.Symbol, operation, context);

            case IInvocationOperation invocation:
                return BuildCallResult(invocation, context);

            case IConditionalOperation conditional:
                return new ConditionalValue
                {
                    WhenTrue = Build(conditional.WhenTrue, context.Deeper()),
                    WhenFalse = Build(conditional.WhenFalse, context.Deeper()),
                    Text = Text(operation),
                    Type = TypeOf(operation)
                };

            case ICoalesceOperation coalesce:
                return new ConditionalValue
                {
                    WhenTrue = Build(coalesce.Value, context.Deeper()),
                    WhenFalse = Build(coalesce.WhenNull, context.Deeper()),
                    Text = Text(operation),
                    Type = TypeOf(operation)
                };

            case IConditionalAccessOperation access:
                return BuildCore(access.WhenNotNull, context);

            case IConditionalAccessInstanceOperation instance:
            {
                var access = Ancestor<IConditionalAccessOperation>(instance);
                return access is null
                    ? UnknownValue.Of(Text(operation))
                    : BuildCore(access.Operation, context);
            }

            default:
                return UnknownValue.Of(Text(operation)) with { Type = TypeOf(operation) };
        }
    }

    // ------------------------------------------------------------------ string folding

    /// <summary>
    /// A string-typed expression as <c>const</c>, <c>strings</c> or <c>template</c>, or null when it
    /// is better described structurally (an element access, a call result).
    /// </summary>
    private Value? BuildString(IOperation operation, ValueContext context)
    {
        var folded = FoldStrings(operation, context.Locals, 0);

        if (folded is { Count: 1 })
            return ConstValue.Of(folded[0], Text(operation)) with { Type = TypeOf(operation) };

        if (folded is { Count: > 1 })
            return StringsValue.Of(folded, Text(operation)) with { Type = TypeOf(operation) };

        // Only a composed string becomes a template; a plain unresolvable expression keeps its own
        // shape, so that "which call produced this" stays answerable.
        return IsComposedString(operation)
            ? new TemplateValue
            {
                Template = TemplateOf(operation, context.Locals),
                Text = Text(operation),
                Type = TypeOf(operation)
            }
            : null;
    }

    private static bool IsComposedString(IOperation operation) => operation switch
    {
        IInterpolatedStringOperation => true,
        IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } => true,
        IParenthesizedOperation p => IsComposedString(p.Operand),
        IConversionOperation c => IsComposedString(c.Operand),
        _ => false
    };

    /// <summary>
    /// The complete set of strings an expression can be, or null when that set is not knowable or
    /// would exceed <see cref="StringsValue.MaxValues"/> (02 §5.4). Null never means "empty".
    /// </summary>
    public IReadOnlyList<string>? FoldStrings(IOperation? operation, LocalFlow? locals, int depth)
    {
        if (operation is null || depth > MaxFoldDepth) return null;

        if (operation.ConstantValue is { HasValue: true, Value: string constant })
            return new[] { constant };

        switch (operation)
        {
            case IParenthesizedOperation parenthesized:
                return FoldStrings(parenthesized.Operand, locals, depth + 1);

            case IConversionOperation conversion:
                return FoldStrings(conversion.Operand, locals, depth + 1);

            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } binary:
                return Product(
                    FoldStrings(binary.LeftOperand, locals, depth + 1),
                    FoldStrings(binary.RightOperand, locals, depth + 1));

            case IConditionalOperation conditional:
                return Union(
                    FoldStrings(conditional.WhenTrue, locals, depth + 1),
                    FoldStrings(conditional.WhenFalse, locals, depth + 1));

            case ICoalesceOperation coalesce:
                return Union(
                    FoldStrings(coalesce.Value, locals, depth + 1),
                    FoldStrings(coalesce.WhenNull, locals, depth + 1));

            case IInterpolatedStringOperation interpolated:
                return FoldInterpolated(interpolated, locals, depth);

            case IFieldReferenceOperation field:
                return FoldField(field.Field, depth);

            case ILocalReferenceOperation local:
                return FoldLocalStrings(local, locals, depth);

            case IInvocationOperation invocation:
                return FoldInvocation(invocation, locals, depth);

            default:
                return null;
        }
    }

    private IReadOnlyList<string>? FoldInterpolated(
        IInterpolatedStringOperation interpolated,
        LocalFlow? locals,
        int depth)
    {
        IReadOnlyList<string>? folded = new[] { string.Empty };

        foreach (var part in interpolated.Parts)
        {
            var piece = part switch
            {
                IInterpolatedStringTextOperation text => FoldStrings(text.Text, locals, depth + 1),
                IInterpolationOperation interpolation => FoldStrings(interpolation.Expression, locals, depth + 1),
                _ => null
            };

            folded = Product(folded, piece);
            if (folded is null) return null;
        }

        return folded;
    }

    /// <summary>
    /// A field folds when it is <c>const</c>, when it is <c>string.Empty</c>, or when it is a
    /// <c>static readonly string</c> whose own initialiser folds (02 §5.4).
    /// </summary>
    private IReadOnlyList<string>? FoldField(IFieldSymbol field, int depth)
    {
        if (field is { HasConstantValue: true, ConstantValue: string constant }) return new[] { constant };

        if (field.ContainingType?.SpecialType == SpecialType.System_String && field.Name == nameof(string.Empty))
            return new[] { string.Empty };

        if (!field.IsStatic || !field.IsReadOnly || !IsString(field.Type)) return null;

        var initializer = Initializer(field);
        if (initializer is null) return null;

        var model = _compilation.GetSemanticModel(initializer.SyntaxTree);
        return FoldStrings(model.GetOperation(initializer), locals: null, depth + 1);
    }

    private IReadOnlyList<string>? FoldLocalStrings(
        ILocalReferenceOperation local,
        LocalFlow? locals,
        int depth)
    {
        var assignments = locals?.AssignmentsOf(local.Local) ?? Array.Empty<IOperation>();
        if (assignments.Count == 0) return null;

        IReadOnlyList<string>? folded = Array.Empty<string>();
        foreach (var assignment in assignments)
            folded = Union(folded, FoldStrings(assignment, locals, depth + 1));

        return folded is { Count: 0 } ? null : folded;
    }

    /// <summary>
    /// <c>string.Concat</c> and the case-changing methods, which are the only calls whose result is
    /// determined by their foldable inputs (02 §5.4).
    /// </summary>
    private IReadOnlyList<string>? FoldInvocation(
        IInvocationOperation invocation,
        LocalFlow? locals,
        int depth)
    {
        var method = invocation.TargetMethod;

        if (method.ContainingType?.SpecialType == SpecialType.System_String)
        {
            switch (method.Name)
            {
                case nameof(string.ToLower) or nameof(string.ToLowerInvariant):
                {
                    var folded = FoldStrings(invocation.Instance, locals, depth + 1);
                    return folded?.Select(s => s.ToLowerInvariant()).ToList();
                }

                case nameof(string.ToUpper) or nameof(string.ToUpperInvariant):
                {
                    var folded = FoldStrings(invocation.Instance, locals, depth + 1);
                    return folded?.Select(s => s.ToUpperInvariant()).ToList();
                }

                case nameof(string.Concat):
                {
                    IReadOnlyList<string>? folded = new[] { string.Empty };
                    foreach (var argument in invocation.Arguments)
                    {
                        folded = Product(folded, FoldStrings(argument.Value, locals, depth + 1));
                        if (folded is null) return null;
                    }

                    return folded;
                }
            }
        }

        return null;
    }

    /// <summary>The string with every non-constant fragment replaced by <c>{*}</c> (02 §5.4).</summary>
    public string TemplateOf(IOperation operation, LocalFlow? locals)
    {
        switch (operation)
        {
            case IParenthesizedOperation parenthesized:
                return TemplateOf(parenthesized.Operand, locals);

            case IConversionOperation conversion:
                return TemplateOf(conversion.Operand, locals);

            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } binary:
                return TemplateOf(binary.LeftOperand, locals) + TemplateOf(binary.RightOperand, locals);

            case IInterpolatedStringOperation interpolated:
            {
                var template = new StringBuilder();

                foreach (var part in interpolated.Parts)
                {
                    switch (part)
                    {
                        case IInterpolatedStringTextOperation text:
                            template.Append(text.Text.ConstantValue.Value as string ?? TemplateValue.Placeholder);
                            break;
                        case IInterpolationOperation interpolation:
                            template.Append(TemplateOf(interpolation.Expression, locals));
                            break;
                        default:
                            template.Append(TemplateValue.Placeholder);
                            break;
                    }
                }

                return template.ToString();
            }

            default:
            {
                var folded = FoldStrings(operation, locals, 0);
                return folded is { Count: 1 } ? folded[0] : TemplateValue.Placeholder;
            }
        }
    }

    private static IReadOnlyList<string>? Product(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left is null || right is null) return null;
        if (left.Count * right.Count > StringsValue.MaxValues) return null;

        var result = new List<string>(left.Count * right.Count);
        foreach (var l in left)
        foreach (var r in right)
            result.Add(l + r);

        return Distinct(result);
    }

    private static IReadOnlyList<string>? Union(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left is null || right is null) return null;

        var result = Distinct(left.Concat(right).ToList());
        return result.Count > StringsValue.MaxValues ? null : result;
    }

    private static IReadOnlyList<string> Distinct(IReadOnlyList<string> values)
        => values.Distinct(StringComparer.Ordinal).ToList();

    // ------------------------------------------------------------------ structural values

    private Value BuildParameter(IParameterReferenceOperation parameter, ValueContext context)
    {
        var owner = parameter.Parameter.ContainingSymbol;
        var methodId = owner is null ? context.OwnerId : _symbols.IdOf(owner);

        return new ParamValue
        {
            MethodId = methodId,
            Index = parameter.Parameter.Ordinal,
            Name = parameter.Parameter.Name,
            Text = Text(parameter),
            Type = TypeOf(parameter)
        };
    }

    private Value BuildLocal(ILocalReferenceOperation local, ValueContext context)
    {
        var flow = context.Locals;
        var assignments = flow?.AssignmentsOf(local.Local) ?? Array.Empty<IOperation>();

        // Exactly one assignment is the condition under which a local's value is actually known
        // (02 §5.4); with several, origin stays null rather than naming the most likely one.
        var origin = assignments.Count == 1
            ? Build(assignments[0], context.Deeper())
            : null;

        return new LocalValue
        {
            Name = local.Local.Name,
            Origin = origin,
            IsLoopIndex = flow?.IsLoopIndex(local.Local) ?? false,
            LoopBound = flow?.LoopBound(local.Local) is { } bound
                ? Build(bound, context.Deeper())
                : null,
            Text = Text(local),
            Type = TypeOf(local)
        };
    }

    private Value BuildMember(ISymbol member, IOperation? instance, IOperation operation, ValueContext context)
    {
        if (member.IsStatic || instance is null)
        {
            return new StaticMemberValue
            {
                SymbolId = _symbols.IdOf(member),
                Path = Array.Empty<string>(),
                Text = Text(operation),
                Type = TypeOf(operation)
            };
        }

        var receiver = Build(instance, context.Deeper());

        // ScreenID.SSAW0907.Id is one static member with a path, not a member access on a value
        // (02 §5.4): the domain adapters match on the field, and the path says which part of it.
        if (receiver is StaticMemberValue staticReceiver)
        {
            return staticReceiver with
            {
                Path = staticReceiver.Path.Append(member.Name).ToList(),
                Text = Text(operation),
                Type = TypeOf(operation)
            };
        }

        return new MemberAccessValue
        {
            Receiver = receiver,
            SymbolId = _symbols.IdOf(member),
            Text = Text(operation),
            Type = TypeOf(operation)
        };
    }

    private Value BuildObjectCreation(IObjectCreationOperation creation, ValueContext context)
    {
        // A target-typed new that lands on a ValueTuple is a tuple, whichever syntax wrote it
        // (02 §5.4), so the registry entries of 03 §6 read the same either way.
        if (creation.Type is INamedTypeSymbol { IsTupleType: true })
        {
            return new TupleValue
            {
                Items = creation.Arguments.Select(a => Build(a.Value, context.Deeper())).ToList(),
                Text = Text(creation),
                Type = TypeOf(creation)
            };
        }

        var members = new List<MemberInit>();
        var elements = new List<IReadOnlyList<Value>>();
        ReadInitializer(creation.Initializer, members, elements, context);

        return new ObjectCreationValue
        {
            TypeId = creation.Type is null ? null : TypeSymbolId(creation.Type),
            CtorId = creation.Constructor is null ? null : _symbols.IdOf(creation.Constructor),
            Args = ArgsOf(creation.Arguments, context.Deeper()),
            Members = members,
            Elements = elements,
            Text = Text(creation),
            Type = TypeOf(creation)
        };
    }

    /// <summary>
    /// An array creation is recorded as a creation with one element per item. It has no symbol of
    /// its own, so <c>typeId</c> stays null and the written type is in <c>type</c>.
    /// </summary>
    private Value BuildArrayCreation(IArrayCreationOperation array, ValueContext context)
    {
        var elements = array.Initializer is null
            ? new List<IReadOnlyList<Value>>()
            : array.Initializer.ElementValues
                .Select(element => (IReadOnlyList<Value>)new[] { Build(element, context.Deeper()) })
                .ToList();

        return new ObjectCreationValue
        {
            Elements = elements,
            Text = Text(array),
            Type = TypeOf(array)
        };
    }

    private void ReadInitializer(
        IObjectOrCollectionInitializerOperation? initializer,
        List<MemberInit> members,
        List<IReadOnlyList<Value>> elements,
        ValueContext context)
    {
        if (initializer is null) return;

        foreach (var entry in initializer.Initializers)
        {
            switch (entry)
            {
                case ISimpleAssignmentOperation assignment:
                {
                    var target = assignment.Target;
                    var symbol = target switch
                    {
                        IPropertyReferenceOperation property => (ISymbol)property.Property,
                        IFieldReferenceOperation field => field.Field,
                        _ => null
                    };

                    members.Add(new MemberInit
                    {
                        Name = symbol?.Name ?? target.Syntax.ToString(),
                        SymbolId = symbol is null ? null : _symbols.IdOf(symbol),
                        Value = Build(assignment.Value, context.Deeper())
                    });
                    break;
                }

                // A collection element is the argument list handed to Add; a dictionary entry is
                // therefore two values, not a pair object (02 §5.4).
                case IInvocationOperation add:
                    elements.Add(add.Arguments
                        .Select(argument => Build(argument.Value, context.Deeper()))
                        .ToList());
                    break;

                default:
                    elements.Add(new[] { Build(entry, context.Deeper()) });
                    break;
            }
        }

        members.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
    }

    private Value BuildDelegateCreation(IDelegateCreationOperation creation, ValueContext context)
        => creation.Target switch
        {
            IMethodReferenceOperation method
                => BuildMethodRef(method.Method, method.Instance, creation, context),
            IAnonymousFunctionOperation anonymous
                => BuildLambda(anonymous.Symbol, creation, context),
            _ => UnknownValue.Of(Text(creation)) with { Type = TypeOf(creation) }
        };

    private Value BuildMethodRef(
        IMethodSymbol method,
        IOperation? instance,
        IOperation operation,
        ValueContext context)
        => new MethodRefValue
        {
            MethodId = _symbols.IdOf(method),
            Receiver = instance is null ? null : Build(instance, context.Deeper()),
            Text = Text(operation),
            Type = TypeOf(operation)
        };

    private Value BuildLambda(IMethodSymbol? lambda, IOperation operation, ValueContext context)
    {
        if (lambda is null) return UnknownValue.Of(Text(operation)) with { Type = TypeOf(operation) };

        return new LambdaValue
        {
            MethodId = _symbols.IdOf(lambda),
            Text = Text(operation),
            Type = TypeOf(operation)
        };
    }

    private Value BuildCallResult(IInvocationOperation invocation, ValueContext context)
        => new CallResultValue
        {
            CallsiteId = Ids.CallsiteId(context.OwnerId, context.File, CallStart(invocation)),
            TargetId = _symbols.IdOf(TargetOf(invocation)),
            Receiver = invocation.Instance is null ? null : Build(invocation.Instance, context.Deeper()),
            Args = ArgsOf(invocation.Arguments, context.Deeper()),
            TypeArgs = invocation.TargetMethod.TypeArguments
                .Select(a => _symbols.TypeRefFor(a))
                .OfType<TypeRef>()
                .ToList(),
            Text = Text(invocation),
            Type = TypeOf(invocation)
        };

    /// <summary>
    /// The offset a callsite id is built from (02 §5.6).
    /// <para>
    /// Not the start of the expression: in <c>a.B().C()</c> both calls start at the same offset, so
    /// one id would stand for two calls. The called member's own name token is unique per call and
    /// just as reproducible. The recorded <c>span</c> still covers the whole expression.
    /// </para>
    /// </summary>
    public static int CallStart(IInvocationOperation invocation)
        => invocation.Syntax is Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax invoked
            ? invoked.Expression switch
            {
                Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax member
                    => member.Name.SpanStart,
                Microsoft.CodeAnalysis.CSharp.Syntax.MemberBindingExpressionSyntax binding
                    => binding.Name.SpanStart,
                _ => invoked.Expression.SpanStart
            }
            : invocation.Syntax.SpanStart;

    /// <summary>
    /// The method an invocation names. For an extension method called in reduced form it is the
    /// definition, so that parameter indexes match the declaration (02 §5.5).
    /// </summary>
    public static IMethodSymbol TargetOf(IInvocationOperation invocation)
        => invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;

    /// <summary>
    /// Arguments aligned by parameter (02 §5.4): a named argument lands at its parameter's index, an
    /// omitted optional parameter does not appear at all, and a <c>params</c> array is expanded into
    /// one argument per element, all sharing the parameter index.
    /// <para>
    /// <paramref name="context"/> is the context each argument value is built in. On a record it is
    /// the root level, since the IR contract counts a record's argument as its own tree; inside a
    /// value it is one level deeper (02 §5.4).
    /// </para>
    /// </summary>
    public IReadOnlyList<Arg> ArgsOf(IEnumerable<IArgumentOperation> arguments, ValueContext context)
    {
        var args = new List<Arg>();

        foreach (var argument in arguments)
        {
            if (argument.ArgumentKind == ArgumentKind.DefaultValue) continue;

            var index = argument.Parameter?.Ordinal ?? args.Count;
            var name = argument.Parameter?.Name;

            if (argument.ArgumentKind == ArgumentKind.ParamArray
                && argument.Value is IArrayCreationOperation { Initializer: not null } array)
            {
                foreach (var element in array.Initializer.ElementValues)
                {
                    args.Add(new Arg
                    {
                        ParamName = name,
                        ParamIndex = index,
                        Value = Build(element, context)
                    });
                }

                continue;
            }

            args.Add(new Arg
            {
                ParamName = name,
                ParamIndex = index,
                Value = Build(argument.Value, context)
            });
        }

        return args.OrderBy(a => a.ParamIndex).ToList();
    }

    // ------------------------------------------------------------------ helpers

    public static Value Constant(object? value, string text, TypeRef? type)
        => new ConstValue { ConstantValue = ConstantNode(value), Text = Value.Truncate(text), Type = type };

    /// <summary>A compile-time constant as JSON: string, number, boolean or null (02 §5.4).</summary>
    public static JsonNode? ConstantNode(object? value) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        char character => JsonValue.Create(character.ToString()),
        byte or sbyte or short or ushort or int => JsonValue.Create(Convert.ToInt32(value)),
        uint or long => JsonValue.Create(Convert.ToInt64(value)),
        ulong number => JsonValue.Create(number),
        float or double => JsonValue.Create(Convert.ToDouble(value)),
        decimal number => JsonValue.Create(number),
        _ => JsonValue.Create(value.ToString())
    };

    private string? TypeSymbolId(ITypeSymbol type)
        => type is INamedTypeSymbol named ? _symbols.IdOf(named.OriginalDefinition) : null;

    private TypeRef? TypeOf(IOperation operation) => _symbols.TypeRefFor(operation.Type);

    private static bool IsString(ITypeSymbol? type) => type?.SpecialType == SpecialType.System_String;

    private static string Text(IOperation operation) => Value.Truncate(operation.Syntax.ToString());

    private static SyntaxNode? Initializer(IFieldSymbol field)
    {
        foreach (var reference in field.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax
                {
                    Initializer: not null
                } declarator)
            {
                return declarator.Initializer.Value;
            }
        }

        return null;
    }

    private static T? Ancestor<T>(IOperation operation) where T : class, IOperation
    {
        for (var current = operation.Parent; current is not null; current = current.Parent)
            if (current is T match) return match;

        return null;
    }
}
