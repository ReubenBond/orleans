namespace Orleans.Runtime
{
    /// <summary>
    /// Marker interface for grain extensions, used by internal runtime extension endpoints
    /// </summary>
    [Hagar.GenerateMethodSerializers(typeof(NewGrainReference), isExtension: true)]
    public interface IGrainExtension : IAddressable
    {
    }
}
