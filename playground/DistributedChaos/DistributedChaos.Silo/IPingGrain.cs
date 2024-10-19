internal interface IPingGrain : IGrainWithStringKey
{
    ValueTask Ping();
}
