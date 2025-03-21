using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Journaling.Tests;

public class TestDurableGrain : DurableGrain, ITestDurableGrainInterface
{
    private readonly DurableValue<string> _name;
    private readonly DurableValue<int> _counter;
    
    public TestDurableGrain(ILogger<TestDurableGrain> logger) 
        : base(logger)
    {
        _name = CreateDurableValue<string>("name");
        _counter = CreateDurableValue<int>("counter");
    }
    
    public Task SetValues(string name, int counter)
    {
        _name.Value = name;
        _counter.Value = counter;
        return WriteStateAsync();
    }
    
    public Task<(string Name, int Counter)> GetValues()
    {
        return Task.FromResult((_name.Value, _counter.Value));
    }
}