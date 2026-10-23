using ScreenFlow.Core.Ir;

namespace ScreenFlow.Core.Resolution;

/// <summary>
/// The extension point for values only a domain knows how to resolve (02 §8.2).
/// <para>
/// <c>ServiceLocator.GetService&lt;T&gt;()</c> and <c>ScreenID.X.Id</c> mean nothing to the core —
/// one is a DI lookup, the other a screen id — and both are POS specifics that live in
/// <c>ScreenFlow.UnityPos</c>. Returning null means "not mine", which is what keeps the core from
/// inventing an answer for them.
/// </para>
/// </summary>
public interface IValueProvider
{
    /// <summary>Stable name, used in diagnostics so it is visible which provider answered.</summary>
    string Name { get; }

    /// <summary>The resolution, or null when this provider does not handle the value.</summary>
    ValueResolution? TryResolve(Value value, ResolveContext context);
}
