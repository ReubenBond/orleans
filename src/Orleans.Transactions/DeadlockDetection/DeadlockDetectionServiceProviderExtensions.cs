using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans.Transactions.DeadlockDetection
{
    public static class DeadlockDetectionServiceProviderExtensions
    {
        public static IServiceCollection UseTransactionalDeadlockDetection(this IServiceCollection serviceCollection) =>
            serviceCollection
                .AddOptions<DeadlockDetectionOptions>()
                .Services
                .AddSingleton<DeadlockDetectionLockObserver>()
                .AddSingleton<ITransactionalLockObserver>(sp => sp.GetRequiredService<DeadlockDetectionLockObserver>())
                .AddSingleton<ILocalDeadlockDetector>(sp => sp.GetRequiredService<DeadlockDetectionLockObserver>())
                .AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(sp => sp.GetRequiredService<DeadlockDetectionLockObserver>());
    }
}