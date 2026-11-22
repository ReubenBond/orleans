using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Orleans.Durable.Storage.Architecture.Tests.SimulationFixtures;

// A fixture layer for SimulationClosureTests: what a rewritten assembly may reference from the Microsoft.Extensions
// abstractions, and each reference which starts work outside a simulation's scheduler.

/// <summary>A component which logs and reads its options once, at construction: the rewritten closure may do both.</summary>
public sealed class FixtureComponent(ILogger<FixtureComponent> logger, IOptions<FixtureOptions> options)
{
    public ILogger Logger { get; } = logger;

    public IOptions<FixtureOptions> Options { get; } = options;
}

/// <summary>Reloadable options, whose change notifications are signalled outside the scheduler.</summary>
public sealed class FixtureOptionsReader
{
    public IOptionsMonitor<FixtureOptions>? Monitor { get; set; }

    public IOptionsSnapshot<FixtureOptions>? Snapshot { get; set; }

    public OptionsMonitor<FixtureOptions>? Concrete { get; set; }
}

/// <summary>Change tokens, whose callbacks run on the thread which signals them.</summary>
public static class FixtureChangeTokens
{
    public static IDisposable Watch(Func<IChangeToken?> changes, Action consumer) => ChangeToken.OnChange(changes, consumer);

    public static CancellationChangeToken Cancelled(CancellationToken token) => new(token);

    public static CompositeChangeToken Either(IReadOnlyList<IChangeToken> tokens) => new(tokens);
}

/// <summary>A hosted service outside a <c>.Hosting</c> namespace.</summary>
public sealed class FixtureHostedService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A reference into a denied abstractions assembly, whose implementations read the clock unrewritten.</summary>
public sealed class FixtureCacheReader
{
    public IDistributedCache? Cache { get; set; }
}

/// <summary>The options <see cref="FixtureComponent"/> reads.</summary>
public sealed class FixtureOptions
{
    public int Interval { get; set; }
}
