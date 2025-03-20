// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace Orleans.Serialization.TypeSystem
{
    /// <summary>
    /// Type which allows any exception type to be resolved.
    /// </summary>
    public sealed class DefaultTypeFilter : ITypeNameFilter
    {
        /// <inheritdoc/>
        public bool? IsTypeNameAllowed(string typeName, string assemblyName)
        {
            if (assemblyName is { } && assemblyName.Contains("Orleans.Serialization", StringComparison.Ordinal))
            {
                return true;
            }

            if (typeName.EndsWith(nameof(Exception), StringComparison.Ordinal))
            {
                return true;
            }

            if (typeName.StartsWith("System.", StringComparison.Ordinal))
            {
                if (typeName.EndsWith("Comparer", StringComparison.Ordinal))
                {
                    return true;
                }

                if (typeName.StartsWith("System.Collections.", StringComparison.Ordinal))
                {
                    return true;
                }

                if (typeName.StartsWith("System.Net.IP", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return null;
        }
    }
}