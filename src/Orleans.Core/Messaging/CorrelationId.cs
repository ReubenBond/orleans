using System;
using System.Threading;

namespace Orleans.Runtime
{
    [Serializable]
    [GenerateSerializer]
    internal readonly struct CorrelationId : IEquatable<CorrelationId>, IComparable<CorrelationId>
    {
        private static long _nextToUse = 1;

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

        public int GetSlotId() => ((int)_id) & 0x0000FFFF;
        
        public static CorrelationId GetNext()
        {
            var val = Interlocked.Increment(ref _nextToUse) << 16;
            var procId = (long)Thread.GetCurrentProcessorId();
            var result = val | procId;
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
