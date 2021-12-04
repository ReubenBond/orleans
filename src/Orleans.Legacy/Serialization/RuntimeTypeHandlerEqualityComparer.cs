using System;
using System.Collections.Generic;

namespace Orleans.Legacy.Serialization
{
    internal class RuntimeTypeHandlerEqualityComparer : IEqualityComparer<RuntimeTypeHandle>
    {
        public static RuntimeTypeHandlerEqualityComparer Instance { get; } = new RuntimeTypeHandlerEqualityComparer();


        RuntimeTypeHandlerEqualityComparer() { }

        public int GetHashCode(RuntimeTypeHandle handle) => handle.GetHashCode();

        public bool Equals(RuntimeTypeHandle first, RuntimeTypeHandle second) => first.Equals(second);
    }
}
