using System.Diagnostics;
using System.Globalization;
using System.Text;
using Orleans.Durable.Storage.Engine.Buffers;

using Orleans.Durable.Storage.Engine.Wal;

namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <summary>
/// What decoding the node file gives (conventions.md §15): the node identity, or the <see cref="InvalidDataException"/>
/// naming the first rule its contents break, with the rule's reason code (ledgerstore.md §7.1).
/// </summary>
internal readonly union NodeFileDecoding(NodeId, InvalidDataException);

/// <summary>
/// The node file, <c>wal/node</c>: the node identity as exactly 32 lowercase hexadecimal ASCII digits, published once,
/// last in the store's creation, after WAL file 1 and the empty catalog checkpoint are durable, and never written again
/// (ledgerstore.md §7.1, LS-I6).
/// </summary>
internal static class NodeFile
{
    internal const string Name = "node";

    private const int Length = 32;

    /// <summary>
    /// Encodes a node identity as the node file's contents.
    /// </summary>
    public static byte[] Encode(NodeId node) => Encoding.ASCII.GetBytes(node.Value.ToString("N", CultureInfo.InvariantCulture));

    /// <summary>
    /// Decodes the node file's contents: exactly 32 lowercase hexadecimal digits naming a non-empty UUID, or the
    /// rejection naming the first rule they break: <c>digits</c>, then <c>empty-uuid</c>.
    /// </summary>
    public static NodeFileDecoding Decode(ReadOnlySpan<byte> contents)
    {
        if (contents.Length != Length || contents.ContainsAnyExcept("0123456789abcdef"u8))
        {
            return Rejection("digits");
        }

        var value = Guid.ParseExact(Encoding.ASCII.GetString(contents), "N");
        if (value == Guid.Empty)
        {
            return Rejection("empty-uuid");
        }

        return new NodeId(value);
    }

    /// <summary>
    /// Reads the node file, returning <see langword="null"/> when there is none.
    /// </summary>
    /// <exception cref="LedgerReplicaCorruptException">The node file is damaged.</exception>
    public static async ValueTask<NodeId?> ReadAsync(WalDirectory directory, string root, CancellationToken cancellationToken)
    {
        WalFile file;
        try
        {
            file = await directory.OpenNamedFileAsync(Name, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        await using (file.ConfigureAwait(false))
        {
            // Contents of another length cannot be 32 digits, so the store reads none of them.
            if (file.Length != Length)
            {
                throw Damaged(root, Rejection("digits"));
            }

            using var contents = await file.ReadAsync(0, Length, cancellationToken).ConfigureAwait(false);
            var decoding = Decode(contents.ToArray());
            Debug.Assert(decoding.Value is not null, "conventions.md §15: a decoder answers a union which holds a case.");
            return decoding switch
            {
                NodeId node => node,
                InvalidDataException rejection => throw Damaged(root, rejection),
                null => throw new UnreachableException(),
            };
        }
    }

    /// <summary>
    /// Creates the node file holding <paramref name="node"/>; its contents and name become durable together.
    /// </summary>
    public static async ValueTask CreateAsync(WalDirectory directory, NodeId node, CancellationToken cancellationToken)
    {
        var bytes = Encode(node);
        using var writer = ArcBufferWriter.CreateAligned();
        writer.Write(bytes);
        using var contents = writer.ConsumeSlice(bytes.Length);
        var file = await directory.CreateNamedFileAsync(Name, contents, cancellationToken).ConfigureAwait(false);
        await file.DisposeAsync().ConfigureAwait(false);
    }

    private static InvalidDataException Rejection(string reason) => DecodeRejection.Create(reason, reason switch
    {
        "digits" => "The LedgerStore node file does not hold 32 lowercase hexadecimal digits.",
        "empty-uuid" => "The LedgerStore node file names the empty UUID.",
        _ => throw new UnreachableException(),
    });

    private static LedgerReplicaCorruptException Damaged(string root, InvalidDataException exception) =>
        new($"The LedgerStore at '{root}' holds a damaged node file, so it cannot establish its node identity.", exception);
}
