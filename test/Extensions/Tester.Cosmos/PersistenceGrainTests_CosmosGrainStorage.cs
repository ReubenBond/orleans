using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using Orleans.Configuration;
using Microsoft.Extensions.Options;
using Orleans.TestingHost;
using TestExtensions;
using Orleans.Hosting;
using System.Threading.Tasks;
using Orleans;
using Orleans.Persistence.Cosmos;
using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Linq;
using Orleans.Runtime;
using System.Threading;
using Orleans.Core;
using System.Collections.Generic;
using UnitTests.Persistence;
using Orleans.Storage;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Tester.Cosmos.Persistence;

/// <summary>
/// PersistenceGrainTests using Cosmos DB - Requires access to Cosmos DB
/// </summary>
[TestCategory("Persistence"), TestCategory("Cosmos")]
public class PersistenceGrainTests_CosmosGrainStorage : OrleansTestingBase, IClassFixture<PersistenceGrainTests_CosmosGrainStorage.Fixture>
{
    private const string GrainNamespace = "Tester.Cosmos.Persistence";
    private readonly ITestOutputHelper _output;
    private readonly BaseTestClusterFixture _fixture;
    private readonly ILogger _logger;
    private readonly TestCluster _testCluster;
    private readonly IGrainFactory _grainFactory;
    private readonly IServiceProvider _services;

