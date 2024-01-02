using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration.Internal;
using Orleans.DurableTasks.Remoting;
using Orleans.Runtime;

namespace Orleans.DurableTasks;

public static class DurableTaskHostingExtensions
{
    public static ISiloBuilder AddDurableTasks(this ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.AddSingleton<DurableTaskGrainExtensionShared>();
        siloBuilder.Services.AddScoped<DurableTaskGrainExtension>();
        siloBuilder.Services.AddFromExisting<IDurableTaskGrainRuntime, DurableTaskGrainExtension>();
        siloBuilder.Services.AddKeyedTransient<IGrainExtension>(typeof(IDurableTaskGrainExtension), (sp, _) => sp.GetRequiredService<DurableTaskGrainExtension>());
        siloBuilder.Services.AddKeyedTransient<IGrainExtension>(typeof(IDurableTaskServer), (sp, _) => sp.GetRequiredService<DurableTaskGrainExtension>());
        siloBuilder.Services.AddKeyedTransient<IGrainExtension>(typeof(IDurableTaskClient), (sp, _) => sp.GetRequiredService<DurableTaskGrainExtension>());

        siloBuilder.Services.AddSingleton<DefaultRetryPolicy>();
        siloBuilder.Services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        return siloBuilder;
    }
}
