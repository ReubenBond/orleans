using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal readonly struct ActivationId : IEquatable<ActivationId>
    {
        public static readonly ActivationId Zero = new ActivationId(Guid.Empty);
        
        public ActivationId(Guid key) => Value = key;

        public static ActivationId NewId() => new ActivationId(Guid.NewGuid());

        public readonly bool IsDefault => !Value.Equals(Guid.Empty);

        public readonly Guid Value { get; }

        public static ActivationId GetDeterministic(GrainId id)
        {
            var type = id.Type;
            var key = id.Key;
            var typeHash = type.GetHashCode();
            var keyHash = key.GetHashCode();
            
            var idGuid = new Guid(typeHash, (short)keyHash, (short)(keyHash >> 32), 0x00, 0x6f, 0x72, 0x6c, 0x65, 0x61, 0x6e, 0x73);
            return new ActivationId(idGuid);
        }

        public override readonly bool Equals(object obj) => obj is ActivationId activationId && this.Equals(activationId);

        public readonly bool Equals(ActivationId other) => Value.Equals(other.Value);

        public override readonly int GetHashCode() => this.Value.GetHashCode();

        public override readonly string ToString() => this.Value.ToString("N");
    }
}
