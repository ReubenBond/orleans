// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Streams
{
    /// <summary>
    /// A stream namespace predicate which matches all namespaces.
    /// </summary>
    internal class AllStreamNamespacesPredicate : IStreamNamespacePredicate
    {
        /// <inheritdoc/>
        public string PredicatePattern => "*";

        /// <inheritdoc/>
        public bool IsMatch(string streamNamespace)
        {
            return true;
        }
    }
}