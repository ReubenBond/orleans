namespace Orleans.Durable.Storage.Fuzz.Tests;

/// <summary>
/// The campaign harness's registry of targets (verification.md §6.1). Each layer's tests replay its own targets'
/// seeds and corpora.
/// </summary>
public sealed class FuzzTargetsTests
{
    private static readonly GoldenVectors Vectors = GoldenVectors.Find();

    [Fact]
    public void TargetNamesAreDistinct() =>
        Assert.Equal(FuzzTargets.All.Count, FuzzTargets.All.Select(static target => target.Name).Distinct(StringComparer.Ordinal).Count());

    /// <summary>
    /// Every target has a committed corpus, and every corpus directory belongs to a target, so no corpus goes unreplayed.
    /// </summary>
    [Fact]
    public void EveryCorpusBelongsToATarget() =>
        Assert.Equal(
            FuzzTargets.All.Select(static target => target.Name).Order(StringComparer.Ordinal),
            Directory.EnumerateDirectories(Path.Combine(Vectors.RepositoryRoot, "fuzz", "corpus")).Select(Path.GetFileName).Order(StringComparer.Ordinal));

    /// <summary>
    /// A campaign's longest input covers every seed, so libFuzzer mutates each seed whole.
    /// </summary>
    [Fact]
    public void ACampaignsLongestInputCoversEverySeed() =>
        Assert.All(
            FuzzTargets.All,
            target => Assert.True(target.Seeds(Vectors).All(seed => seed.Length <= FuzzTargets.MaxLength(target, Vectors)), target.Name));
}
