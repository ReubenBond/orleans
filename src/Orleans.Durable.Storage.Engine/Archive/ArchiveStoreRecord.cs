using System.Collections.Immutable;
using System.Globalization;
using System.Text;


namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>What decoding a store's record gives (conventions.md §15): the record, or the rejection naming the rule it breaks.</summary>
public readonly union ArchiveStoreRecordDecoding(ArchiveStoreRecord, InvalidDataException);

/// <summary>
/// One field of a store's record (archive.md §7): a store field, the layout among them, or a caller's parameter, which
/// the record names apart so that neither can take the other's name.
/// </summary>
/// <param name="Name">The field's name: a store field's key, or a caller parameter's key without its prefix.</param>
/// <param name="IsParameter">Whether the field is one of the caller's parameters.</param>
public readonly record struct ArchiveStoreRecordField(string Name, bool IsParameter)
{
    /// <inheritdoc/>
    public override string ToString() => IsParameter ? ArchiveStoreRecord.ParameterPrefix + Name : Name;
}

/// <summary>
/// An archive store's record (archive.md §2.4, §7), one object per store, which its first user creates once and every
/// later user reads: strict UTF-8 lines separated by a line feed, with no terminator, each <c>{field}={value}</c>. Its
/// ID (<c>id</c>) comes first and its layout (<c>layout</c>) second; then the store's other fields, its <c>Plan</c>
/// parameters, and then the caller's parameters, each named <c>caller.{key}</c>, which the store records verbatim and
/// never compares or interprets; each group in ordinal order of its keys. The two groups are compared as separate maps,
/// each by its owner: the store's fields by the store (<see cref="CheckStoreFieldsMatch"/>, HI-I3), the caller's
/// parameters by the caller (<see cref="CheckParametersMatch"/>); so a store field added later never collides with a
/// caller parameter or shifts another field. Every field is fixed for the cluster's life.
/// </summary>
public sealed class ArchiveStoreRecord
{
    /// <summary>The store ID's field, the record's first.</summary>
    public const string IdField = "id";

    /// <summary>
    /// The field a kind mismatch names (<see cref="ArchiveStoreIdentity.CheckKind"/>): the store's kind, which the record
    /// does not hold, since its layout implies it, against the kind the composition records.
    /// </summary>
    public const string KindField = "kind";

    /// <summary>The layout's field, the record's second.</summary>
    public const string LayoutField = "layout";

    /// <summary>The prefix of every caller parameter's field, which no store field's key holds.</summary>
    public const string ParameterPrefix = "caller.";

    internal const string ReasonBadUtf8 = "bad-utf8";
    internal const string ReasonBadLine = "bad-line";
    internal const string ReasonDuplicateField = "duplicate-field";
    internal const string ReasonBadId = "bad-id";
    internal const string ReasonBadLayout = "bad-layout";

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private ArchiveStoreRecord(ArchiveStoreId id, string layout, ImmutableSortedDictionary<string, string> storeFields, ImmutableSortedDictionary<string, string> parameters)
    {
        Id = id;
        Layout = layout;
        StoreFields = storeFields;
        Parameters = parameters;
    }

    /// <summary>Gets the store's ID, the record's <c>id</c>.</summary>
    public ArchiveStoreId Id { get; }

    /// <summary>Gets the store's layout, the record's <c>layout</c>.</summary>
    public string Layout { get; }

    /// <summary>Gets the store's other fields, its <c>Plan</c> parameters, by key.</summary>
    public ImmutableSortedDictionary<string, string> StoreFields { get; }

    /// <summary>Gets the caller's parameters, by key without their prefix.</summary>
    public ImmutableSortedDictionary<string, string> Parameters { get; }

    /// <summary>Makes a record of a store's ID, its layout and other fields, and the caller's parameters.</summary>
    /// <param name="id">The store's ID.</param>
    /// <param name="layout">The store's layout.</param>
    /// <param name="storeFields">The store's <c>Plan</c> parameters.</param>
    /// <param name="parameters">The caller's opaque parameters.</param>
    /// <exception cref="ArgumentException">
    /// A field's or parameter's key is empty or not of <c>[a-z0-9-]</c>, or a store field is named <c>id</c> or
    /// <c>layout</c>; a value holds a line feed or is not well-formed UTF-16; the layout is empty; or the ID is the default.
    /// </exception>
    public static ArchiveStoreRecord Create(
        ArchiveStoreId id,
        string layout,
        IReadOnlyDictionary<string, string> storeFields,
        IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentException.ThrowIfNullOrEmpty(layout);
        ArgumentNullException.ThrowIfNull(storeFields);
        ArgumentNullException.ThrowIfNull(parameters);
        if (id == default)
        {
            throw new ArgumentException("The default ArchiveStoreId is not a store ID.", nameof(id));
        }

        CheckValue(LayoutField, layout, nameof(layout));
        foreach (var (key, value) in storeFields)
        {
            if (!IsName(key) || key is IdField or LayoutField)
            {
                throw new ArgumentException($"A store record's field '{key}' is not a key of [a-z0-9-] other than id and layout.", nameof(storeFields));
            }

            CheckValue(key, value, nameof(storeFields));
        }

        CheckParameters(parameters);
        return new ArchiveStoreRecord(id, layout, storeFields.ToImmutableSortedDictionary(StringComparer.Ordinal), parameters.ToImmutableSortedDictionary(StringComparer.Ordinal));
    }

