// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Tester.StorageFacet.Abstractions;

namespace Tester
{
    public interface IStorageFacetGrain : IGrainWithIntegerKey
    {
        Task<string[]> GetNames();
        Task<string[]> GetExtendedInfo();
    }

    public class StorageFacetGrain : Grain, IStorageFacetGrain
    {
        private readonly IExampleStorage<string> first;
        private readonly IExampleStorage<string> second;

        public StorageFacetGrain(
            [ExampleStorage("Blob", "FirstState")] IExampleStorage<string> first,
            [ExampleStorage("Table")] IExampleStorage<string> second)
        {
            this.first = first;
            this.second = second;
        }

        public Task<string[]> GetNames()
        {
            return Task.FromResult(new[] { this.first.Name, this.second.Name });
        }

        public Task<string[]> GetExtendedInfo()
        {
            return Task.FromResult(new[] { this.first.GetExtendedInfo(), this.second.GetExtendedInfo() });
        }
    }

    public interface IStorageFactoryGrain : IStorageFacetGrain
    {
    }
    public class StorageFactoryGrain : Grain, IStorageFactoryGrain
    {
        private readonly IExampleStorage<string> first;
        private readonly IExampleStorage<string> second;

        public StorageFactoryGrain(
            INamedExampleStorageFactory namedExampleStorageFactory)
        {
            this.first = namedExampleStorageFactory.Create<string>("Blob", new ExampleStorageConfig("FirstState"));
            this.second = namedExampleStorageFactory.Create<string>("Table", new ExampleStorageConfig("second"));
        }

        public Task<string[]> GetNames()
        {
            return Task.FromResult(new[] { this.first.Name, this.second.Name });
        }

        public Task<string[]> GetExtendedInfo()
        {
            return Task.FromResult(new[] { this.first.GetExtendedInfo(), this.second.GetExtendedInfo() });
        }
    }

    public interface IStorageDefaultFactoryGrain : IStorageFacetGrain
    {
    }

    public class StorageDefaultFactoryGrain : Grain, IStorageDefaultFactoryGrain
    {
        private readonly IExampleStorage<string> first;
        private readonly IExampleStorage<string> second;

        public StorageDefaultFactoryGrain(
            IExampleStorageFactory ExampleStorageFactory)
        {
            this.first = ExampleStorageFactory.Create<string>(new ExampleStorageConfig("FirstState"));
            this.second = ExampleStorageFactory.Create<string>(new ExampleStorageConfig("second"));
        }

        public Task<string[]> GetNames()
        {
            return Task.FromResult(new[] { this.first.Name, this.second.Name });
        }

        public Task<string[]> GetExtendedInfo()
        {
            return Task.FromResult(new[] { this.first.GetExtendedInfo(), this.second.GetExtendedInfo() });
        }
    }

    public interface IStorageDefaultFacetGrain : IStorageFacetGrain
    {
    }

    public class StorageDefaultFacetGrain : Grain, IStorageDefaultFacetGrain
    {
        private readonly IExampleStorage<string> first;
        private readonly IExampleStorage<string> second;

        public StorageDefaultFacetGrain(
            [ExampleStorage(stateName: "FirstState")] IExampleStorage<string> first,
            [ExampleStorage] IExampleStorage<string> second)
        {
            this.first = first;
            this.second = second;
        }

        public Task<string[]> GetNames()
        {
            return Task.FromResult(new[] { this.first.Name, this.second.Name });
        }

        public Task<string[]> GetExtendedInfo()
        {
            return Task.FromResult(new[] { this.first.GetExtendedInfo(), this.second.GetExtendedInfo() });
        }
    }
}
