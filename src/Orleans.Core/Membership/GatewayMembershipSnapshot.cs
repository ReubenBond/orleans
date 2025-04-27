#nullable enable
using System;
using System.Collections.Immutable;
using System.Text;
using Orleans.Runtime;

namespace Orleans.Membership;

/// <summary>
/// Represents a snapshot of cluster membership from the perspective of gateways.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="GatewayMembershipSnapshot"/> class.
/// </remarks>
/// <param name="members">The cluster members.</param>
/// <param name="version">The cluster membership version.</param>
internal sealed class GatewayMembershipSnapshot(ImmutableArray<GatewayMembershipEntry> members, MembershipVersion version)
{
    internal static GatewayMembershipSnapshot Default => new([], MembershipVersion.MinValue);

    /// <summary>
    /// Gets the cluster members.
    /// </summary>
    /// <value>The cluster members.</value>
    [Id(0)]
    public ImmutableArray<GatewayMembershipEntry> Members { get; } = members;

    /// <summary>
    /// Gets the cluster membership version.
    /// </summary>
    /// <value>The cluster membership version.</value>
    [Id(1)]
    public MembershipVersion Version { get; } = version;

    /// <summary>
    /// Returns a <see cref="GatewayMembershipUpdate"/> which represents this instance.
    /// </summary>
    /// <returns>A <see cref="GatewayMembershipUpdate"/> which represents this instance.</returns>
    public GatewayMembershipUpdate AsUpdate() => new(Members, Version);

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"GatewayMembershipSnapshot {{ Version = {Version}, Members.Count = {Members.Length}, Members = [");
        var first = true;
        foreach (var member in Members)
        {
            if (first)
            {
                first = false;
            }
            else
            {
                sb.Append(", ");
            }

            sb.Append(member);
        }

        sb.Append("] }}");
        return sb.ToString();
    }
}

/// <summary>
/// Represents a cluster gateway member.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="GatewayMembershipEntry"/> class.
/// </remarks>                
/// <param name="siloAddress">
/// The silo address.
/// </param>
/// <param name="status">
/// The silo status.
/// </param>
/// <param name="name">
/// The silo name.
/// </param>
/// <param name="gatewayAddress">
/// The gateway address.
/// </param>
[GenerateSerializer, Immutable, Alias(nameof(GatewayMembershipEntry))]
public sealed class GatewayMembershipEntry(SiloAddress siloAddress, SiloStatus status, string name, SiloAddress gatewayAddress) : IEquatable<GatewayMembershipEntry>
{
    /// <summary>
    /// Gets the identity of the silo.
    /// </summary>
    /// <value>The silo address.</value>
    [Id(0)]
    public SiloAddress SiloAddress { get; } = siloAddress ?? throw new ArgumentNullException(nameof(siloAddress));

    /// <summary>
    /// Gets the silo status.
    /// </summary>
    /// <value>The silo status.</value>
    [Id(1)]
    public SiloStatus Status { get; } = status;

    /// <summary>
    /// Gets the silo name.
    /// </summary>
    /// <value>The silo name.</value>
    [Id(2)]
    public string Name { get; } = name;

    /// <summary>
    /// Gets the gateway address.
    /// </summary>
    /// <value>The gateway address.</value>
    [Id(3)]
    public SiloAddress GatewayAddress { get; } = gatewayAddress;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as GatewayMembershipEntry);

    /// <inheritdoc/>
    public bool Equals(GatewayMembershipEntry? other) => other != null
        && SiloAddress.Equals(other.SiloAddress)
        && Equals(GatewayAddress, other.GatewayAddress)
        && Status == other.Status
        && string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(SiloAddress, GatewayAddress, Status);

    /// <inheritdoc/>
    public override string ToString() => GatewayAddress is not null ? $"{SiloAddress}/{Name}/{Status}" : $"{SiloAddress}/{Name}/{Status}/{GatewayAddress}";
}

/// <summary>
/// Represents changes from a previous snapshot.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="GatewayMembershipUpdate"/> class.
/// </remarks>
/// <param name="changes">The changes.</param>
/// <param name="version">The version.</param>
[GenerateSerializer, Immutable, Alias(nameof(GatewayMembershipUpdate))]
public sealed class GatewayMembershipUpdate(ImmutableArray<GatewayMembershipEntry> changes, MembershipVersion version)
{
    /// <summary>
    /// Gets a value indicating whether this instance has changes.
    /// </summary>
    /// <value><see langword="true"/> if this instance has changes; otherwise, <see langword="false"/>.</value>
    public bool HasChanges => !Changes.IsDefaultOrEmpty;

    /// <summary>
    /// Gets the changes.
    /// </summary>
    /// <value>The changes.</value>
    [Id(0)]
    public ImmutableArray<GatewayMembershipEntry> Changes { get; } = changes;

    /// <summary>
    /// Gets the cluster membership version.
    /// </summary>
    /// <value>The cluster membership version.</value>
    [Id(1)]
    public MembershipVersion Version { get; } = version;
}