    /// <summary>
    /// Checks a caller's parameters as <see cref="Create"/> does, so a caller refuses a malformed set before it reaches a
    /// store: each key a non-empty key of <c>[a-z0-9-]</c>, and each value without a line feed and well-formed UTF-16.
    /// </summary>
    /// <param name="parameters">The caller's opaque parameters.</param>
    /// <exception cref="ArgumentException">A key or a value is malformed; the message names the key.</exception>
    public static void CheckParameters(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        foreach (var (key, value) in parameters)
        {
            if (!IsName(key))
            {
                throw new ArgumentException($"A store record's parameter '{key}' is not a non-empty key of [a-z0-9-].", nameof(parameters));
            }

            CheckValue(key, value, nameof(parameters));
        }
    }

    /// <summary>
    /// Decodes a record, checking in order that its bytes are strict UTF-8 (<c>bad-utf8</c>); that each line is a
    /// non-empty key of <c>[a-z0-9-]</c>, or <c>caller.</c> and one, an equals sign, and a value (<c>bad-line</c>); that no
    /// key repeats (<c>duplicate-field</c>); that the first line is <c>id</c> with a store ID (<c>bad-id</c>); and that the
    /// second is <c>layout</c> with a non-empty value (<c>bad-layout</c>).
    /// </summary>
    /// <returns>The record, or the rejection naming the first rule the bytes break.</returns>
    public static ArchiveStoreRecordDecoding Decode(ReadOnlySpan<byte> bytes)
    {
        string text;
        try
        {
            text = Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return DecodeRejection.Create(ReasonBadUtf8, "The store's record is not strict UTF-8.");
        }

        var lines = text.Split('\n');
        var fields = new List<KeyValuePair<string, string>>(lines.Length);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Length; index++)
        {
            var equals = lines[index].IndexOf('=', StringComparison.Ordinal);
            var key = equals < 0 ? string.Empty : lines[index][..equals];
            if (!IsName(key.StartsWith(ParameterPrefix, StringComparison.Ordinal) ? key[ParameterPrefix.Length..] : key))
            {
                return Reject(ReasonBadLine, $"Line {index} of the store's record is not a key of [a-z0-9-], optionally after '{ParameterPrefix}', an equals sign, and a value.");
            }

            if (!names.Add(key))
            {
                return Reject(ReasonDuplicateField, $"Line {index} of the store's record repeats the field '{key}'.");
            }

            fields.Add(KeyValuePair.Create(key, lines[index][(equals + 1)..]));
        }

        if (fields[0].Key != IdField || ArchiveStoreId.TryParse(fields[0].Value) is not { } id)
        {
            return DecodeRejection.Create(ReasonBadId, "The store's record does not begin with its ID, 32 lowercase hexadecimal digits, not all zero.");
        }

        if (fields.Count < 2 || fields[1].Key != LayoutField || fields[1].Value.Length == 0)
        {
            return DecodeRejection.Create(ReasonBadLayout, "The store's record's second field is not its layout.");
        }

