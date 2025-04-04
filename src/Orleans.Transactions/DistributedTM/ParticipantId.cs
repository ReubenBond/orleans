using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Transactions;

[GenerateSerializer, Immutable]
public readonly struct ParticipantId
{
    public static readonly IEqualityComparer<ParticipantId> Comparer = new IdComparer();

    [GenerateSerializer]
    [Flags]
    public enum Role
    {
        Resource = 1 << 0,
        Manager = 1 << 1,
        PriorityManager = 1 << 2
    }

    [Id(0)]
    public string Name { get; }

    [Id(1)]
    public GrainReference Id { get; }

    [Id(2)]
    public Role SupportedRoles { get; }

    public ParticipantId(string name, GrainReference id, Role supportedRoles)
    {
        Name = name;
        Id = id;
        SupportedRoles = supportedRoles;
    }

    public override string ToString() => $"ParticipantId.{Name}.{Id}";

    [GenerateSerializer, Immutable]
    public sealed class IdComparer : IEqualityComparer<ParticipantId>
    {
        public bool Equals(ParticipantId x, ParticipantId y) => string.CompareOrdinal(x.Name, y.Name) == 0 && Equals(x.Id, y.Id);

        public int GetHashCode(ParticipantId obj) => HashCode.Combine(obj.Name, obj.Id);
    }
}

internal static class ParticipantRoleExtensions
{
    private static bool SupportsRoles(this ParticipantId participant, ParticipantId.Role role) => (participant.SupportedRoles & role) != 0;

    public static bool IsResource(this ParticipantId participant) => participant.SupportsRoles(ParticipantId.Role.Resource);

    public static bool IsManager(this ParticipantId participant) => participant.SupportsRoles(ParticipantId.Role.Manager);

    public static bool IsPriorityManager(this ParticipantId participant) => participant.SupportsRoles(ParticipantId.Role.PriorityManager);
}
