namespace Orleans.Runtime
{
    /// <summary>
    /// Marker interface for grain extensions, used by internal runtime extension endpoints.
    /// </summary>
    [GenerateMethodSerializers(typeof(GrainReference), isExtension: true)]
    [Alias("Orleans.Runtime.IGrainExtension")]
    public interface IGrainExtension : IAddressable
    {
    }
}
