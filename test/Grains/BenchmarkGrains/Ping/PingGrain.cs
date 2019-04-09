using Orleans;
using BenchmarkGrainInterfaces.Ping;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;

namespace BenchmarkGrains.Ping
{
    public class PingGrain : Grain, IPingGrain
    {
        private IPingGrain self;

        public override Task OnActivateAsync()
        {
            this.self = this.AsReference<IPingGrain>();
            return base.OnActivateAsync();
        }

        public Task Run()
        {
            return Task.CompletedTask;
        }

        public Task PingPongInterleave(IPingGrain other, int count)
        {
            if (count == 0) return Task.CompletedTask;
            return other.PingPongInterleave(this.self, count - 1);
        }
    }

    public interface IHashGrain : IGrainWithIntegerKey
    {
        Task Create(HashSet<Guid> data);
        Task CreateIEnumerable(IEnumerable<Guid> data);
    }

    public class HashGrain : Grain, IHashGrain, IIncomingGrainCallFilter
    {
        public Task Create(HashSet<Guid> data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            return Task.CompletedTask;
        }

        public Task CreateIEnumerable(IEnumerable<Guid> data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            return Task.CompletedTask;
        }

        public Task Invoke(IIncomingGrainCallContext context)
        {
            if (context.InterfaceMethod == null)
                throw new ArgumentNullException("InterfaceMethod");

            return context.Invoke();
        }
    }
}
