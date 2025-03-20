// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Serialization.Activators;

namespace Orleans.Serialization.Serializers
{
    /// <summary>
    /// Provides activators.
    /// </summary>
    public interface IActivatorProvider
    {
        /// <summary>
        /// Gets an activator for the specified type.
        /// </summary>
        /// <typeparam name="T">The type.</typeparam>
        /// <returns>The activator.</returns>
        IActivator<T> GetActivator<T>();
    }
}