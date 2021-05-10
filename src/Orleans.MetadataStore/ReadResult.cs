using System;

namespace Orleans.MetadataStore
{
    [Serializable]
    [GenerateSerializer]
    public struct ReadResult<TValue> where TValue : class
    {
        public ReadResult(bool success, TValue value)
        {
            Success = success;
            Value = value;
        }

        [Id(0)]
        public TValue Value { get; set; }

        [Id(1)]
        public bool Success { get; set; }

        public void Deconstruct(out bool success, out TValue value)
        {
            success = Success;
            value = Value;
        }

        public override string ToString()
        {
            return $"{nameof(ReadResult<TValue>)}({nameof(Success)}: {Success}, {nameof(Value)}: {Value})";
        }
    }
}