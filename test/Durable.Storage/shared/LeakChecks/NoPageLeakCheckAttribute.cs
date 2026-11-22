namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Exempts a test class from holding an <see cref="ArcBufferLeakCheck"/>, for the reason given
/// (test/Durable.Storage/docs/spec/verification.md §11.1).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
internal sealed class NoPageLeakCheckAttribute(string reason) : Attribute
{
    /// <summary>
    /// Gets why the class needs no page leak check.
    /// </summary>
    public string Reason { get; } = reason;
}
