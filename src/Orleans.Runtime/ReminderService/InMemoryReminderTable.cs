using System;
using System.Threading.Tasks;
using System.Threading;

namespace Orleans.Runtime.ReminderService
{
    internal class InMemoryReminderTable : IReminderTable, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly TaskCompletionSource<bool> ready = new TaskCompletionSource<bool>();
        private readonly IReminderTableGrain reminderTableGrain;
        private bool shutdown;

        public InMemoryReminderTable(IGrainFactory grainFactory)
        {
            this.reminderTableGrain = grainFactory.GetGrain<IReminderTableGrain>(Constants.ReminderTableGrainId);
        }

        public Task Init() => Task.CompletedTask;

        public async Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            await this.WaitUntilReady();
            return await this.reminderTableGrain.ReadRow(grainRef, reminderName);
        }

        public async Task<ReminderTableData> ReadRows(GrainReference key)
        {
            await this.WaitUntilReady();
            return await this.reminderTableGrain.ReadRows(key);
        }

        public async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            await this.WaitUntilReady();
            return await this.reminderTableGrain.ReadRows(begin, end);
        }

        public async Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            await this.WaitUntilReady();
            return await this.reminderTableGrain.RemoveRow(grainRef, reminderName, eTag);
        }

        public async Task TestOnlyClearTable()
        {
            await this.WaitUntilReady();
            await this.reminderTableGrain.TestOnlyClearTable();
        }

        public async Task<string> UpsertRow(ReminderEntry entry)
        {
            await this.WaitUntilReady();
            return await this.reminderTableGrain.UpsertRow(entry);
        }

        private async Task WaitUntilReady()
        {
            await this.ready.Task;
            if (this.shutdown) ThrowShutdown();

            void ThrowShutdown() => throw new InvalidOperationException("The reminder service is not currently available.");
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            Task OnApplicationServicesStart(CancellationToken ct)
            {
                this.ready.TrySetResult(true);
                return Task.CompletedTask;
            }

            Task OnApplicationServicesStop(CancellationToken ct)
            {
                this.shutdown = true;
                this.ready.TrySetResult(true);
                return Task.CompletedTask;
            }

            lifecycle.Subscribe(
                nameof(GrainBasedReminderTable),
                ServiceLifecycleStage.ApplicationServices,
                OnApplicationServicesStart,
                OnApplicationServicesStop);
        }
    }
}
