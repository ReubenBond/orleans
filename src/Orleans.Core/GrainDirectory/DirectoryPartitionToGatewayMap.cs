#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.Membership;
using Orleans.Runtime.Utilities;

namespace Orleans.Runtime.GrainDirectory;
internal sealed class DirectoryPartitionToGatewayMap
{
    // See ConsistentRingOptions.DEFAULT_NUM_VIRTUAL_RING_BUCKETS
    private const int PartitionsPerSilo = 30;
    private readonly ImmutableArray<(uint Start, int MemberIndex, int PartitionIndex)> _ringBoundaries;
    private readonly ImmutableArray<GatewayMembershipEntry> _members;

    public DirectoryPartitionToGatewayMap(GatewayMembershipSnapshot snapshot) : this(snapshot, static (silo, count) => silo.GetUniformHashCodes(count))
    {
    }

    internal DirectoryPartitionToGatewayMap(GatewayMembershipSnapshot snapshot, Func<SiloAddress, int, uint[]> getRingBoundaries)
    {
        _members = ExtractAndSortMembers(snapshot);

        var hashIndexPairs = ImmutableArray.CreateBuilder<(uint Hash, int MemberIndex, int PartitionIndex)>(PartitionsPerSilo * _members.Length);
        for (var memberIndex = 0; memberIndex < _members.Length; memberIndex++)
        {
            var activeMember = _members[memberIndex];
            var hashCodes = getRingBoundaries(activeMember.SiloAddress, PartitionsPerSilo).ToList();
            hashCodes.Sort();
            Debug.Assert(hashCodes.Count == PartitionsPerSilo);
            for (var partitionIndex = 0; partitionIndex < hashCodes.Count; partitionIndex++)
            {
                var hashCode = hashCodes[partitionIndex];
                hashIndexPairs.Add((hashCode, memberIndex, partitionIndex));
            }
        }

        hashIndexPairs.Sort(static (left, right) =>
        {
            var hashCompare = left.Hash.CompareTo(right.Hash);
            if (hashCompare != 0)
            {
                return hashCompare;
            }

            var partitionCompare = left.PartitionIndex.CompareTo(right.PartitionIndex);
            if (partitionCompare != 0)
            {
                return partitionCompare;
            }

            return left.MemberIndex.CompareTo(right.MemberIndex);
        });

        Dictionary<int, ImmutableArray<RingRange>.Builder> rangesByMemberPartitionBuilders = [];
        for (var i = 0; i < hashIndexPairs.Count; i++)
        {
            var (_, memberIndex, _) = hashIndexPairs[i];
            ref var builder = ref CollectionsMarshal.GetValueRefOrAddDefault(rangesByMemberPartitionBuilders, memberIndex, out _);
            builder ??= ImmutableArray.CreateBuilder<RingRange>(PartitionsPerSilo);
            var (entryStart, _, _) = hashIndexPairs[i];
            var (nextStart, _, _) = hashIndexPairs[(i + 1) % hashIndexPairs.Count];
            var range = (entryStart == nextStart) switch
            {
                true when hashIndexPairs.Count == 1 => RingRange.Full,
                true => RingRange.Empty,
                _ => RingRange.Create(entryStart, nextStart)
            };
            builder.Add(range);
        }

        // Remove empty ranges.
        if (hashIndexPairs.Count > 1)
        {
            for (var i = 1; i < hashIndexPairs.Count;)
            {
                if (hashIndexPairs[i].Hash == hashIndexPairs[i - 1].Hash)
                {
                    hashIndexPairs.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }
        }

        _ringBoundaries = hashIndexPairs.ToImmutable();
    }

    private static ImmutableArray<GatewayMembershipEntry> ExtractAndSortMembers(GatewayMembershipSnapshot snapshot)
    {
        var sortedActiveMembers = ImmutableArray.CreateBuilder<GatewayMembershipEntry>(snapshot.Members.Count(static m => m.Status == SiloStatus.Active));
        foreach (var member in snapshot.Members)
        {
            // Only active members are part of directory membership.
            if (member.Status == SiloStatus.Active)
            {
                sortedActiveMembers.Add(member);
            }
        }

        sortedActiveMembers.Sort(static (left, right) => left.SiloAddress.CompareTo(right.SiloAddress));
        return sortedActiveMembers.ToImmutable();
    }


    public static DirectoryPartitionToGatewayMap Default { get; } = new DirectoryPartitionToGatewayMap(new GatewayMembershipSnapshot([], MembershipVersion.MinValue), null!);

    private RingRange GetRangeCore(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _ringBoundaries.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 0);

        var (entryStart, _, _) = _ringBoundaries[index];
        var (nextStart, _, _) = _ringBoundaries[(index + 1) % _ringBoundaries.Length];
        if (entryStart == nextStart)
        {
            // Handle hash collisions by making subsequent adjacent ranges empty.
            if (_ringBoundaries.Length == 1)
            {
                return RingRange.Full;
            }
            else
            {
                // Handle hash collisions by making subsequent adjacent ranges empty.
                return RingRange.Empty;
            }
        }

        return RingRange.Create(entryStart, nextStart);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetGateway(GrainId grainId, [NotNullWhen(true)] out SiloAddress? owner) => TryGetGateway(grainId.GetUniformHashCode(), out owner);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetGateway(uint hashCode, [NotNullWhen(true)] out SiloAddress? owner)
    {
        var index = SearchAlgorithms.RingRangeBinarySearch(
            _ringBoundaries.Length,
            this,
            static (collection, index) => collection.GetRangeCore(index),
            hashCode);
        if (index >= 0)
        {
            var (_, memberIndex, partitionIndex) = _ringBoundaries[index];
            owner = _members[memberIndex].GatewayAddress;
            return owner.Endpoint.Port != 0;
        }

        Debug.Assert(_members.Length == 0);
        owner = null;
        return false;
    }
}