    public class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 4;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddCosmosGrainStorage("GrainStorageForTest", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                    {
                        options.ConfigureTestDefaults();
                        options.DeleteStateOnClear = true;
                    }))
                    .AddMemoryGrainStorage("MemoryStore");
            }
        }

        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            if (string.IsNullOrWhiteSpace(TestDefaultConfiguration.CosmosDBAccountEndpoint)
                || string.IsNullOrWhiteSpace(TestDefaultConfiguration.CosmosDBAccountKey))
            {
                throw new SkipException();
            }
        }
    }

    public PersistenceGrainTests_CosmosGrainStorage(ITestOutputHelper output, Fixture fixture)
    {
        fixture.EnsurePreconditionsMet();
        _output = output;
        _fixture = fixture;
        _logger = fixture.Logger;
        _testCluster = fixture.HostedCluster;
        _services = ((InProcessSiloHandle)fixture.HostedCluster.Primary).SiloHost.Services;
        _grainFactory = fixture.GrainFactory;
    }

    private async Task<CosmosGrainStorage> InitializeStorage()
    {
        var options = new CosmosGrainStorageOptions();

        options.ConfigureTestDefaults();

        var pkProvider = new DefaultPartitionKeyProvider();
        var clusterOptions = _services.GetRequiredService<IOptions<ClusterOptions>>();

        IGrainActivationContextAccessor actContextAccessor = new FakeGrainContextAccessor();
        var store = ActivatorUtilities.CreateInstance<CosmosGrainStorage>(_services, options, clusterOptions, "TestStorage", pkProvider, actContextAccessor);
        var lifecycle = ActivatorUtilities.CreateInstance<SiloLifecycleSubject>(_services);
        store.Participate(lifecycle);
        await lifecycle.OnStart(CancellationToken.None);
        return store;
    }

    private sealed class FakeGrainContextAccessor : IGrainActivationContextAccessor, IGrainActivationContext
    {
        public IGrainActivationContext GrainActivationContext => this;

        public Type GrainType => typeof(MyGrain);
        public IGrainIdentity GrainIdentity => throw new NotImplementedException();
        public IServiceProvider ActivationServices => throw new NotImplementedException();
        public Grain GrainInstance => throw new NotImplementedException();
        public IDictionary<object, object> Items => throw new NotImplementedException();
        public IGrainLifecycle ObservableLifecycle => throw new NotImplementedException();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task PersistenceProvider_Azure_Read()
    {
        const string testName = nameof(PersistenceProvider_Azure_Read);

        var store = await InitializeStorage();

        var grainReference = (GrainReference)_fixture.GrainFactory.GetGrain<IMyGrain>(Guid.NewGuid().ToString());
        await Test_PersistenceProvider_Read(testName, store, null, grainReference);
    }

    [SkippableTheory, TestCategory("Functional")]
    [InlineData(null)]
    [InlineData(15 * 64 * 1024 - 256)]
    [InlineData(15 * 32 * 1024 - 256)]
    public async Task PersistenceProvider_Azure_WriteRead(int? stringLength)
    {
        var testName = string.Format("{0}({1} = {2})",
            nameof(PersistenceProvider_Azure_WriteRead),
            nameof(stringLength), stringLength == null ? "default" : stringLength.ToString());

        var grainState = TestStoreGrainState.NewRandomState(stringLength);

        var store = await InitializeStorage();

        var grainReference = (GrainReference)_fixture.GrainFactory.GetGrain<IMyGrain>(Guid.NewGuid().ToString());
        await Test_PersistenceProvider_WriteRead(testName, store, grainState, grainReference);
    }

    [SkippableTheory, TestCategory("Functional")]
    [InlineData(null)]
    [InlineData(15 * 64 * 1024 - 256)]
    [InlineData(15 * 32 * 1024 - 256)]
    public async Task PersistenceProvider_Azure_WriteClearRead(int? stringLength)
    {
        var testName = string.Format("{0}({1} = {2})",
            nameof(PersistenceProvider_Azure_WriteClearRead),
            nameof(stringLength), stringLength == null ? "default" : stringLength.ToString());

        var grainState = TestStoreGrainState.NewRandomState(stringLength);

        var store = await InitializeStorage();

        await Test_PersistenceProvider_WriteClearRead(testName, store, grainState);
    }

    [SkippableTheory, TestCategory("Functional")]
    [InlineData(null)]
    [InlineData(15 * 32 * 1024 - 256)]
    public async Task PersistenceProvider_Azure_ChangeReadFormat(int? stringLength)
    {
        var testName = string.Format("{0}({1} = {2})",
            nameof(PersistenceProvider_Azure_ChangeReadFormat),
            nameof(stringLength), stringLength == null ? "default" : stringLength.ToString());

        var grainState = TestStoreGrainState.NewRandomState(stringLength);
        var grainReference = (GrainReference)_fixture.GrainFactory.GetGrain<IMyGrain>(Guid.NewGuid().ToString());

        var store = await InitializeStorage();

        grainState = await Test_PersistenceProvider_WriteRead(testName, store, grainState, grainReference);

        store = await InitializeStorage();

        await Test_PersistenceProvider_Read(testName, store, grainState, grainReference);
    }

    [SkippableTheory, TestCategory("Functional")]
    [InlineData(null)]
    [InlineData(15 * 32 * 1024 - 256)]
    public async Task PersistenceProvider_Azure_ChangeWriteFormat(int? stringLength)
    {
        var testName = string.Format("{0}({1}={2})",
            nameof(PersistenceProvider_Azure_ChangeWriteFormat),
            nameof(stringLength), stringLength == null ? "default" : stringLength.ToString());

        var grainState = TestStoreGrainState.NewRandomState(stringLength);

        var grainReference = (GrainReference)_fixture.GrainFactory.GetGrain<IMyGrain>(Guid.NewGuid().ToString());

        var store = await InitializeStorage();

        await Test_PersistenceProvider_WriteRead(testName, store, grainState, grainReference);

        grainState = TestStoreGrainState.NewRandomState(stringLength);
        grainState.ETag = "*";

        store = await InitializeStorage();

        await Test_PersistenceProvider_WriteRead(testName, store, grainState, grainReference);
    }

    private async Task Test_PersistenceProvider_Read(string grainTypeName, IGrainStorage store, GrainState<TestStoreGrainState> grainState, GrainReference grainRef)
    {
        grainState ??= new GrainState<TestStoreGrainState>(new TestStoreGrainState());

        var storedGrainState = new GrainState<TestStoreGrainState>(new TestStoreGrainState());

        Stopwatch sw = new Stopwatch();
        sw.Start();

        await store.ReadStateAsync(grainTypeName, grainRef, storedGrainState);

        TimeSpan readTime = sw.Elapsed;
        _output.WriteLine("{0} - Read time = {1}", store.GetType().FullName, readTime);

        var storedState = storedGrainState.State;
        Assert.Equal(grainState.State.A, storedState.A);
        Assert.Equal(grainState.State.B, storedState.B);
        Assert.Equal(grainState.State.C, storedState.C);
    }

    private async Task<GrainState<TestStoreGrainState>> Test_PersistenceProvider_WriteRead(string grainTypeName,
        IGrainStorage store, GrainState<TestStoreGrainState> grainState, GrainReference grainRef)
    {
        grainState ??= TestStoreGrainState.NewRandomState();

        Stopwatch sw = new Stopwatch();
        sw.Start();

        await store.WriteStateAsync(grainTypeName, grainRef, grainState);

        TimeSpan writeTime = sw.Elapsed;
        sw.Restart();

        var storedGrainState = new GrainState<TestStoreGrainState>
        {
            State = new TestStoreGrainState()
        };
        await store.ReadStateAsync(grainTypeName, grainRef, storedGrainState);
        TimeSpan readTime = sw.Elapsed;
        _output.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName, writeTime, readTime);
        Assert.Equal(grainState.State.A, storedGrainState.State.A);
        Assert.Equal(grainState.State.B, storedGrainState.State.B);
        Assert.Equal(grainState.State.C, storedGrainState.State.C);

        return storedGrainState;
    }

    private async Task<GrainState<TestStoreGrainState>> Test_PersistenceProvider_WriteClearRead(string grainTypeName,
        IGrainStorage store, GrainState<TestStoreGrainState> grainState = null, GrainReference grainRef = default)
    {
        grainRef ??= (GrainReference)_grainFactory.GetGrain<IMyGrain>(Guid.NewGuid().ToString());

        if (grainState == null)
        {
            grainState = TestStoreGrainState.NewRandomState();
        }

        Stopwatch sw = new Stopwatch();
        sw.Start();

        await store.WriteStateAsync(grainTypeName, grainRef, grainState);

        TimeSpan writeTime = sw.Elapsed;
        sw.Restart();

        await store.ClearStateAsync(grainTypeName, grainRef, grainState);

        var storedGrainState = new GrainState<TestStoreGrainState>
        {
            State = new TestStoreGrainState()
        };
        await store.ReadStateAsync(grainTypeName, grainRef, storedGrainState);
        TimeSpan readTime = sw.Elapsed;
        _output.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName, writeTime, readTime);
        Assert.NotNull(storedGrainState.State);
        Assert.Equal(default(string), storedGrainState.State.A);
        Assert.Equal(default(int), storedGrainState.State.B);
        Assert.Equal(default(long), storedGrainState.State.C);

        return storedGrainState;
    }

    public class TestStoreGrainStateWithCustomJsonProperties
    {
        [JsonPropertyName("s")]
        public string String { get; set; }

        internal static GrainState<TestStoreGrainStateWithCustomJsonProperties> NewRandomState(int? aPropertyLength = null)
        {
            return new GrainState<TestStoreGrainStateWithCustomJsonProperties>
            {
                State = new TestStoreGrainStateWithCustomJsonProperties
                {
                    String = aPropertyLength == null
                        ? Random.Shared.Next().ToString(CultureInfo.InvariantCulture)
                        : GenerateRandomDigitString(aPropertyLength.Value)
                }
            };
        }

        private static string GenerateRandomDigitString(int stringLength)
        {
            var characters = new char[stringLength];
            for (var i = 0; i < stringLength; ++i)
            {
                characters[i] = (char)Random.Shared.Next('0', '9' + 1);
            }
            return new string(characters);
        }
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_GrainStorage_Delete_Core()
    {
        Guid id = Guid.NewGuid();
        IGrainStorageTestGrain grain = this._grainFactory.GetGrain<IGrainStorageTestGrain>(id, GrainNamespace);

        await grain.DoWrite(1);

        await grain.DoDelete();

        int val = await grain.GetValue(); // Should this throw instead?
        Assert.Equal(0, val);  // "Value after Delete"

        await grain.DoWrite(2);

        val = await grain.GetValue();
        Assert.Equal(2, val);  // "Value after Delete + New Write"
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_GrainStorage_Read_Core()
    {
        Guid id = Guid.NewGuid();
        IGrainStorageTestGrain grain = this._grainFactory.GetGrain<IGrainStorageTestGrain>(id, GrainNamespace);

        int val = await grain.GetValue();

        Assert.Equal(0, val);  // "Initial value"
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_GuidKey_GrainStorage_Read_Write_Core()
    {
        Guid id = Guid.NewGuid();
        IGrainStorageTestGrain grain = this._grainFactory.GetGrain<IGrainStorageTestGrain>(id, GrainNamespace);

        int val = await grain.GetValue();

        Assert.Equal(0, val);  // "Initial value"

        await grain.DoWrite(1);
        val = await grain.GetValue();
        Assert.Equal(1, val);  // "Value after Write-1"

        await grain.DoWrite(2);
        val = await grain.GetValue();
        Assert.Equal(2, val);  // "Value after Write-2"

        val = await grain.DoRead();

        Assert.Equal(2, val);  // "Value after Re-Read"
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_LongKey_GrainStorage_Read_Write_Core()
    {
        long id = random.Next();
        IGrainStorageTestIntegerGrain grain = this._grainFactory.GetGrain<IGrainStorageTestIntegerGrain>(id, GrainNamespace);

        int val = await grain.GetValue();

        Assert.Equal(0, val);  // "Initial value"

        await grain.DoWrite(1);
        val = await grain.GetValue();
        Assert.Equal(1, val);  // "Value after Write-1"

        await grain.DoWrite(2);
        val = await grain.GetValue();
        Assert.Equal(2, val);  // "Value after Write-2"

        val = await grain.DoRead();

        Assert.Equal(2, val);  // "Value after Re-Read"
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_LongKeyExtended_GrainStorage_Read_Write_Core()
    {
        long id = random.Next();
        string extKey = random.Next().ToString(CultureInfo.InvariantCulture);

        IGrainStorageTestIntegerExtendedKeyGrain
            grain = this._grainFactory.GetGrain<IGrainStorageTestIntegerExtendedKeyGrain>(id, extKey, GrainNamespace);

        int val = await grain.GetValue();

        Assert.Equal(0, val);  // "Initial value"

        await grain.DoWrite(1);
        val = await grain.GetValue();
        Assert.Equal(1, val);  // "Value after Write-1"

        await grain.DoWrite(2);
        val = await grain.GetValue();
        Assert.Equal(2, val);  // "Value after Write-2"

        val = await grain.DoRead();
        Assert.Equal(2, val);  // "Value after DoRead"

        val = await grain.GetValue();
        Assert.Equal(2, val);  // "Value after Re-Read"

        string extKeyValue = await grain.GetExtendedKeyValue();
        Assert.Equal(extKey, extKeyValue);  // "Extended Key"
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_GuidKeyExtended_GrainStorage_Read_Write_Core()
    {
        var id = Guid.NewGuid();
        string extKey = random.Next().ToString(CultureInfo.InvariantCulture);

        IGrainStorageTestGuidExtendedKeyGrain
            grain = this._grainFactory.GetGrain<IGrainStorageTestGuidExtendedKeyGrain>(id, extKey, GrainNamespace);

        int val = await grain.GetValue();

        Assert.Equal(0, val);  // "Initial value"

        await grain.DoWrite(1);
        val = await grain.GetValue();
        Assert.Equal(1, val);  // "Value after Write-1"

        await grain.DoWrite(2);
        val = await grain.GetValue();
        Assert.Equal(2, val);  // "Value after Write-2"

        val = await grain.DoRead();
        Assert.Equal(2, val);  // "Value after DoRead"

        val = await grain.GetValue();
        Assert.Equal(2, val);  // "Value after Re-Read"

        string extKeyValue = await grain.GetExtendedKeyValue();
        Assert.Equal(extKey, extKeyValue);  // "Extended Key"
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_String_GrainStorage_Read_Write_Core()
    {
        string extKey = random.Next().ToString(CultureInfo.InvariantCulture);

        var grain = this._grainFactory.GetGrain<IGrainStorageTestStringGrain>(extKey, GrainNamespace);

        int val = await grain.GetValue();

        Assert.Equal(0, val);  // "Initial value"

        await grain.DoWrite(1);
        val = await grain.GetValue();
        Assert.Equal(1, val);  // "Value after Write-1"

        await grain.DoWrite(2);
        val = await grain.GetValue();
        Assert.Equal(2, val);  // "Value after Write-2"

        val = await grain.DoRead();
        Assert.Equal(2, val);  // "Value after DoRead"

        val = await grain.GetValue();
        Assert.Equal(2, val);  // "Value after Re-Read"

        string extKeyValue = await grain.GetExtendedKeyValue();
        Assert.Equal(extKey, extKeyValue);  // "Extended Key"
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_GrainStorage_SiloRestart_Core()
    {
        var initialServiceId = _fixture.GetClientServiceId();

        _output.WriteLine("ClusterId={0} ServiceId={1}", this._testCluster.Options.ClusterId, initialServiceId);

        Guid id = Guid.NewGuid();
        IGrainStorageTestGrain grain = this._grainFactory.GetGrain<IGrainStorageTestGrain>(id, GrainNamespace);

        int val = await grain.GetValue();

        Assert.Equal(0, val);  // "Initial value"

        await grain.DoWrite(1);

        var serviceId = await this._grainFactory.GetGrain<UnitTests.GrainInterfaces.IServiceIdGrain>(Guid.Empty).GetServiceId();
        Assert.Equal(initialServiceId, serviceId);  // "ServiceId same before restart."

        _output.WriteLine("About to reset Silos");
        foreach (var silo in this._testCluster.GetActiveSilos().ToList())
        {
            await this._testCluster.RestartSiloAsync(silo);
        }
        this._testCluster.InitializeClient();

        _output.WriteLine("Silos restarted");

        serviceId = await this._grainFactory.GetGrain<UnitTests.GrainInterfaces.IServiceIdGrain>(Guid.Empty).GetServiceId();
        _output.WriteLine("ClusterId={0} ServiceId={1}", this._testCluster.Options.ClusterId, serviceId);
        Assert.Equal(initialServiceId, serviceId);  // "ServiceId same after restart."

        val = await grain.GetValue();
        Assert.Equal(1, val);  // "Value after Write-1"

        await grain.DoWrite(2);
        val = await grain.GetValue();
        Assert.Equal(2, val);  // "Value after Write-2"

        val = await grain.DoRead();

        Assert.Equal(2, val);  // "Value after Re-Read"
    }
}

[Serializable]
public class PersistenceTestGrainState
{
    public int Field1 { get; set; }
    public string Field2 { get; set; }
}

[GrainType("guid-grain")]
[Orleans.Providers.StorageProvider(ProviderName = "GrainStorageForTest")]
public class GrainStorageTestGrain : Grain<PersistenceTestGrainState>, IGrainStorageTestGrain
{
    public override Task OnActivateAsync()
    {
        return Task.CompletedTask;
    }

    public Task<int> GetValue()
    {
        return Task.FromResult(State.Field1);
    }

    public Task DoWrite(int val)
    {
        State.Field1 = val;
        return WriteStateAsync();
    }

    public async Task<int> DoRead()
    {
        await ReadStateAsync(); // Re-read state from store
        return State.Field1;
    }

    public Task DoDelete()
    {
        return ClearStateAsync(); // Automatically marks this grain as DeactivateOnIdle 
    }
}

[GrainType("long-grain")]
[Orleans.Providers.StorageProvider(ProviderName = "GrainStorageForTest")]
public class GrainStorageTestIntegerGrain : Grain<PersistenceTestGrainState>, IGrainStorageTestIntegerGrain
{
    public override Task OnActivateAsync()
    {
        return Task.CompletedTask;
    }

    public Task<int> GetValue()
    {
        return Task.FromResult(State.Field1);
    }

    public Task DoWrite(int val)
    {
        State.Field1 = val;
        return WriteStateAsync();
    }

    public async Task<int> DoRead()
    {
        await ReadStateAsync(); // Re-read state from store
        return State.Field1;
    }

    public Task DoDelete()
    {
        return ClearStateAsync(); // Automatically marks this grain as DeactivateOnIdle 
    }
}

[GrainType("guidext-grain")]
[Orleans.Providers.StorageProvider(ProviderName = "GrainStorageForTest")]
public class GrainStorageTestGuidExtendedKeyGrain : Grain<PersistenceTestGrainState>, IGrainStorageTestGuidExtendedKeyGrain
{
    public Task<string> GetExtendedKeyValue()
    {
        this.GetPrimaryKey(out var result);
        return Task.FromResult(result);
    }
    public override Task OnActivateAsync()
    {
        return Task.CompletedTask;
    }

    public Task<int> GetValue()
    {
        return Task.FromResult(State.Field1);
    }

    public Task DoWrite(int val)
    {
        State.Field1 = val;
        return WriteStateAsync();
    }

    public async Task<int> DoRead()
    {
        await ReadStateAsync(); // Re-read state from store
        return State.Field1;
    }

    public Task DoDelete()
    {
        return ClearStateAsync(); // Automatically marks this grain as DeactivateOnIdle 
    }
}

[GrainType("longext-grain")]
[Orleans.Providers.StorageProvider(ProviderName = "GrainStorageForTest")]
public class GrainStorageTestIntegerExtendedKeyGrain : Grain<PersistenceTestGrainState>, IGrainStorageTestIntegerExtendedKeyGrain
{
    public Task<string> GetExtendedKeyValue()
    {
        this.GetPrimaryKey(out var result);
        return Task.FromResult(result);
    }

    public override Task OnActivateAsync()
    {
        return Task.CompletedTask;
    }

    public Task<int> GetValue()
    {
        return Task.FromResult(State.Field1);
    }

    public Task DoWrite(int val)
    {
        State.Field1 = val;
        return WriteStateAsync();
    }

    public async Task<int> DoRead()
    {
        await ReadStateAsync(); // Re-read state from store
        return State.Field1;
    }

    public Task DoDelete()
    {
        return ClearStateAsync(); // Automatically marks this grain as DeactivateOnIdle 
    }
}

[GrainType("string-grain")]
[Orleans.Providers.StorageProvider(ProviderName = "GrainStorageForTest")]
public class GrainStorageTestStringExtendedKeyGrain : Grain<PersistenceTestGrainState>, IGrainStorageTestStringGrain
{
    public Task<string> GetExtendedKeyValue()
    {
        this.GetPrimaryKey(out var result);
        return Task.FromResult(result);
    }

    public override Task OnActivateAsync()
    {
        return Task.CompletedTask;
    }

    public Task<int> GetValue()
    {
        return Task.FromResult(State.Field1);
    }

    public Task DoWrite(int val)
    {
        State.Field1 = val;
        return WriteStateAsync();
    }

    public async Task<int> DoRead()
    {
        await ReadStateAsync(); // Re-read state from store
        return State.Field1;
    }

    public Task DoDelete()
    {
        return ClearStateAsync(); // Automatically marks this grain as DeactivateOnIdle 
    }
}

public interface IGrainStorageTestGrain : IGrainWithGuidKey
{
    Task<int> GetValue();
    Task DoWrite(int val);
    Task<int> DoRead();
    Task DoDelete();
}

public interface IGrainStorageTestGuidExtendedKeyGrain : IGrainWithGuidCompoundKey
{
    Task<string> GetExtendedKeyValue();
    Task<int> GetValue();
    Task DoWrite(int val);
    Task<int> DoRead();
    Task DoDelete();
}

public interface IGrainStorageTestIntegerGrain : IGrainWithIntegerKey
{
    Task<int> GetValue();
    Task DoWrite(int val);
    Task<int> DoRead();
    Task DoDelete();
}

public interface IGrainStorageTestIntegerExtendedKeyGrain : IGrainWithIntegerCompoundKey
{
    Task<string> GetExtendedKeyValue();
    Task<int> GetValue();
    Task DoWrite(int val);
    Task<int> DoRead();
    Task DoDelete();
}

public interface IGrainStorageTestStringGrain : IGrainWithStringKey
{
    Task<string> GetExtendedKeyValue();
    Task<int> GetValue();
    Task DoWrite(int val);
    Task<int> DoRead();
    Task DoDelete();
}

public interface IMyGrain : IGrainWithStringKey
{
    Task<bool> Foo();
}

[GrainType("my-grain")]
public class MyGrain : Grain, IMyGrain
{
    public Task<bool> Foo() => Task.FromResult(true);
}

