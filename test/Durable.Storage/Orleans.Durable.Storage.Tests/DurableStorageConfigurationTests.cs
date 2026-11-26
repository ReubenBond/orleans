using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Hosting;
using Orleans.Durable.Storage.Testing;
using Orleans.Hosting;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// orleans.md §6: <c>AddDurableJournalStorage(IConfiguration)</c> binds every option of the silo's options root, through the
/// configuration binding generator, from a section whose <c>Node</c> and <c>Control</c> subsections name them; a key which
/// names no option fails the binding, and options which cannot start a node fail validation.
/// </summary>
[NoPageLeakCheck("Binding options rents no pages.")]
public sealed class DurableStorageConfigurationTests
{
    /// <summary>
    /// Every public settable option of the root, its two sections, and their nested sections binds from its key's text:
    /// each takes a value other than its default, so an option the generated binder skipped keeps its default and fails.
    /// </summary>
    [Fact]
    public void EveryOptionBindsFromItsKey()
    {
        var expected = new DurableStorageSiloOptions();
        var values = new Dictionary<string, string?>();
        Assign(expected, "DurableStorage", values);

        var bound = Bind(values);

        AssertBound(expected, bound, "DurableStorage");
        Assert.Equal(expected.Node.MaxSegmentAge, bound.Node.CreateWriterOptions().MaxSegmentAge);
    }

    /// <summary>A node to replace binds from its identity's text form, <c>{node:N}</c>, as a log shows it, in either case.</summary>
    [Fact]
    public void TheNodeToReplaceBindsFromItsIdentitysTextForm()
    {
        var node = Orleans.Durable.Storage.Engine.Ledgers.NodeId.New();

        var bound = Bind(new() { ["DurableStorage:Node:NodeToReplace"] = node.ToString().ToUpperInvariant() });

        Assert.Equal(node.Value, bound.Node.NodeToReplace);
    }

