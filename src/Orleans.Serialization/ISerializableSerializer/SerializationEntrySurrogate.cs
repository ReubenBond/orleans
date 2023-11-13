using System;

namespace Orleans.Serialization
{
    [GenerateSerializer]
    [Alias("Orleans.Serialization.SerializationEntrySurrogate")]
    internal struct SerializationEntrySurrogate
    {
        [Id(0)]
        public string Name;

        [Id(1)]
        public object Value;

        [Id(2)]
        public Type ObjectType;
    }
}