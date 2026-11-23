using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Journaling;
using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using Orleans.Hosting;
using Orleans.Providers;

namespace Orleans.Durable.Storage.Interop.Tests;

/// <summary>
/// A real journal's replay (archive-logs.md §10; precondition 8): Orleans' own <c>JournaledStateManager</c> writes a
/// <c>DurableDictionary</c> and a <c>DurableValue</c> in each of Orleans' journal formats, which a capturing storage
/// records as one snapshot, its <c>Replace</c>, and the segments appended after it; Clog writes them through
/// <see cref="ArchiveLogWriter"/> as an opener of the snapshot and the first append, and a later
/// append run; and a plain Orleans manager with read-only access recovers the states over <c>AzureBlobJournalStorage</c>,
/// equal to the writer's.
/// </summary>
public sealed class OrleansStateReplayTests(CloudEmulators emulators) : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>Gets every backend with each of Orleans' journal formats: JSON lines, its default, and Orleans' binary.</summary>
    public static TheoryData<InteropBackend, string> BackendsAndFormats =>
        [.. Enum.GetValues<InteropBackend>().SelectMany(static backend => ((string[])["json", "orleans-binary"]).Select(format => (backend, format)))];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Theory]
    [MemberData(nameof(BackendsAndFormats))]
    public async Task AJournaledStateManagersJournalRecoversThroughPlainOrleans(InteropBackend backend, string format)
    {
        await using var services = await InteropServices.CreateAsync(backend, emulators);
        var captured = new CapturingStorage();
        Dictionary<string, int> scores;
        string? title;
        await using (var writer = Writer(captured, format))
        {
            var (manager, states) = await ManagerAsync(writer, "replay/scores");
            await using (manager)
            {
                var (dictionary, value) = states;
                dictionary["a"] = 1;
                dictionary["b"] = 2;
                value.Value = "first";
                await manager.WriteStateAsync(Token);
                dictionary["c"] = 3;
                captured.CompactionRequested = true;
                await manager.WriteStateAsync(Token);
                captured.CompactionRequested = false;
                dictionary["a"] = 10;
                _ = dictionary.Remove("b");
                await manager.WriteStateAsync(Token);
                value.Value = "second";
                dictionary["d"] = 4;
                await manager.WriteStateAsync(Token);
                value.Value = "third";
                await manager.WriteStateAsync(Token);
                scores = dictionary.ToDictionary(StringComparer.Ordinal);
                title = value.Value;
            }
        }

        Assert.NotNull(captured.Snapshot);
        Assert.Equal(3, captured.Appended.Count);

        await using var journals = new ArchiveLogJournals(services, AzureArchiveLogStore.AzureLayout, format);
        var journal = new Journal("replay/scores");
        await journals.WriteAsync(journal, new Command(RowKind.Replace, captured.Snapshot), new Command(RowKind.Append, captured.Appended[0]));
        await journals.WriteAsync(journal, [.. captured.Appended.Skip(1).Select(static segment => new Command(RowKind.Append, segment))]);
        await journals.AssertReadsAsync(journal);

        var (recovering, recovered) = await ManagerAsync(journals.Orleans, journal.Id);
        await using (recovering)
        {
            var (dictionary, value) = recovered;
            Assert.Equal(scores.OrderBy(static pair => pair.Key, StringComparer.Ordinal), dictionary.OrderBy(static pair => pair.Key, StringComparer.Ordinal));
            Assert.Equal(title, value.Value);
        }

        services.AssertNoViolations();
    }

    /// <summary>Returns plain Orleans' services over a provider which captures its journal's segments.</summary>
    private static PlainOrleans Writer(CapturingStorage captured, string format) => PlainOrleans.With(format, silo => silo.AddJournalStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, _ => new CapturingProvider(captured)));

    /// <summary>
    /// Creates a standalone manager of <paramref name="journal"/> in <paramref name="orleans"/>, registers a durable
    /// dictionary and a durable value with it through Orleans' own registrations, and initializes it, which replays the
    /// journal.
    /// </summary>
    private static async Task<(IJournaledStateManager Manager, (IDurableDictionary<string, int> Dictionary, IDurableValue<string> Value) States)> ManagerAsync(PlainOrleans orleans, string journal)
    {
        var manager = new ScopedManager(
            orleans.Services.GetRequiredService<IJournaledStateManagerFactory>().CreateStandalone(new JournalId(journal)),
            orleans.Services);
        var scope = manager.Scope.ServiceProvider;
        scope.GetRequiredService<ManagerHolder>().Manager = manager.Inner;
        var dictionary = scope.GetRequiredKeyedService<IDurableDictionary<string, int>>("scores");
        var value = scope.GetRequiredKeyedService<IDurableValue<string>>("title");
        await manager.InitializeAsync(Token);
        return (manager, (dictionary, value));
    }

    /// <summary>A manager which disposes the scope its states were resolved in with itself.</summary>
    private sealed class ScopedManager(IJournaledStateManager manager, IServiceProvider services) : IJournaledStateManager
    {
        private readonly IServiceScope _scope = services.CreateScope();

        public IJournaledStateManager Inner => manager;

        public IServiceScope Scope => _scope;

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => manager.InitializeAsync(cancellationToken);

        public void RegisterStateMachine(string name, IStateMachine stateMachine) => manager.RegisterStateMachine(name, stateMachine);

        public bool TryGetStateMachine(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IStateMachine? stateMachine) => manager.TryGetStateMachine(name, out stateMachine);

        public ValueTask WriteStateAsync(CancellationToken cancellationToken = default) => manager.WriteStateAsync(cancellationToken);

        public ValueTask DeleteStateAsync(CancellationToken cancellationToken = default) => manager.DeleteStateAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await manager.DisposeAsync();
            _scope.Dispose();
        }
    }

    /// <summary>A storage provider of one capturing storage.</summary>
    private sealed class CapturingProvider(CapturingStorage storage) : IJournalStorageProvider
    {
        public IJournalStorage CreateStorage(JournalId journalId) => storage;
    }

    /// <summary>
    /// A journal storage which starts empty and records the manager's last snapshot, its <c>Replace</c>, and the segments
    /// appended after it, and asks for a compaction when the test says.
    /// </summary>
    private sealed class CapturingStorage : IJournalStorage
    {
        public bool CompactionRequested { get; set; }

        public byte[]? Snapshot { get; private set; }

        public List<byte[]> Appended { get; } = [];

        public bool IsCompactionRequested => CompactionRequested;

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            consumer.Complete(metadata: null);
            return ValueTask.CompletedTask;
        }

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            Appended.Add(value.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            Snapshot = value.ToArray();
            Appended.Clear();
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
