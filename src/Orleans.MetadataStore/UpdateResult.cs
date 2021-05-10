using System;

namespace Orleans.MetadataStore
{
    [Serializable]
    [GenerateSerializer]
    public struct UpdateResult<TState>
    {
        public UpdateResult(bool success, TState value)
        {
            Success = success;
            Value = value;
        }

        [Id(0)]
        public TState Value { get; set; }

        [Id(1)]
        public bool Success { get; set; }

        public override string ToString()
        {
            return $"{nameof(UpdateResult<TState>)}({nameof(Success)}: {Success}, {nameof(Value)}: {Value})";
        }
    }
}