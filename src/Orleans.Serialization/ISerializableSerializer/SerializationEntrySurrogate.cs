// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Serialization
{
    [GenerateSerializer]
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