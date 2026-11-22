using System.Globalization;
using System.Text.RegularExpressions;

namespace Orleans.Durable.Storage.Testing.Tests;

/// <summary>
/// Checks Porcupine's published key-value histories (test/Durable.Storage/shared-vectors/linearizability) and reaches Porcupine's
/// verdicts, partitioned by key and, where Porcupine checks it quickly, as one unpartitioned map.
/// </summary>
public sealed partial class PorcupineVectorTests
{
    private const string Area = "linearizability";

    public static TheoryData<string> Histories() => [.. SharedVectors.Cases(Area, "histories.json", "histories").Select(SharedVectors.Name)];

    /// <summary>
    /// The histories Porcupine also checks unpartitioned; the others take minutes as one map.
    /// </summary>
    public static TheoryData<string> UnpartitionedHistories() =>
    [
        .. SharedVectors.Cases(Area, "histories.json", "histories")
            .Where(static vector => vector.GetProperty("unpartitioned").GetBoolean())
            .Select(SharedVectors.Name),
    ];

    [Theory]
    [MemberData(nameof(Histories))]
    public void PartitionedCheckReachesPorcupinesVerdict(string name)
    {
        var (file, linearizable) = Vector(name);
        var history = Parse(File.ReadAllLines(SharedVectors.FilePath(Area, file)));

        var verdict = LinearizabilityChecker.Check(KvModel.Instance, history, TestContext.Current.CancellationToken);

        Assert.Equal(linearizable ? LinearizabilityVerdict.Ok : LinearizabilityVerdict.Illegal, verdict);
    }

    [Theory]
    [MemberData(nameof(UnpartitionedHistories))]
    public void UnpartitionedCheckReachesPorcupinesVerdict(string name)
    {
        var (file, linearizable) = Vector(name);
        var history = Parse(File.ReadAllLines(SharedVectors.FilePath(Area, file)));

        var verdict = LinearizabilityChecker.Check(UnpartitionedKvModel.Instance, history, TestContext.Current.CancellationToken);

        Assert.Equal(linearizable ? LinearizabilityVerdict.Ok : LinearizabilityVerdict.Illegal, verdict);
    }

    /// <summary>
    /// The verbose check, stopping at the first illegal partition, explains each verdict: a linearizable history has one
    /// complete linearization per partition, and an illegal one has an illegal partition; no partial linearization of a
    /// partition which is not linearized covers its operations.
    /// </summary>
    [Theory]
    [MemberData(nameof(Histories))]
    public void VerboseCheckExplainsPorcupinesVerdict(string name)
    {
        var (file, linearizable) = Vector(name);
        var history = Parse(File.ReadAllLines(SharedVectors.FilePath(Area, file)));

        var analysis = LinearizabilityChecker.CheckVerbose(KvModel.Instance, history, stopAtFirstIllegal: true, TestContext.Current.CancellationToken);

        Assert.Equal(linearizable ? LinearizabilityVerdict.Ok : LinearizabilityVerdict.Illegal, analysis.Verdict);
        Assert.Equal(
            history.Where(static historyEvent => historyEvent.Kind == HistoryEventKind.Call).Select(static historyEvent => historyEvent.Input.Key).Distinct(StringComparer.Ordinal).Count(),
            analysis.Partitions.Count);
        foreach (var partition in analysis.Partitions)
        {
            if (partition.Verdict == LinearizabilityVerdict.Ok)
            {
                var linearization = Assert.Single(partition.PartialLinearizations);
                Assert.Equal(Enumerable.Range(0, partition.Operations.Count), linearization.Order());
            }
            else
            {
                Assert.All(partition.PartialLinearizations, linearization => Assert.True(linearization.Count < partition.Operations.Count));
            }
        }

        Assert.Equal(!linearizable, analysis.Partitions.Any(static partition => partition.Verdict == LinearizabilityVerdict.Illegal));
        Assert.Contains("const data = {", PorcupineVisualization.Html(analysis), StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses a history as Porcupine's <c>parseKvLog</c> does: an invocation without a response responds after every
    /// other event with an empty value.
    /// </summary>
    private static HistoryEvent<KvInput, string>[] Parse(string[] lines)
    {
        var events = new List<HistoryEvent<KvInput, string>>();
        var pending = new Dictionary<int, int>();
        var id = 0;
        foreach (var line in lines)
        {
            if (Invocation().Match(line) is { Success: true } invocation)
            {
                var process = int.Parse(invocation.Groups["process"].Value, CultureInfo.InvariantCulture);
                var key = invocation.Groups["key"].Value;
                var value = invocation.Groups["value"].Value;
                var input = invocation.Groups["f"].Value switch
                {
                    "get" when value == "nil" => KvInput.Get(key),
                    "put" when value.StartsWith('"') => KvInput.Put(key, value[1..^1]),
                    "append" when value.StartsWith('"') => KvInput.Append(key, value[1..^1]),
                    _ => throw new InvalidDataException($"Unexpected invocation: {line}"),
                };
                events.Add(new HistoryEvent<KvInput, string> { Kind = HistoryEventKind.Call, Id = id, ClientId = process, Input = input });
                pending.Add(process, id++);
            }
            else if (Response().Match(line) is { Success: true } response)
            {
                var process = int.Parse(response.Groups["process"].Value, CultureInfo.InvariantCulture);
                var output = response.Groups["f"].Value == "get" ? response.Groups["value"].Value : string.Empty;
                Assert.True(pending.Remove(process, out var call), $"A response without an invocation: {line}");
                events.Add(new HistoryEvent<KvInput, string> { Kind = HistoryEventKind.Return, Id = call, Output = output });
            }
            else if (line.Length > 0)
            {
                throw new InvalidDataException($"Unexpected line: {line}");
            }
        }

        foreach (var call in pending.Values.Order())
        {
            events.Add(new HistoryEvent<KvInput, string> { Kind = HistoryEventKind.Return, Id = call, Output = string.Empty });
        }

        return [.. events];
    }

    private static (string File, bool Linearizable) Vector(string name)
    {
        var vector = SharedVectors.Cases(Area, "histories.json", "histories").Single(element => SharedVectors.Name(element) == name);
        return (SharedVectors.Text(vector, "file"), vector.GetProperty("linearizable").GetBoolean());
    }

    [GeneratedRegex("""^\{:process (?<process>\d+), :type :invoke, :f :(?<f>get|put|append), :key "(?<key>.*)", :value (?<value>nil|".*")\}$""", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 10_000)]
    private static partial Regex Invocation();

    [GeneratedRegex("""^\{:process (?<process>\d+), :type :ok, :f :(?<f>get|put|append), :key ".*", :value "(?<value>.*)"\}$""", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 10_000)]
    private static partial Regex Response();
}
