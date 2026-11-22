using System.Globalization;
using Orleans.Durable.Storage.Fuzz;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Replays fuzz targets' seeds and committed corpora through their oracles on every test run (verification.md §6.1),
/// so an input a campaign found stays a regression case without libFuzzer. Each layer's tests replay that layer's
/// targets.
/// </summary>
internal static class CorpusReplay
{
    /// <summary>
    /// The time one input may take before its replay fails as a hang: a campaign's <c>-timeout</c>.
    /// </summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private static readonly GoldenVectors Vectors = GoldenVectors.Find();

    public static TheoryData<string> Names(IEnumerable<FuzzTarget> targets) => [.. targets.Select(static target => target.Name)];

    /// <summary>
    /// Checks every seed the golden vectors give a target. Between them the seeds reach both a rejection and an
    /// acceptance, so every branch of the oracle runs.
    /// </summary>
    /// <param name="targets">The layer's targets.</param>
    /// <param name="name">The target to replay.</param>
    /// <param name="eachInput">Runs each input's check, for a layer whose tests check each input for leaks.</param>
    public static async Task ReplaySeedsAsync(IEnumerable<FuzzTarget> targets, string name, Func<Func<Task>, Task>? eachInput = null)
    {
        var target = targets.Single(target => target.Name == name);
        var seeds = target.Seeds(Vectors).ToArray();
        Assert.NotEmpty(seeds);
        var outcomes = FuzzOutcome.None;
        for (var index = 0; index < seeds.Length; index++)
        {
            outcomes |= await CheckAsync(target, seeds[index], string.Create(CultureInfo.InvariantCulture, $"seed {index}"), eachInput);
        }

        Assert.Equal(FuzzOutcome.Accepted | FuzzOutcome.Rejected, outcomes);
    }

    /// <summary>
    /// Checks every input of a target's committed corpus, <c>test/Durable.Storage/fuzz/corpus/{target}</c> and its <c>regressions</c>. The
    /// seeds stay in the golden vectors, so no committed input is a copy of one.
    /// </summary>
    /// <param name="targets">The layer's targets.</param>
    /// <param name="name">The target to replay.</param>
    /// <param name="eachInput">Runs each input's check, for a layer whose tests check each input for leaks.</param>
    public static async Task ReplayCorpusAsync(IEnumerable<FuzzTarget> targets, string name, Func<Func<Task>, Task>? eachInput = null)
    {
        var target = targets.Single(target => target.Name == name);
        var directory = Path.Combine(Vectors.StorageRoot, "fuzz", "corpus", name);
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);
        var seeds = target.Seeds(Vectors).Select(Convert.ToHexString).ToHashSet(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var input = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
            var source = Path.GetRelativePath(directory, file);
            Assert.False(seeds.Contains(Convert.ToHexString(input)), $"{name}/{source} is a copy of a seed.");
            _ = await CheckAsync(target, input, source, eachInput);
        }
    }

    /// <summary>
    /// Checks one input through <paramref name="eachInput"/>, naming the input when the wrapper fails after the oracle
    /// is satisfied, as a leak check does.
    /// </summary>
    private static async Task<FuzzOutcome> CheckAsync(FuzzTarget target, byte[] input, string source, Func<Func<Task>, Task>? eachInput)
    {
        if (eachInput is null)
        {
            return await CheckAsync(target, input, source);
        }

        FuzzOutcome? outcome = null;
        try
        {
            await eachInput(async () => outcome = await CheckAsync(target, input, source));
        }
        catch (Exception exception) when (outcome is not null)
        {
            Assert.Fail($"{target.Name} satisfied its oracle on {source} but failed its check: {exception.Message}");
        }

        return outcome ?? FuzzOutcome.None;
    }

    private static async Task<FuzzOutcome> CheckAsync(FuzzTarget target, byte[] input, string source)
    {
        var check = Task.Run(() => target.Check(input), TestContext.Current.CancellationToken);
        try
        {
            return await check.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            Assert.Fail(string.Create(CultureInfo.InvariantCulture, $"{target.Name} did not finish {source} within {Deadline.TotalSeconds} s."));
            throw;
        }
        catch (FuzzFindingException finding)
        {
            Assert.Fail($"{target.Name} broke its oracle on {source} ({Convert.ToHexStringLower(input.AsSpan(0, Math.Min(input.Length, 64)))}...): {finding}");
            throw;
        }
    }
}
