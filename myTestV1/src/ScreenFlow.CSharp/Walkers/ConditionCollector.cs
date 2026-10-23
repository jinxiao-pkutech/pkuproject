using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ScreenFlow.Core.Ir;
using ScreenFlow.CSharp.Values;

namespace ScreenFlow.CSharp.Walkers;

/// <summary>
/// Collects the guards governing a callsite, assignment or creation (02 §5.7).
/// <para>
/// This is what turns "this method can send SSAW0907" into "it sends SSAW0907 when the event path
/// equals X": the string tests inside a guard are how an event path, a screen id or an error code
/// identifies the branch it belongs to (03 §10.5).
/// </para>
/// </summary>
public sealed class ConditionCollector
{
    private readonly ValueBuilder _values;

    public ConditionCollector(ValueBuilder values) => _values = values;

    /// <summary>
    /// The guards of one site, outermost first. The walk stops at <paramref name="boundary"/>, the
    /// declaration whose body is being analysed.
    /// </summary>
    public IReadOnlyList<Condition> Collect(
        SyntaxNode site,
        SyntaxNode boundary,
        ValueContext context,
        bool approximate)
    {
        var conditions = new List<Condition>();
        var child = site;

        for (var current = site.Parent; current is not null; child = current, current = current.Parent)
        {
            var condition = From(current, child, context, approximate);
            if (condition is not null) conditions.Add(condition);
            if (ReferenceEquals(current, boundary)) break;
        }

        // The ancestor walk runs inside out; a guard set reads outside in.
        conditions.Reverse();
        return conditions;
    }

    /// <summary>True when the body contains a <c>goto</c> or a label (02 §5.7).</summary>
    public static bool IsApproximate(SyntaxNode body)
        => body.DescendantNodes().Any(n => n is GotoStatementSyntax or LabeledStatementSyntax);

