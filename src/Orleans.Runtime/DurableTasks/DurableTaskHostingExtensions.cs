#nullable enable
using System;
using System.Distributed.DurableTasks.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration.Internal;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.DurableTasks;

namespace Orleans.DurableTasks;

public static class DurableTaskHostingExtensions
{
    public static ISiloBuilder AddDurableTasks(this ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.AddSingleton<DurableTaskGrainRuntimeShared>();
        siloBuilder.Services.AddScoped<DurableTaskGrainRuntime>();
        siloBuilder.Services.AddFromExisting<IDurableTaskGrainRuntime, DurableTaskGrainRuntime>();
        siloBuilder.Services.AddKeyedTransient<IGrainExtension>(typeof(IDurableTaskGrainExtension), (sp, _) => sp.GetRequiredService<DurableTaskGrainRuntime>());
        siloBuilder.Services.AddKeyedTransient<IGrainExtension>(typeof(IDurableTaskServer), (sp, _) => sp.GetRequiredService<DurableTaskGrainRuntime>());
        siloBuilder.Services.AddKeyedTransient<IGrainExtension>(typeof(IDurableTaskObserver), (sp, _) => sp.GetRequiredService<DurableTaskGrainRuntime>());

        siloBuilder.Services.AddSingleton<DefaultRetryPolicy>();
        siloBuilder.Services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        return siloBuilder;
    }
}
