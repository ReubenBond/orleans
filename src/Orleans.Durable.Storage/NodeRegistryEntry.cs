using System.Buffers;
using System.Globalization;
using System.Text;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Runtime;

namespace Orleans.Durable.Storage;

/// <summary>
/// One node's entry in the node registry (orleans.md §5.3, §7.3): its identity, the silo which serves it, its StatefulSet
/// ordinal and failure domain when it has them, and, once it is retired, the revision of the registry write which retired
/// it.
/// </summary>
/// <remarks>
/// A registration carries the entry its silo would hold, active. The node registry committer alone changes an entry, and
/// only to a strictly greater generation of the same node's silo, or from active to retired (OR-I2). Orleans builds a
/// deserialized entry without its constructor, so the committer checks every entry it receives with
/// <see cref="Validate"/>.
/// </remarks>
[GenerateSerializer]
[Immutable]
internal sealed record NodeRegistryEntry
{
    /// <summary>Initializes a new instance of the <see cref="NodeRegistryEntry"/> class.</summary>
    /// <param name="node">The node identity.</param>
    /// <param name="silo">The silo serving the node: its IP endpoint and its positive generation.</param>
    /// <param name="ordinal">The node's StatefulSet ordinal, non-negative, or <see langword="null"/> when it has none.</param>
    /// <param name="failureDomain">The node's failure domain, non-empty, or <see langword="null"/> when none is configured.</param>
    /// <param name="retiredRevision">The revision of the registry write which retired the node; 0 while it is active.</param>
    /// <param name="diskless">Whether the silo runs without a local <c>Root</c> and holds no <c>LedgerStore</c> (orleans.md §5.14).</param>
    /// <exception cref="ArgumentException">The entry breaks a rule of <see cref="Validate"/>.</exception>
    public NodeRegistryEntry(NodeId node, SiloAddress silo, int? ordinal = null, string? failureDomain = null, ulong retiredRevision = 0, bool diskless = false)
    {
        Node = node;
        Silo = silo;
        Ordinal = ordinal;
        FailureDomain = failureDomain;
        RetiredRevision = retiredRevision;
        Diskless = diskless;
        Validate();
    }

    /// <summary>Gets the node identity.</summary>
    [Id(0)]
    public NodeId Node { get; }

    /// <summary>Gets the silo serving the node: its IP endpoint and generation.</summary>
    [Id(1)]
    public SiloAddress Silo { get; }

    /// <summary>Gets the node's StatefulSet ordinal, or <see langword="null"/> when it has none.</summary>
    [Id(2)]
    public int? Ordinal { get; }

    /// <summary>Gets the node's failure domain, or <see langword="null"/> when none is configured.</summary>
    [Id(3)]
    public string? FailureDomain { get; }

    /// <summary>Gets the revision of the registry write which retired the node; 0 while it is active.</summary>
    [Id(4)]
    public ulong RetiredRevision { get; }

    /// <summary>Gets a value indicating whether the node is diskless (no <c>Root</c> or <c>LedgerStore</c>; orleans.md §5.14).</summary>
    [Id(5)]
    public bool Diskless { get; }

    /// <summary>The longest failure domain an entry holds, in UTF-8 bytes, which every registration reserves (orleans.md §7.3).</summary>
    public const int MaxFailureDomainBytes = 64;

    /// <summary>Gets a value indicating whether the node is retired.</summary>
    public bool IsRetired => RetiredRevision != 0;

    /// <summary>
    /// Throws unless the entry is one <c>CLNR</c> can hold (orleans.md §7.3): a node identity; a silo whose generation is
    /// positive and whose endpoint is as <see cref="SiloAddress"/> holds it, an IPv4-mapped address as IPv4, with no IPv6 scope ID; a non-negative
    /// ordinal; and a failure domain which is non-empty, well-formed UTF-16, and at most
    /// <see cref="MaxFailureDomainBytes"/> bytes in UTF-8.
    /// </summary>
    /// <exception cref="ArgumentException">The entry breaks one of these rules.</exception>
    public void Validate()
    {
        if (Node == default)
        {
            throw new ArgumentException("An entry names a node.", nameof(Node));
        }

        if (Silo is null)
        {
            throw new ArgumentException("An entry names its silo.", nameof(Silo));
        }

        if (Silo.Generation <= 0)
        {
            throw new ArgumentException("An entry's silo generation is positive.", nameof(Silo));
        }

        if (!NodeRegistryCodec.IsSiloEndpoint(Silo.Endpoint))
        {
            throw new ArgumentException("An entry's silo endpoint is as a SiloAddress holds it, an IPv4-mapped address as IPv4, with no IPv6 scope ID.", nameof(Silo));
        }

        if (Ordinal is < 0)
        {
            throw new ArgumentException("An entry's ordinal is non-negative.", nameof(Ordinal));
        }

        if (Diskless && Ordinal is not null)
        {
            throw new ArgumentException("A diskless node holds no StatefulSet ordinal (orleans.md §5.14).", nameof(Ordinal));
        }

        if (FailureDomain is { } domain && (domain.Length == 0 || !IsWellFormed(domain) || Encoding.UTF8.GetByteCount(domain) > MaxFailureDomainBytes))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"A configured failure domain is non-empty, well-formed UTF-16, and at most {MaxFailureDomainBytes} bytes in UTF-8."), nameof(FailureDomain));
        }
    }

    /// <summary>Returns this entry retired at <paramref name="revision"/>.</summary>
    internal NodeRegistryEntry Retire(ulong revision) => new(Node, Silo, Ordinal, FailureDomain, revision, Diskless);

    /// <summary>
    /// Returns this entry at <paramref name="silo"/>, every other field kept: a retired node's restart, which holds no
    /// ordinal and is never placed, so its ordinal and failure domain stay as they were retired (orleans.md §5.3).
    /// </summary>
    internal NodeRegistryEntry WithSilo(SiloAddress silo) => new(Node, silo, Ordinal, FailureDomain, RetiredRevision, Diskless);

    /// <summary>Returns whether <paramref name="text"/> holds no lone surrogate, so its UTF-8 encoding is lossless.</summary>
    private static bool IsWellFormed(ReadOnlySpan<char> text)
    {
        while (!text.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(text, out _, out var consumed) != OperationStatus.Done)
            {
                return false;
            }

            text = text[consumed..];
        }

        return true;
    }
}
