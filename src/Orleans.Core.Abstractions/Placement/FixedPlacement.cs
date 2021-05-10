using System;
using System.Text;

namespace Orleans.Runtime
{
    [Serializable]
    [GenerateSerializer]
    public class FixedPlacement : PlacementStrategy
    {
        private const char SegmentSeparator = '+';
        internal static FixedPlacement Singleton { get; } = new FixedPlacement();

        public override bool IsUsingGrainDirectory => false;
        internal override bool IsDeterministicActivationId => true;

        /// <summary>
        /// Creates a new <see cref="GrainId"/> instance.
        /// </summary>
        public static GrainId CreateGrainId(GrainType kind, SiloAddress address) => new(kind, new IdSpan(address.ToUtf8String()));

        /// <summary>
        /// Creates a new <see cref="GrainId"/> instance.
        /// </summary>
        public static GrainId CreateGrainId(GrainType kind, SiloAddress address, string extraIdentifier)
        {
            var addr = address.ToUtf8String();
            if (extraIdentifier is string)
            {
                var extraLen = Encoding.UTF8.GetByteCount(extraIdentifier);
                var buf = new byte[addr.Length + 1 + extraLen];
                addr.CopyTo(buf.AsSpan());
                buf[addr.Length] = (byte)SegmentSeparator;
                Encoding.UTF8.GetBytes(extraIdentifier, 0, extraIdentifier.Length, buf, addr.Length + 1);
                addr = buf;
            }

            return new GrainId(kind, new IdSpan(addr));
        }

        /// <summary>
        /// Parses a silo address from a grain id.
        /// </summary>
        public static SiloAddress ParseSiloAddress(GrainId grainId)
        {
            var key = grainId.Key.AsSpan();
            if (key.IndexOf((byte)SegmentSeparator) is int index && index >= 0)
            {
                key = key.Slice(0, index);
            }

            return SiloAddress.FromUtf8String(key);
        }

        /// <summary>
        /// Returns a new <see cref="GrainId"/> targeting the provided address.
        /// </summary>
        public static GrainId UpdateSiloAddress(GrainId grainId, SiloAddress siloAddress)
        {
            var addr = siloAddress.ToUtf8String();
            var key = grainId.Key.AsSpan();
            if (key.IndexOf((byte)SegmentSeparator) is int index && index >= 0)
            {
                var extraIdentifier = key.Slice(index + 1);

                var buf = new byte[addr.Length + 1 + extraIdentifier.Length];
                addr.CopyTo(buf.AsSpan());
                buf[addr.Length] = (byte)SegmentSeparator;
                extraIdentifier.CopyTo(buf.AsSpan(addr.Length + 1));
                addr = buf;
            }

            return new GrainId(grainId.Type, new IdSpan(addr));
        }
    }
}
