using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// <see cref="ArchiveOptions"/>' defaults (archive §8.7, §8.8) and its validation, at each bound and past it.
/// </summary>
[NoPageLeakCheck("Options are values; the tests rent no pages.")]
public sealed class ArchiveOptionsTests
{

    [Fact]
    public void TheDefaultsAreTheSpecifiedOnesAndValid()
    {
        var options = new ArchiveOptions();

        options.Validate();
        Assert.Equal(
            (256 * 1024, 14, 4, 16, 64L * 1024 * 1024, 2L * 1024 * 1024 * 1024, 256L * 1024 * 1024, TimeSpan.FromSeconds(30)),
            (options.SectionBlockBytes, options.BloomBitsPerKey, options.GridBits, options.MaxSectionsPerPartition, options.SectionBytes, options.CompactionBudgetBytes, options.TailCacheBytes, options.ViewAdoptionInterval));
        Assert.Equal(
            (null, 16L * 1024 * 1024 * 1024, 64L * 1024 * 1024),
            (options.BlockCachePath, options.BlockCacheBytes, options.BlockCacheRegionBytes));
        Assert.Equal(new RunSectionSettings(256 * 1024, 14), options.SectionSettings);
    }

    /// <summary>Each bound is inclusive: the options at it are valid.</summary>
    [Fact]
    public void OptionsAtEachBoundAreValid()
    {
        ArchiveOptions[] valid =
        [
            new() { SectionBlockBytes = 1 },
            new() { BloomBitsPerKey = 1 },
            new() { BloomBitsPerKey = 64 },
            new() { GridBits = 1, MaxSectionsPerPartition = 2 },
            new() { GridBits = ArchiveOptions.MaxGridBits, MaxSectionsPerPartition = 1 << ArchiveOptions.MaxGridBits, SectionBytes = 1 },
            new() { SectionBytes = 1, CompactionBudgetBytes = 1 },
            new() { MaxSectionsPerPartition = 31, SectionBytes = int.MaxValue / 32 },
            new() { TailCacheBytes = 0, ViewAdoptionInterval = TimeSpan.Zero },
            new() { BlockCacheBytes = 0 },
            new() { BlockCachePath = Path.Combine(Path.GetTempPath(), "blocks"), BlockCacheRegionBytes = ArchiveOptions.MinBlockCacheRegionBytes, BlockCacheBytes = 2 * ArchiveOptions.MinBlockCacheRegionBytes },
            new() { BlockCacheRegionBytes = ArchiveOptions.MaxBlockCacheRegionBytes, BlockCacheBytes = 2 * ArchiveOptions.MaxBlockCacheRegionBytes },
        ];

        Assert.All(valid, static options => options.Validate());
    }

    /// <summary>
    /// Validation refuses each option past its bound, naming it: <c>(k + 1) × s</c> past the object bound among them.
    /// </summary>
    [Fact]
    public void ValidationRefusesEachOptionPastItsBound()
    {
        (ArchiveOptions Options, string Name)[] invalid =
        [
            (new() { SectionBlockBytes = 0 }, nameof(ArchiveOptions.SectionBlockBytes)),
            (new() { BloomBitsPerKey = 0 }, nameof(ArchiveOptions.BloomBitsPerKey)),
            (new() { BloomBitsPerKey = 65 }, nameof(ArchiveOptions.BloomBitsPerKey)),
            (new() { GridBits = 0 }, nameof(ArchiveOptions.GridBits)),
            (new() { GridBits = ArchiveOptions.MaxGridBits + 1 }, nameof(ArchiveOptions.GridBits)),
            (new() { MaxSectionsPerPartition = 15 }, nameof(ArchiveOptions.MaxSectionsPerPartition)),
            (new() { GridBits = 1, MaxSectionsPerPartition = 1 }, nameof(ArchiveOptions.MaxSectionsPerPartition)),

            (new() { SectionBytes = 0 }, nameof(ArchiveOptions.SectionBytes)),
            (new() { MaxSectionsPerPartition = 31, SectionBytes = int.MaxValue / 32 + 1 }, nameof(ArchiveOptions.SectionBytes)),
            (new() { CompactionBudgetBytes = 0 }, nameof(ArchiveOptions.CompactionBudgetBytes)),
            (new() { TailCacheBytes = -1 }, nameof(ArchiveOptions.TailCacheBytes)),
            (new() { ViewAdoptionInterval = TimeSpan.FromTicks(-1) }, nameof(ArchiveOptions.ViewAdoptionInterval)),
            (new() { BlockCacheRegionBytes = ArchiveOptions.MinBlockCacheRegionBytes - 1 }, nameof(ArchiveOptions.BlockCacheRegionBytes)),
            (new() { BlockCacheRegionBytes = ArchiveOptions.MaxBlockCacheRegionBytes + 1 }, nameof(ArchiveOptions.BlockCacheRegionBytes)),
            (new() { BlockCacheBytes = -1 }, nameof(ArchiveOptions.BlockCacheBytes)),
            (new() { BlockCacheRegionBytes = 64 * 1024, BlockCacheBytes = 128 * 1024 - 1 }, nameof(ArchiveOptions.BlockCacheBytes)),
        ];

        Assert.All(invalid, static invalid => Assert.Equal(invalid.Name, Assert.Throws<ArgumentOutOfRangeException>(invalid.Options.Validate).ParamName));
        Assert.Equal(nameof(ArchiveOptions.BlockCachePath), Assert.Throws<ArgumentException>(new ArchiveOptions { BlockCachePath = "" }.Validate).ParamName);
        Assert.Equal(nameof(ArchiveOptions.BlockCachePath), Assert.Throws<ArgumentException>(new ArchiveOptions { BlockCachePath = "   " }.Validate).ParamName);
        Assert.Equal(nameof(ArchiveOptions.BlockCachePath), Assert.Throws<ArgumentException>(new ArchiveOptions { BlockCachePath = "relative-blocks" }.Validate).ParamName);
    }
}
