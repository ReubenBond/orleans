namespace Orleans.Durable.Storage.Engine;

/// <summary>
/// Builds and reads the rejections a decoder reports: an <see cref="InvalidDataException"/> naming the first rule its
/// input breaks, with the rule's reason code under <see cref="ReasonKey"/> (conventions.md §15). Each format's owning
/// section lists its codes in check order, and its rejection vectors and fuzz oracle use the same codes.
/// </summary>
public static class DecodeRejection
{
    /// <summary>
    /// The <see cref="Exception.Data"/> key under which a rejection carries its reason code, one key for every format.
    /// </summary>
    public const string ReasonKey = "orleans.durable.storage.decode.reason";

    /// <summary>
    /// Creates the rejection of an input which breaks the rule <paramref name="reason"/> names.
    /// </summary>
    /// <param name="reason">The rule's reason code, from its format's list.</param>
    /// <param name="message">The rule the input breaks, in words.</param>
    /// <returns>The rejection, carrying <paramref name="reason"/> under <see cref="ReasonKey"/>.</returns>
    public static InvalidDataException Create(string reason, string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        var rejection = new InvalidDataException(message);
        rejection.Data[ReasonKey] = reason;
        return rejection;
    }

    /// <summary>
    /// Returns the reason code a rejection carries, or <see langword="null"/> when it carries none.
    /// </summary>
    public static string? ReasonOf(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Data[ReasonKey] as string;
    }
}
