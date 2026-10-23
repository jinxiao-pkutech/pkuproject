using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;
using ScreenFlow.UnityPos.Ui;
using Xunit;
using Xunit.Abstractions;

namespace ScreenFlow.UnityPos.Tests.Ui;

/// <summary>
/// The ui-bindings adapter (02 §9.3). Covers E-19, E-20 and E-22.
/// </summary>
public sealed class UiBindingsTests
{
    private const string Ssaw0903View =
        "Assets.SceneCustomer.Reception.EdyCharge.View.Script.EdyCharge_SSAW0903_View";

    private const string Ssaw0903Presenter =
        "Assets.SceneCustomer.Reception.EdyCharge.Presenter.Script.EdyCharge_SSAW0903_Presenter";

    private readonly ITestOutputHelper _output;

    public UiBindingsTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// E-19: the button's handler is not assigned directly — the view stores a lambda in a delegate
    /// field and subscribes that field (03 §9). Following the field is the only way to get from
    /// "btnOther was clicked" to the code that runs.
    /// </summary>
    [Fact]
    [Trait("Task", "T14")]
    public void Ui_ControlHandler_ThroughDelegateField()
    {
        var index = DomainIndex.Editor;
        var handlers = index.Reader.ControlHandlers("customer", "SSAW0903");

        foreach (var handler in handlers)
            _output.WriteLine($"{handler.ControlId}.{handler.EventName} -> {handler.HandlerMethodId} ({handler.Confidence})");

        Assert.Equal(4, handlers.Count);
        Assert.All(handlers, handler => Assert.Equal("clicked", handler.EventName));
        Assert.All(handlers, handler => Assert.Equal(ControlsAdapter.Certain, handler.Confidence));

        var other = handlers.Single(handler => handler.ControlId == "selectCharge_Other");

        // The handler is the lambda the view assigned to clickBtnOtherFunc (03 §9).
        var lambda = index.Reader.Symbol(other.HandlerMethodId);
        Assert.NotNull(lambda);
        Assert.True(lambda!.IsLambda);

        var setEvent = index.Symbol(SymbolKinds.Method, Ssaw0903View + ".SetEvent()");
        Assert.Equal(setEvent.Id, lambda.ContainerId);

        // And from that lambda the chain continues into the presenter (T18 follows it further).
        var contract = index.Symbol(
            SymbolKinds.Method,
            "Assets.SceneCustomer.Reception.EdyCharge.View.Script.IEdyCharge_SSAW0903_View_Event.OnClickBtnOther()");

        Assert.Contains(index.Reader.CallsitesIn(lambda.Id), call => call.TargetId == contract.Id);

        // Every control that has a click handler is a control of this screen.
        var controls = index.Reader.Controls("customer", "SSAW0903").Select(c => c.ControlId).ToList();
        Assert.All(handlers, handler => Assert.Contains(handler.ControlId, controls));
    }

    /// <summary>
    /// E-20: the title's key is stated directly and is certain; a button row's key comes from the
    /// <c>Add</c> order lining up with the array order, which is a pattern and therefore a
    /// candidate (02 §9.3 step 4; 03 §14.2).
    /// </summary>
    [Fact]
    [Trait("Task", "T14")]
    public void Ui_TextKeys_IndexedAndDirect()
    {
        var index = DomainIndex.Editor;
        var controls = index.Reader.Controls("customer", "SSAW0903").ToDictionary(c => c.ControlId, StringComparer.Ordinal);

        foreach (var control in controls.Values)
            _output.WriteLine($"{control.ControlId}: {control.TextKey} ({control.TextKeySource}, {control.TextKeyConfidence})");

        // Direct bindings are certain (02 §9.3).
        Assert.Equal("SSAW0903_1", controls["Title"].TextKey);
        Assert.Equal("json:TextTitle", controls["Title"].TextKeySource);
        Assert.Equal(ControlsAdapter.Certain, controls["Title"].TextKeyConfidence);

        Assert.Equal("SSAW0903_2", controls["btnClose"].TextKey);
        Assert.Equal("json:ButtonClose", controls["btnClose"].TextKeySource);

        // The indexed binding reaches each button through listButtons[i] (03 §14.2, E-20).
        Assert.Equal("SSAW0903_6", controls["selectCharge_1000"].TextKey);
        Assert.Equal("json:ButtonTitle[0]", controls["selectCharge_1000"].TextKeySource);
        Assert.Equal("SSAW0903_7", controls["selectCharge_2000"].TextKey);
        Assert.Equal("SSAW0903_12", controls["selectCharge_Other"].TextKey);
        Assert.Equal("json:ButtonTitle[2]", controls["selectCharge_Other"].TextKeySource);

        // Inferred from a pattern, so never stated as certain (02 §9.3 step 4).
        Assert.Equal(ControlsAdapter.Candidate, controls["selectCharge_Other"].TextKeyConfidence);

        // A control nothing assigns a key to says so rather than borrowing one.
        Assert.Null(controls["VelScreenMng"].TextKey);
        Assert.Equal(ControlsAdapter.Unresolved, controls["VelScreenMng"].TextKeyConfidence);

        // The keys resolve to the text the screen shows (03 §15).
        var text = index.Reader.Text("StringLocalizationTable_Customer", "SSAW0903_12", "ja");
        Assert.NotNull(text);
        Assert.Equal("その他の金額を入力\n(1,000円単位)", text!.TextPlain);
    }