    /// <summary>
    /// A key which names no option fails the binding rather than going unnoticed: a misspelled option, and a writers' option
    /// under the section the writers' options once had.
    /// </summary>
    [Theory]
    [InlineData("DurableStorage:Node:MaxReplicaCall", "MaxReplicaCall")]
    [InlineData("DurableStorage:Node:Writers:MaxSegmentBytes", "Writers")]
    [InlineData("DurableStorage:Control:Replica", "Replica")]
    public void AKeyWhichNamesNoOptionFailsTheBinding(string key, string unknown)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Bind(new() { [key] = "1" }));

        Assert.Contains($"'{unknown}'", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Options which cannot start a node fail validation, naming the option, as the silo resolves them.</summary>
    [Fact]
    public void OptionsWhichCannotStartANodeFailValidation()
    {
        using var services = Register(new()
        {
            ["DurableStorage:Node:Cluster"] = Guid.NewGuid().ToString(),
            ["DurableStorage:Node:Root"] = Path.Combine(Path.GetTempPath(), "bound"),
            ["DurableStorage:Node:ArchiveStore"] = nameof(ArchiveStoreKind.Lsm),
            ["DurableStorage:Node:NodeToReplace"] = Guid.Empty.ToString("N"),
            ["DurableStorage:Node:Ordinal"] = "1",
        });

        var failure = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<DurableStorageSiloOptions>>().Value);

        Assert.Contains("replacement", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// orleans.md §6, archive.md §2.4: a new cluster's silo whose Journals' body limit is out of its range does not start,
    /// its failure naming the option's key, and records nothing in the archive store, whose record would keep the limit for
    /// the cluster's life.
    /// </summary>
    [Fact]
    public async Task ASiloWithABodyLimitOutOfItsRangeDoesNotStartAndRecordsNothing()
    {
        var token = TestContext.Current.CancellationToken;
        var objects = new InMemoryObjectStore();
        var builder = Host.CreateApplicationBuilder();
        _ = builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DurableStorage:Node:Cluster"] = Guid.NewGuid().ToString(),
            ["DurableStorage:Node:Root"] = Path.Combine(Path.GetTempPath(), "unused"),
            ["DurableStorage:Node:ArchiveStore"] = nameof(ArchiveStoreKind.Lsm),
            ["DurableStorage:Node:Journals:MaxBodyBytes"] = "0",
        });
        _ = builder.Services.AddSingleton<IMetaStore>(new InMemoryMetaStore());
        _ = builder.Services.AddSingleton<IObjectStore>(objects);
        _ = builder.UseOrleans(silo => silo.UseLocalhostClustering().AddDurableJournalStorage(builder.Configuration.GetSection("DurableStorage")));
        using var host = builder.Build();

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(token));

        Assert.Contains("Journals:MaxBodyBytes", failure.Message, StringComparison.Ordinal);
        Assert.Empty(await objects.ListAsync("", cancellationToken: token).ToListAsync(token));
    }

    /// <summary>
    /// orleans.md §6, journals.md §8.4: options which cannot hold the largest batch the Journals build fail validation,
    /// naming the option: a ledger's queue and a writer's proposals each hold it.
    /// </summary>
    [Theory]
    [InlineData("Journals:MaxQueuedBytes")]
    [InlineData("MaxProposalBytes")]
    [InlineData("MaxSegmentAge")]
    public void OptionsWhichCannotHoldTheLargestBatchFailValidation(string option)
    {
        var options = new DurableStorageOptions { Cluster = Guid.NewGuid(), Root = Path.Combine(Path.GetTempPath(), "unused"), ArchiveStore = ArchiveStoreKind.Lsm };
        switch (option)
        {
            case "Journals:MaxQueuedBytes":
                options.Journals.MaxQueuedBytes = options.Journals.LargestBatch() - 1;
                break;
            case "MaxProposalBytes":
                options.MaxProposalBytes = options.Journals.LargestBatch() - 1;
                break;
            default:
                options.MaxSegmentAge = Orleans.Durable.Storage.Engine.Ledgers.LedgerWriterOptions.LongestSegmentAge + TimeSpan.FromTicks(1);
                break;
        }

        Assert.Equal(option, Assert.ThrowsAny<ArgumentException>(options.Validate).ParamName);
    }

    /// <summary>A silo's configuration reaches the node's services: the node's and the control plane's sections, as bound.</summary>
    [Fact]
    public void TheBoundSectionsAreTheNodesOptions()
    {
        using var services = Register(new()
        {
            ["DurableStorage:Node:Cluster"] = Guid.NewGuid().ToString(),
            ["DurableStorage:Node:Root"] = Path.Combine(Path.GetTempPath(), "bound"),
            ["DurableStorage:Node:ArchiveStore"] = nameof(ArchiveStoreKind.Lsm),
            ["DurableStorage:Control:Replicas"] = "5",
        });

        var options = services.GetRequiredService<IOptions<DurableStorageSiloOptions>>().Value;

        Assert.Equal(2, new ControlOptions().MaxConcurrentTransitions);
        Assert.Equal(2, new ControlOptions().MaxConcurrentDrains);
        Assert.Equal(32, new ControlOptions().MaxConcurrentHandOffs);
        Assert.Equal(64, new ControlOptions().MaxConcurrentRepairs);
        Assert.Equal(64, new Orleans.Durable.Storage.Engine.Journals.JournalStoreOptions().MaxConcurrentHistoryReads);
        Assert.Same(options.Node, services.GetRequiredService<DurableStorageOptions>());
        Assert.Same(options.Control, services.GetRequiredService<Engine.Cluster.Control.ControlOptions>());
        Assert.Equal(5, options.Control.Replicas);
    }

    /// <summary>
    /// orleans.md §5.10, journals.md §8.3: <see cref="DurableStorageSiloBuilderExtensions.AddDurableJournalIndex"/>
    /// constructs the silo's <see cref="Engine.Journals.IJournalIndex"/> from <see cref="DurableStorageOptions.Root"/>
    /// and injects that instance into <see cref="NodeLedgers"/> through silo DI.
    /// </summary>
    [Fact]
    public void AddDurableJournalIndexWiresNodesRootIntoNodeLedgersThroughSiloDi()
    {
        var expectedRoot = Path.Combine(Path.GetTempPath(), "silo-journal-index-di", Guid.NewGuid().ToString("N"));
        string? observedRoot = null;
        var created = new Engine.Journals.Memory.InMemoryJournalIndex();

        var builder = Host.CreateApplicationBuilder();
        _ = builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DurableStorage:Node:Cluster"] = Guid.NewGuid().ToString(),
            ["DurableStorage:Node:Root"] = expectedRoot,
            ["DurableStorage:Node:ArchiveStore"] = nameof(ArchiveStoreKind.Lsm),
            ["DurableStorage:Node:ArchiveStorePrefix"] = "test",
        });
        _ = builder.Services.AddSingleton<IMetaStore>(new InMemoryMetaStore());
        _ = builder.Services.AddSingleton<IObjectStore>(new InMemoryObjectStore());
        _ = builder.UseOrleans(silo => silo
            .UseLocalhostClustering()
            .AddDurableJournalStorage(builder.Configuration.GetSection("DurableStorage"))
            .AddDurableJournalIndex(root =>
            {
                observedRoot = root;
                return created;
            }));
        using var host = builder.Build();

        var nodeLedgers = host.Services.GetServices<ILifecycleParticipant<Orleans.Runtime.ISiloLifecycle>>()
            .OfType<NodeLedgers>()
            .Single();

        Assert.Equal(expectedRoot, observedRoot);
        Assert.Same(created, host.Services.GetRequiredService<Engine.Journals.IJournalIndex>());
        Assert.Same(created, nodeLedgers.JournalIndex);
    }

    /// <summary>Registers a silo's durable storage node from the configuration's <c>DurableStorage</c> section.</summary>
    private static ServiceProvider Register(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var silo = new SiloBuilder(new ServiceCollection(), configuration);
        _ = silo.AddDurableJournalStorage(configuration.GetSection("DurableStorage"));
        return silo.Services.BuildServiceProvider();
    }

    /// <summary>Applies the registration's configuration of the options to new options, without validating them.</summary>
    private static DurableStorageSiloOptions Bind(Dictionary<string, string?> values)
    {
        using var services = Register(values);
        var options = new DurableStorageSiloOptions();
        foreach (var configure in services.GetServices<IConfigureOptions<DurableStorageSiloOptions>>())
        {
            configure.Configure(options);
        }

        return options;
    }

    /// <summary>
    /// Gives every settable option of <paramref name="target"/> a value other than its default, recursing into its sections,
    /// and records each value's text under its key.
    /// </summary>
    private static void Assign(object target, string path, Dictionary<string, string?> values)
    {
        foreach (var property in Options(target))
        {
            var key = $"{path}:{property.Name}";
            var current = property.GetValue(target);
            if (Section(property.PropertyType))
            {
                if (current is null)
                {
                    current = Activator.CreateInstance(property.PropertyType)!;
                    property.SetValue(target, current);
                }

                Assign(current, key, values);
                continue;
            }

            var (value, text) = Other(property, current);
            property.SetValue(target, value);
            values[key] = text;
        }
    }

    private static void AssertBound(object expected, object actual, string path)
    {
        foreach (var property in Options(expected))
        {
            var key = $"{path}:{property.Name}";
            if (Section(property.PropertyType))
            {
                AssertBound(property.GetValue(expected)!, property.GetValue(actual)!, key);
                continue;
            }

            Assert.True(Equals(property.GetValue(expected), property.GetValue(actual)), $"{key} did not bind.");
        }
    }

    /// <summary>The public settable options of a section.</summary>
    private static IEnumerable<PropertyInfo> Options(object section) =>
        section.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => property.SetMethod is { IsPublic: true });

    private static bool Section(Type type) => type.IsClass && type != typeof(string) && type != typeof(Uri);

    /// <summary>A value of the option other than <paramref name="current"/>, and its text.</summary>
    private static (object Value, string Text) Other(PropertyInfo property, object? current)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type == typeof(bool))
        {
            var value = !(bool)current!;
            return (value, value ? "true" : "false");
        }

        if (type == typeof(TimeSpan))
        {
            var value = ((TimeSpan?)current ?? TimeSpan.Zero) + TimeSpan.FromSeconds(1);
            return (value, value.ToString("c", CultureInfo.InvariantCulture));
        }

        if (type == typeof(Guid))
        {
            var value = Guid.NewGuid();
            return (value, value.ToString("N", CultureInfo.InvariantCulture));
        }

        if (type == typeof(Uri))
        {
            var value = new Uri($"https://account.blob.core.windows.net/{property.Name.ToLowerInvariant()}");
            return (value, value.ToString());
        }

        return type switch
        {
            _ when type == typeof(int) => Text((int?)current + 1 ?? 3),
            _ when type == typeof(long) => Text((long?)current + 1 ?? 3L << 30),
            _ when type == typeof(double) => Text((double?)current + 1 ?? 3d),
            _ when type == typeof(string) => ($"/{property.Name}", $"/{property.Name}"),
            _ when type == typeof(ArchiveStoreKind) => (ArchiveStoreKind.Logs, nameof(ArchiveStoreKind.Logs)),
            _ when type == typeof(LedgerPlacement) => (LedgerPlacement.ObjectStore, nameof(LedgerPlacement.ObjectStore)),
            _ when type == typeof(ObjectLedgerLayout) => (ObjectLedgerLayout.Append, nameof(ObjectLedgerLayout.Append)),
            _ when type == typeof(ObjectLedgerRedundancy) => (ObjectLedgerRedundancy.ZoneRedundant, nameof(ObjectLedgerRedundancy.ZoneRedundant)),
            _ => throw new InvalidOperationException($"No test value for {property.DeclaringType!.Name}.{property.Name} of type {type}: add one."),
        };

        static (object, string) Text<T>(T value)
            where T : IFormattable => (value, value.ToString(null, CultureInfo.InvariantCulture));
    }

    /// <summary>A silo builder over a service collection and a configuration, which registration needs no more of.</summary>
    private sealed class SiloBuilder(IServiceCollection services, IConfiguration configuration) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = services;

        public IConfiguration Configuration { get; } = configuration;
    }
}
