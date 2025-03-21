using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling.Tests;

public class TestGrain : DurableGrain, ITestDurableGrainInterface
{
    private readonly IDurableValue<string> _name;
    private readonly IDurableValue<int> _counter;

    public TestGrain(
        [FromKeyedServices("name")] IDurableValue<string> name,
        [FromKeyedServices("counter")] IDurableValue<int> counter)
    {
        _name = name;
        _counter = counter;
    }

    public Task SetValues(string name, int counter)
    {
        _name.Value = name;
        _counter.Value = counter;
        return WriteStateAsync().AsTask();
    }

    public Task<(string Name, int Counter)> GetValues()
    {
        return Task.FromResult((_name.Value!, _counter.Value!));
    }
}