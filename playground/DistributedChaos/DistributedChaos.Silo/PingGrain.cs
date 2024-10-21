
internal sealed class PingGrain : Grain, IPingGrain
{
    public ValueTask Ping() => ValueTask.CompletedTask;
}