    private Condition? From(SyntaxNode parent, SyntaxNode child, ValueContext context, bool approximate)
    {
        switch (parent)
        {
            case IfStatementSyntax ifStatement:
            {
                // A site inside the condition itself is not guarded by it.
                if (ReferenceEquals(child, ifStatement.Condition)) return null;

                // An else-if chain needs no special case: walking up from the inner if reaches the
                // outer if through its else clause, which is the polarity=false side (02 §5.7).
                var polarity = !ReferenceEquals(child, ifStatement.Else);
                return Build(ConditionKinds.If, polarity, ifStatement.Condition, context, approximate);
            }

            case ConditionalExpressionSyntax ternary:
            {
                if (ReferenceEquals(child, ternary.Condition)) return null;

                var polarity = ReferenceEquals(child, ternary.WhenTrue);
                return Build(ConditionKinds.Ternary, polarity, ternary.Condition, context, approximate);
            }

            case WhileStatementSyntax loop when !ReferenceEquals(child, loop.Condition):
                return Build(ConditionKinds.Loop, true, loop.Condition, context, approximate);

            case DoStatementSyntax loop when !ReferenceEquals(child, loop.Condition):
                return Build(ConditionKinds.Loop, true, loop.Condition, context, approximate);

            case ForStatementSyntax { Condition: not null } loop when ReferenceEquals(child, loop.Statement):
                return Build(ConditionKinds.Loop, true, loop.Condition, context, approximate);

            case CommonForEachStatementSyntax loop when ReferenceEquals(child, loop.Statement):
                return Build(ConditionKinds.Loop, true, loop.Expression, context, approximate);

            case CatchClauseSyntax katch:
                return new Condition
                {
                    Kind = ConditionKinds.Catch,
                    Polarity = true,
                    Text = (katch.Declaration?.ToString() ?? "catch").Trim(),
                    Span = SymbolCollector.SpanOf(katch.Declaration ?? (SyntaxNode)katch.CatchKeyword.Parent!),
                    Approximate = approximate
                };

            case SwitchSectionSyntax section:
                return FromSwitchSection(section, context, approximate);

            case SwitchExpressionArmSyntax arm when !ReferenceEquals(child, arm.Pattern):
                return FromSwitchArm(arm, context, approximate);

            // 02 §5.7: the right operand of && or || only runs when the left one allows it.
            case BinaryExpressionSyntax binary
                when binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression):
            {
                if (!ReferenceEquals(child, binary.Right)) return null;
                return Build(ConditionKinds.LogicalRight, true, binary.Left, context, approximate);
            }

            default:
                return null;
        }
    }

    private Condition Build(
        string kind,
        bool polarity,
        ExpressionSyntax expression,
        ValueContext context,
        bool approximate)
    {
        var tests = new List<StringTest>();
        CollectTests(expression, negate: false, tests, context);

        return new Condition
        {
            Kind = kind,
            Polarity = polarity,
            Mode = ModeOf(expression),
            Text = expression.ToString(),
            Tests = tests,
            Span = SymbolCollector.SpanOf(expression),
            Approximate = approximate
        };
    }

    /// <summary><c>A || B</c> means any of its tests is enough; anything else means all of them.</summary>
    private static string ModeOf(ExpressionSyntax expression) => Unwrap(expression) switch
    {
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalOrExpression) => ConditionModes.Any,
        _ => ConditionModes.All
    };

    private Condition? FromSwitchSection(SwitchSectionSyntax section, ValueContext context, bool approximate)
    {
        if (section.Parent is not SwitchStatementSyntax statement) return null;

        var subject = statement.Expression;
        var isDefault = section.Labels.Any(label => label is DefaultSwitchLabelSyntax);

        if (!isDefault)
        {
            var tests = new List<StringTest>();
            foreach (var label in section.Labels)
                AddSwitchLabel(label, subject, StringTestOps.SwitchLabel, tests, context);

            return new Condition
            {
                Kind = ConditionKinds.SwitchCase,
                Polarity = true,

                // Several labels on one section: matching any of them enters it.
                Mode = section.Labels.Count > 1 ? ConditionModes.Any : ConditionModes.All,
                Text = string.Join(" ", section.Labels.Select(l => l.ToString().Trim())),
                Tests = tests,
                Span = SymbolCollector.SpanOf(subject),
                Approximate = approximate
            };
        }

        // The default section runs when no other label matched, so its tests are the negation of
        // every other label (02 §5.7).
        var negated = new List<StringTest>();
        foreach (var other in statement.Sections.Where(s => !ReferenceEquals(s, section)))
        foreach (var label in other.Labels)
            AddSwitchLabel(label, subject, StringTestOps.NotEquals, negated, context);

        return new Condition
        {
            Kind = ConditionKinds.SwitchDefault,
            Polarity = true,
            Mode = ConditionModes.All,
            Text = "default",
            Tests = negated,
            Span = SymbolCollector.SpanOf(subject),
            Approximate = approximate
        };
    }

    private Condition? FromSwitchArm(SwitchExpressionArmSyntax arm, ValueContext context, bool approximate)
    {
        if (arm.Parent is not SwitchExpressionSyntax expression) return null;

        var tests = new List<StringTest>();
        var isDiscard = arm.Pattern is DiscardPatternSyntax;

        if (!isDiscard) AddPattern(arm.Pattern, expression.GoverningExpression, StringTestOps.SwitchLabel, tests, context);

        return new Condition
        {
            Kind = isDiscard ? ConditionKinds.SwitchDefault : ConditionKinds.SwitchCase,
            Polarity = true,
            Mode = ConditionModes.All,
            Text = arm.Pattern.ToString(),
            Tests = tests,
            Span = SymbolCollector.SpanOf(expression.GoverningExpression),
            Approximate = approximate
        };
    }

    private void AddSwitchLabel(
        SwitchLabelSyntax label,
        ExpressionSyntax subject,
        string op,
        List<StringTest> tests,
        ValueContext context)
    {
        switch (label)
        {
            case CaseSwitchLabelSyntax { Value: not null } constant:
                AddTests(subject, constant.Value, op, tests, context);
                break;
            case CasePatternSwitchLabelSyntax pattern:
                AddPattern(pattern.Pattern, subject, op, tests, context);
                break;
        }
    }

    private void AddPattern(
        PatternSyntax pattern,
        ExpressionSyntax subject,
        string op,
        List<StringTest> tests,
        ValueContext context)
    {
        if (pattern is ConstantPatternSyntax constant) AddTests(subject, constant.Expression, op, tests, context);
    }

    // ------------------------------------------------------------------ string tests

    private void CollectTests(
        ExpressionSyntax expression,
        bool negate,
        List<StringTest> tests,
        ValueContext context)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                CollectTests(parenthesized.Expression, negate, tests, context);
                return;

            case PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.LogicalNotExpression):
                // 02 §5.7: a negated compound condition keeps its text and contributes no tests,
                // because "not (A and B)" is not expressible as a set of tests.
                if (IsCompound(unary.Operand)) return;
                CollectTests(unary.Operand, !negate, tests, context);
                return;

            case BinaryExpressionSyntax binary
                when binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression):
                CollectTests(binary.Left, negate, tests, context);
                CollectTests(binary.Right, negate, tests, context);
                return;

            case BinaryExpressionSyntax binary
                when binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression):
            {
                var equality = binary.IsKind(SyntaxKind.EqualsExpression) != negate;
                var op = equality ? StringTestOps.EqualsOp : StringTestOps.NotEquals;

                if (Literals(binary.Right, context).Count > 0)
                    AddTests(binary.Left, binary.Right, op, tests, context);
                else if (Literals(binary.Left, context).Count > 0)
                    AddTests(binary.Right, binary.Left, op, tests, context);

                return;
            }

            case IsPatternExpressionSyntax pattern when pattern.Pattern is ConstantPatternSyntax constant:
                AddTests(
                    pattern.Expression,
                    constant.Expression,
                    negate ? StringTestOps.NotEquals : StringTestOps.EqualsOp,
                    tests,
                    context);
                return;

            case InvocationExpressionSyntax invocation:
                CollectInvocationTest(invocation, negate, tests, context);
                return;
        }
    }

    private void CollectInvocationTest(
        InvocationExpressionSyntax invocation,
        bool negate,
        List<StringTest> tests,
        ValueContext context)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax member) return;

        var name = member.Name.Identifier.ValueText;
        var arguments = invocation.ArgumentList.Arguments;

        // string.Equals(a, "x")
        if (name == nameof(string.Equals) && arguments.Count == 2 && IsStringType(member.Expression))
        {
            var op = negate ? StringTestOps.NotEquals : StringTestOps.EqualsOp;

            if (Literals(arguments[1].Expression, context).Count > 0)
                AddTests(arguments[0].Expression, arguments[1].Expression, op, tests, context);
            else if (Literals(arguments[0].Expression, context).Count > 0)
                AddTests(arguments[1].Expression, arguments[0].Expression, op, tests, context);

            return;
        }

        if (arguments.Count != 1) return;

        var argument = arguments[0].Expression;

        switch (name)
        {
            case nameof(string.Equals):
            {
                var op = negate ? StringTestOps.NotEquals : StringTestOps.EqualsOp;

                // "x".Equals(a) reads the other way round, and means the same thing.
                if (Literals(argument, context).Count > 0) AddTests(member.Expression, argument, op, tests, context);
                else if (Literals(member.Expression, context).Count > 0)
                    AddTests(argument, member.Expression, op, tests, context);

                return;
            }

            // There is no negated form of these in the vocabulary of 02 §5.7, so a negated one
            // contributes no test rather than an inverted meaning.
            case nameof(string.Contains) when !negate:
                AddTests(member.Expression, argument, StringTestOps.Contains, tests, context);
                return;
            case nameof(string.StartsWith) when !negate:
                AddTests(member.Expression, argument, StringTestOps.StartsWith, tests, context);
                return;
            case nameof(string.EndsWith) when !negate:
                AddTests(member.Expression, argument, StringTestOps.EndsWith, tests, context);
                return;
        }
    }

    /// <summary>
    /// One test per literal the comparand folds to. A comparand that folds to several strings is
    /// several tests, not the first of them (02 §5.4).
    /// </summary>
    private void AddTests(
        ExpressionSyntax subject,
        ExpressionSyntax literal,
        string op,
        List<StringTest> tests,
        ValueContext context)
    {
        var values = Literals(literal, context);
        if (values.Count == 0) return;

        var folded = _values.Build(Operation(subject, context), context);

        foreach (var value in values)
            tests.Add(new StringTest { Subject = folded, Op = op, Literal = value });
    }

    private IReadOnlyList<string> Literals(ExpressionSyntax expression, ValueContext context)
    {
        var operation = Operation(expression, context);
        if (operation is null) return Array.Empty<string>();

        return _values.FoldStrings(operation, context.Locals, 0) ?? Array.Empty<string>();
    }

    private IOperation? Operation(ExpressionSyntax expression, ValueContext context)
        => context.Model?.GetOperation(expression);

    private static bool IsStringType(ExpressionSyntax expression)
    {
        var text = expression.ToString();
        return text is "string" or "String" or "System.String";
    }

    private static bool IsCompound(ExpressionSyntax expression) => Unwrap(expression) switch
    {
        BinaryExpressionSyntax binary =>
            binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression),
        _ => false
    };

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
        => expression is ParenthesizedExpressionSyntax parenthesized
            ? Unwrap(parenthesized.Expression)
            : expression;
}
