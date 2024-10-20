internal sealed class PingGrain : IPingGrain
{
    public ValueTask Ping() => ValueTask.CompletedTask;
}
