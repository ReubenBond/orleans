using System;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions;

public sealed class TransactionalStateFactory(IGrainContextAccessor contextAccessor) : ITransactionalStateFactory
{
    public ITransactionalState<TState> Create<TState>(TransactionalStateConfiguration config) where TState : class, new()
    {
        var currentContext = contextAccessor.GrainContext;
        TransactionalState<TState> transactionalState = ActivatorUtilities.CreateInstance<TransactionalState<TState>>(currentContext.ActivationServices, config, contextAccessor);
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
