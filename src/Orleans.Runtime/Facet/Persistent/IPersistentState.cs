// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Core;

namespace Orleans.Runtime
{
    /// <summary>
    /// Provides access to grain state with functionality to save, clear, and refresh the state.
    /// </summary>
    /// <typeparam name="TState">The underlying state type.</typeparam>
    /// <seealso cref="Orleans.Core.IStorage{TState}" />
    public interface IPersistentState<TState> : IStorage<TState>
    {
    }
}
