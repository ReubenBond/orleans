// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Orleans.Serialization;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions;

public class TransactionalStateFactory : ITransactionalStateFactory
{
    private readonly IGrainContextAccessor contextAccessor;
    public TransactionalStateFactory(IGrainContextAccessor contextAccessor)
    {
        this.contextAccessor = contextAccessor;
    }

    public ITransactionalState<TState> Create<TState>(TransactionalStateConfiguration config) where TState : class, new()
    {
        var currentContext = this.contextAccessor.GrainContext;
        TransactionalState<TState> transactionalState = ActivatorUtilities.CreateInstance<TransactionalState<TState>>(currentContext.ActivationServices, config, this.contextAccessor);
        transactionalState.Participate(currentContext.ObservableLifecycle);
        return transactionalState;
    }

    public static JsonSerializerSettings GetJsonSerializerSettings(IServiceProvider serviceProvider)
    {
        var serializerSettings = OrleansJsonSerializerSettings.GetDefaultSerializerSettings(serviceProvider);
        serializerSettings.PreserveReferencesHandling = PreserveReferencesHandling.None;
        return serializerSettings;
    }
}
