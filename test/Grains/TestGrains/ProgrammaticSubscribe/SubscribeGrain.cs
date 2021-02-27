using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams.Core;
using Orleans.Streams.PubSub;
using System.IO;

namespace UnitTests.Grains.ProgrammaticSubscribe
{
    public interface ISubscribeGrain : IGrainWithGuidKey
    {
        Task<bool> CanGetSubscriptionManager(string providerName);
    }

    public class SubscribeGrain : Grain, ISubscribeGrain
    {
        public Task<bool> CanGetSubscriptionManager(string providerName)
        {
            return Task.FromResult(this.ServiceProvider.GetServiceByName<IStreamProvider>(providerName).TryGetStreamSubscrptionManager(out _));
        }
    }

    [Serializable]
    [Hagar.GenerateSerializer]
    public class FullStreamIdentity : IStreamIdentity
    {
        public FullStreamIdentity(Guid streamGuid, string streamNamespace, string providerName)
        {
            Guid = streamGuid;
            Namespace = streamNamespace;
            this.ProviderName = providerName;
        }

        [Hagar.Id(0)]
        public string ProviderName;

        /// <summary>
        /// Stream primary key guid.
        /// </summary>
        [Hagar.Id(1)]
        public Guid Guid { get; }

        /// <summary>
        /// Stream namespace.
        /// </summary>
        [Hagar.Id(2)]
        public string Namespace { get; }

        public static implicit operator StreamId(FullStreamIdentity identity) => StreamId.Create(identity);
    }
}