    /// <summary>
    /// E-22: the fixture's comments name Err2 and Err22 the other way round (03 §13.3). The code is
    /// what binds a constraint to a code, and a comment is never a parsing fact (CLAUDE.md §4).
    /// </summary>
    [Fact]
    [Trait("Task", "T14")]
    public void Ui_Constraints_FollowCodeNotComments()
    {
        var index = DomainIndex.Editor;
        var constraints = Constraints(index, "SSAW0907");

        foreach (var constraint in constraints)
            _output.WriteLine($"{constraint.Kind} = {constraint.Value} -> {constraint.CodeName} ({constraint.CodeValue})");

        // The comment above SetInputMaxNumberOfCharacter says Err2 and the code passes ErrMsg2,
        // which traces to ERRB002; the comment above SetInputMaxValue says Err22 and the code
        // passes ErrMsg22, which traces to ERRB022. Both agree with the code, which is the point.
        var maxDigits = constraints.Where(c => c.Kind == "maxDigits").ToList();
        Assert.NotEmpty(maxDigits);
        Assert.All(maxDigits, c => Assert.Equal("ERRB002", c.CodeName));
        Assert.All(maxDigits, c => Assert.Equal(2, c.CodeValue));
        Assert.All(maxDigits, c => Assert.Equal("2", c.Value));

        var maxValue = constraints.Single(c => c.Kind == "maxValue");
        Assert.Equal("ERRB022", maxValue.CodeName);
        Assert.Equal(22, maxValue.CodeValue);
        Assert.Equal("25", maxValue.Value);

        var minValue = constraints.Where(c => c.Kind == "minValue").ToList();
        Assert.All(minValue, c => Assert.Equal("ERRB022", c.CodeName));
        Assert.All(minValue, c => Assert.Equal("1", c.Value));

        var allowEmpty = constraints.Where(c => c.Kind == "allowEmpty").ToList();
        Assert.All(allowEmpty, c => Assert.Equal("ERRB024", c.CodeName));
        Assert.All(allowEmpty, c => Assert.Equal("false", c.Value));

        Assert.All(constraints, c => Assert.Equal(ControlsAdapter.Certain, c.Confidence));

        // The same constraint set in several methods is several rows, each with its own method
        // (02 §9.3 step 5) — merging them would hide where the rule is applied.
        Assert.True(
            minValue.Select(c => c.MethodId).Distinct(StringComparer.Ordinal).Count() > 1,
            "minValue is set in more than one method of the fixture");

        // The code resolves to the message the screen shows (03 §16).
        var message = index.Reader.Message(22, "ja");
        Assert.NotNull(message);
        Assert.Equal("登録できない数です", message!.TextRaw);

        // Every constraint names the control it applies to.
        Assert.All(constraints, c => Assert.NotNull(c.ControlFieldId));
    }

    /// <summary>
    /// 02 §9.3 step 1: a control the code queries but the UXML does not declare is still a control.
    /// Dropping it would hide a mismatch between the two; the mismatch is reported instead.
    /// </summary>
    [Fact]
    [Trait("Task", "T14")]
    public void Ui_ControlWithoutUxml_StillRecorded()
    {
        var index = DomainIndex.Editor;
        var controls = index.Reader.Controls("customer", "SSAW0903");

        // VelScreenMng is queried from the document root and is not part of the screen's UXML.
        var root = controls.Single(control => control.ControlId == "VelScreenMng");
        Assert.Null(root.HierarchyPath);
        Assert.NotNull(root.FieldId);
        Assert.Equal("UnityEngine.UIElements.VisualElement", root.ControlType);

        Assert.Contains(
            index.Reader.Diagnostics(),
            diagnostic => diagnostic.Code == ControlsAdapter.NoUxmlMatchCode
                          && diagnostic.Message.Contains("VelScreenMng", StringComparison.Ordinal));

        // A control that is in the UXML carries its path, so the two cases are distinguishable.
        var title = controls.Single(control => control.ControlId == "Title");
        Assert.NotNull(title.HierarchyPath);
        Assert.Contains("EdyCharge_SSAW0903_View", title.HierarchyPath!, StringComparison.Ordinal);
        Assert.Equal(
            "Assets/SceneCustomer/Reception/EdyCharge/View/Screen/EdyCharge_SSAW0903_View.uxml",
            title.UxmlPath);

        // A screen whose view has no UXML at all still has its controls (03 §7).
        var employee = index.Reader.Controls("employee", "SSAW0001");
        Assert.NotEmpty(employee);
        Assert.All(employee, control => Assert.Null(control.UxmlPath));
    }

    private sealed record ConstraintRow(
        string Kind,
        string? Value,
        string? CodeName,
        int? CodeValue,
        string? Confidence,
        string? MethodId,
        string? ControlFieldId);

    private static IReadOnlyList<ConstraintRow> Constraints(DomainIndex index, string screenId)
    {
        using var command = index.Db.Command(
            """
            SELECT kind, value, error_code_name, error_code_value, error_confidence, method_id, control_field_id
            FROM input_constraints WHERE screen_id = $screen ORDER BY kind, id;
            """);

        command.Parameters.AddWithValue("$screen", screenId);

        var rows = new List<ConstraintRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new ConstraintRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return rows;
    }
}
