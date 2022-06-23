#nullable enable


namespace Orleans.Runtime.MembershipService.Standalone;

[GenerateSerializer]
public readonly struct AcceptOptions
{
    public AcceptOptions(bool prepareNextAccept)
    {
        PrepareNextAccept = prepareNextAccept;
    }

    [Id(0)]
    public bool PrepareNextAccept { get; }
}
