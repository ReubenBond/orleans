using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Orleans.Journaling;
public static class HostingExtensions
{
    public static ISiloBuilder AddStateMachineStorage(this ISiloBuilder builder)
    {
        builder.Services.TryAddScoped<IStateMachineStorage>(sp => sp.GetRequiredService<IStateMachineStorageProvider>().Create(sp.GetRequiredService<IGrainContext>()));
        builder.Services.TryAddScoped<IStateMachineManager, StateMachineManager>();
        builder.Services.TryAddTransient(typeof(DurableDictionary<,>));
        builder.Services.TryAddTransient(typeof(DurableList<>));
        builder.Services.TryAddTransient(typeof(DurableSet<>));
        builder.Services.TryAddTransient(typeof(DurableQueue<>));
        builder.Services.TryAddTransient(typeof(DurableValue<>));
        builder.Services.TryAddTransient(typeof(DurableTaskCompletionSource<>));
        return builder;
    }
}