        var storeFields = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var parameters = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in fields.Skip(2))
        {
            if (key.StartsWith(ParameterPrefix, StringComparison.Ordinal))
            {
                parameters.Add(key[ParameterPrefix.Length..], value);
            }
            else
            {
                storeFields.Add(key, value);
            }
        }

        return new ArchiveStoreRecord(id, fields[1].Value, storeFields.ToImmutable(), parameters.ToImmutable());
    }

    /// <summary>
    /// Returns the first field, after the ID, in which <paramref name="stored"/> and <paramref name="process"/> differ,
    /// compared verbatim: the layout, then the store's fields, then the caller's parameters, each group as a map in ordinal
    /// order of its keys; a field one holds and the other lacks, or holds with another value. <see langword="null"/> when
    /// every field matches.
    /// </summary>
    public static ArchiveStoreRecordField? FirstDifference(ArchiveStoreRecord stored, ArchiveStoreRecord process)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(process);
        return FirstStoreDifference(stored, process.Layout, process.StoreFields)
            ?? (FirstDifference(stored.Parameters, process.Parameters) is { } parameter ? new ArchiveStoreRecordField(parameter, IsParameter: true) : null);
    }

    /// <summary>
    /// Throws unless the record's store fields, its layout and its other fields, are <paramref name="layout"/> and
    /// <paramref name="storeFields"/>, the store's own (archive.md §2.4, HI-I3); the caller's parameters are not compared.
    /// </summary>
    /// <param name="layout">The store's layout.</param>
    /// <param name="storeFields">The store's other fields, its <c>Plan</c> parameters.</param>
    /// <exception cref="ArchiveStoreMismatchException">A store field differs, which the exception names with both values.</exception>
    public void CheckStoreFieldsMatch(string layout, IReadOnlyDictionary<string, string> storeFields)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(storeFields);
        var process = storeFields.ToImmutableSortedDictionary(StringComparer.Ordinal);
        if (FirstStoreDifference(this, layout, process) is { } field)
        {
            throw field.Name == LayoutField
                ? new ArchiveStoreMismatchException(field, Layout, layout)
                : new ArchiveStoreMismatchException(field, StoreFields.GetValueOrDefault(field.Name), process.GetValueOrDefault(field.Name));
        }
    }

    /// <summary>
    /// Throws unless the record's caller parameters are <paramref name="parameters"/>, compared verbatim as a map
    /// (archive.md §2.4): the caller's own check, which the store never makes.
    /// </summary>
    /// <param name="parameters">The caller's opaque parameters.</param>
    /// <exception cref="ArgumentException">A parameter is not a valid field of a store's record, as <see cref="CheckParameters"/> finds.</exception>
    /// <exception cref="ArchiveStoreMismatchException">A parameter differs, which the exception names with both values.</exception>
    public void CheckParametersMatch(IReadOnlyDictionary<string, string> parameters)
    {
        CheckParameters(parameters);
        var process = parameters.ToImmutableSortedDictionary(StringComparer.Ordinal);
        if (FirstDifference(Parameters, process) is { } parameter)
        {
            throw new ArchiveStoreMismatchException(new ArchiveStoreRecordField(parameter, IsParameter: true), Parameters.GetValueOrDefault(parameter), process.GetValueOrDefault(parameter));
        }
    }

    /// <summary>Returns the record's bytes: its fields in the record's order.</summary>
    public byte[] Encode()
    {
        var text = new StringBuilder();
        _ = text.Append(IdField).Append('=').Append(Id.ToString()).Append('\n').Append(LayoutField).Append('=').Append(Layout);
        foreach (var (key, value) in StoreFields)
        {
            _ = text.Append('\n').Append(key).Append('=').Append(value);
        }

        foreach (var (key, value) in Parameters)
        {
            _ = text.Append('\n').Append(ParameterPrefix).Append(key).Append('=').Append(value);
        }

        return Strict.GetBytes(text.ToString());
    }

    private static ArchiveStoreRecordField? FirstStoreDifference(ArchiveStoreRecord stored, string layout, ImmutableSortedDictionary<string, string> storeFields)
    {
        if (!string.Equals(stored.Layout, layout, StringComparison.Ordinal))
        {
            return new ArchiveStoreRecordField(LayoutField, IsParameter: false);
        }

        return FirstDifference(stored.StoreFields, storeFields) is { } field ? new ArchiveStoreRecordField(field, IsParameter: false) : null;
    }

    private static string? FirstDifference(ImmutableSortedDictionary<string, string> stored, ImmutableSortedDictionary<string, string> process)
    {
        foreach (var key in stored.Keys.Union(process.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!stored.TryGetValue(key, out var left) || !process.TryGetValue(key, out var right) || !string.Equals(left, right, StringComparison.Ordinal))
            {
                return key;
            }
        }

        return null;
    }

    private static void CheckValue(string key, string value, string parameter)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameter, $"A store record's field '{key}' has no value.");
        }

        if (value.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException($"A store record's field '{key}' holds a line feed.", parameter);
        }

        try
        {
            _ = Strict.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException($"A store record's field '{key}' is not well-formed UTF-16.", parameter, exception);
        }
    }

    private static bool IsName(string key) => key.Length > 0 && !key.AsSpan().ContainsAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789-");

    private static InvalidDataException Reject(string reason, FormattableString message) =>
        DecodeRejection.Create(reason, message.ToString(CultureInfo.InvariantCulture));
}
