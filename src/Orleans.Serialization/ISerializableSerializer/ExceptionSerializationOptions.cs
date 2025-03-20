// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

namespace Orleans.Serialization
{
    /// <summary>
    /// Options for exception serialization.
    /// </summary>
    public class ExceptionSerializationOptions
    {
        /// <summary>
        /// Gets the collection of supported namespace prefixes for the exception serializer.
        /// Any exception type which has a namespace with one of these prefixes will be serialized using the exception serializer.
        /// </summary>
        public HashSet<string> SupportedNamespacePrefixes { get; } = new HashSet<string>(StringComparer.Ordinal) { "Microsoft", "System", "Azure" };

        /// <summary>
        /// Gets or sets the predicate used to enable serialization for an exception type.
        /// </summary>
        public Func<Type, bool> SupportedExceptionTypeFilter { get; set; } = _ => false;
    }
}