using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Resolves an <see cref="IGrainLocator"/> for the specified grain type.
    /// </summary>
    public interface IGrainLocatorResolver
    {
        /// <summary>
        /// Resolves a grain locator for the specified grain type.
        /// </summary>
        /// <param name="grainType">The grain type to resolve a grain locator for.</param>
        /// <param name="result">The resolved grain locator.</param>
        /// <returns><see langword="true"/> if the an appropriate grain locator was resolved, otherwise <see langword="false"/>.</returns>
        bool TryResolveGrainLocator(GrainType grainType, out IGrainLocator result);
    }
}
