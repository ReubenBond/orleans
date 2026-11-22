using Microsoft.Extensions.Hosting;

namespace Orleans.Durable.Storage.Architecture.Tests.SimulationFixtures.Hosting;

/// <summary>A host adapter in the fixture layer's <c>.Hosting</c> namespace, which may reference the hosting abstractions.</summary>
public sealed class FixtureBackgroundLoop : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
