using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Orleans.Runtime
{
    [Serializable]
    [GenerateSerializer]
    internal readonly struct CorrelationId : IEquatable<CorrelationId>, IComparable<CorrelationId>
    {
        private static readonly CorrelationIdRecord[] PerCoreValues;

        [StructLayout(LayoutKind.Explicit, Size = 64)]
        private struct CorrelationIdRecord
        {
            [FieldOffset(0)]
            public long NextId;
        }

        static CorrelationId()
        {
            PerCoreValues = new CorrelationIdRecord[Environment.ProcessorCount];
            for (var i = 0; i < PerCoreValues.Length; i++)
            {
                PerCoreValues[i].NextId = (i << 56) + 1;
            }
        }

        [Id(1)]
        private readonly long _id;

        public CorrelationId(long value)
        {
            _id = value;
        }

        public CorrelationId(CorrelationId other)
        {
            _id = other._id;
        }

        public int GetSlotId() => (int)(_id >> 56);

        public static CorrelationId GetNext()
        {
            var procId = Thread.GetCurrentProcessorId();
            if (procId >= PerCoreValues.Length)
            {
                procId = 0;
            }

            var slot = PerCoreValues[procId];
            var result = Interlocked.Increment(ref slot.NextId);
            return new CorrelationId(result);
        }

        public override int GetHashCode()
        {
 	        return _id.GetHashCode();
        }

        public override bool Equals(object obj)
        {
            if (obj is not CorrelationId correlationId)
            {
                return false;
            }

            return this.Equals(correlationId);
        }

        public bool Equals(CorrelationId other)
        {
            return _id == other._id;
        }

        public static bool operator ==(CorrelationId lhs, CorrelationId rhs)
        {
            return rhs._id == lhs._id;
        }

        public static bool operator !=(CorrelationId lhs, CorrelationId rhs)
        {
            return rhs._id != lhs._id;
        }

        public int CompareTo(CorrelationId other)
        {
            return _id.CompareTo(other._id);
        }

        public override string ToString()
        {
            return _id.ToString();
        }

        internal long ToInt64() => this._id;
    }
}
