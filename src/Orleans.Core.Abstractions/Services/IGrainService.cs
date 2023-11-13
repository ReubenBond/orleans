namespace Orleans.Services
{
    /// <summary>
    /// Base interface for grain services.
    /// </summary>
    [Alias("Orleans.Services.IGrainService")]
    public interface IGrainService : ISystemTarget
    {
    }
}