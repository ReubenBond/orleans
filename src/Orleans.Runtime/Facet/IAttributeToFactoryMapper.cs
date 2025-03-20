// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

namespace Orleans.Runtime
{
    /// <summary>
    /// Responsible for mapping a facet metadata to a cachable factory.
    /// </summary>
    public interface IAttributeToFactoryMapper<in TMetadata>
        where TMetadata : IFacetMetadata
    {
        /// <summary>
        /// Responsible for mapping a facet metadata to a cachable factory from the parameter and facet metadata.
        /// </summary>
        /// <param name="parameter">The parameter info.</param>
        /// <param name="metadata">The metadata.</param>
        /// <returns>The factory used to create facet instances for a grain.</returns>
        Factory<IGrainContext, object> GetFactory(ParameterInfo parameter, TMetadata metadata);
    }
}
