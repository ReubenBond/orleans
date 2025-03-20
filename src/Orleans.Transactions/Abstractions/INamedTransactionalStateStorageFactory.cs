// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Factory which creates an ITransactionalStateStorage by name.
    /// </summary>
    public interface INamedTransactionalStateStorageFactory
    {
        /// <summary>
        /// Create an ITransactionalStateStorage by name.
        /// </summary>
        /// <typeparam name="TState"></typeparam>
        /// <param name="storageName">Name of transaction state storage to create.</param>
        /// <param name="stateName">Name of transaction state.</param>
        /// <returns>ITransactionalStateStorage, null if not found.</returns>
        ITransactionalStateStorage<TState> Create<TState>(string storageName, string stateName) where TState : class, new();
    }
}
