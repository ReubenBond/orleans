#if false
using System;
using System.Threading.Tasks;

namespace Orleans.MetadataStore.Tests
{       
    public interface IMetadataStoreTestGrain : IGrainWithGuidKey
    {
        Task<ReadResult<TValue>> Get<TValue>(string key) where TValue : class, IVersioned;
        Task<UpdateResult<TValue>> TryUpdate<TValue>(string key, TValue updated) where TValue : class, IVersioned;
    }

    public class MetadataStoreTestGrain : Grain, IMetadataStoreTestGrain
    {
        private readonly IMetadataStore store;
        public MetadataStoreTestGrain(IMetadataStore store)
        {
            this.store = store;
        }

        public Task<ReadResult<TValue>> Get<TValue>(string key) where TValue : class, IVersioned => this.store.TryGet<TValue>(key);

        public Task<UpdateResult<TValue>> TryUpdate<TValue>(string key, TValue updated) where TValue : class, IVersioned => this.store.TryUpdate(key, updated);
    }
    
    [Immutable]
    [GenerateSerializer]
    public class MyVersionedData : IVersioned
    {
        [Id(0)]
        public string Value { get; set; }

        [Id(1)]
        public long Version { get; set; }

        public override string ToString()
        {
            return $"{nameof(Version)}: {Version}, {nameof(Value)}: {Value}";
        }
    }
}
#endif