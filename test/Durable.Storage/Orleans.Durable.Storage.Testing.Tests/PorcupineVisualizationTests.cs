using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using KvHistoryOperation = Orleans.Durable.Storage.Testing.HistoryOperation<Orleans.Durable.Storage.Testing.Tests.KvInput, string>;

namespace Orleans.Durable.Storage.Testing.Tests;

/// <summary>
/// The visualization matches Porcupine's: the data its <c>computeVisualizationData</c> computes, embedded with its
/// unchanged page, stylesheet, and script as its <c>Visualize</c> writes them.
/// </summary>
public sealed class PorcupineVisualizationTests
{
    /// <summary>
    /// The history of Porcupine's <c>TestVisualizationMultipleLengths</c>.
    /// </summary>
    private static readonly KvHistoryOperation[] MultipleLengths =
    [
        new(0, KvInput.Get("x"), 0, "w", 100),
        new(1, KvInput.Put("x", "y"), 5, string.Empty, 10),
        new(2, KvInput.Put("x", "z"), 0, string.Empty, 10),
        new(1, KvInput.Get("x"), 20, "y", 30),
        new(1, KvInput.Put("x", "w"), 35, string.Empty, 45),
        new(5, KvInput.Get("x"), 25, "z", 35),
        new(3, KvInput.Get("x"), 30, "y", 40),
        new(4, KvInput.Get("y"), 50, "a", 90),
        new(2, KvInput.Put("y", "a"), 55, string.Empty, 85),
    ];

    /// <summary>
    /// Porcupine's <c>TestVisualizationMultipleLengths</c>: the timestamps, descriptions, partial linearizations with
    /// their states, and each operation's largest partial linearization are exactly Porcupine's.
    /// </summary>
    [Fact]
    public void DataMatchesPorcupineForPartialLinearizationsOfSeveralLengths()
    {
        var analysis = LinearizabilityChecker.CheckVerbose(KvModel.Instance, MultipleLengths, TestContext.Current.CancellationToken);

        Assert.Equal(LinearizabilityVerdict.Illegal, analysis.Verdict);
        var expected = JsonNode.Parse(
            """
            {
              "Partitions": [
                {
                  "History": [
                    { "ClientId": 0, "Start": 0, "OriginalStart": "0", "End": 1300, "OriginalEnd": "100", "Description": "get('x') -> 'w'", "Metadata": "" },
                    { "ClientId": 1, "Start": 100, "OriginalStart": "5", "End": 200, "OriginalEnd": "10", "Description": "put('x', 'y')", "Metadata": "" },
                    { "ClientId": 2, "Start": 0, "OriginalStart": "0", "End": 200, "OriginalEnd": "10", "Description": "put('x', 'z')", "Metadata": "" },
                    { "ClientId": 1, "Start": 300, "OriginalStart": "20", "End": 500, "OriginalEnd": "30", "Description": "get('x') -> 'y'", "Metadata": "" },
                    { "ClientId": 1, "Start": 600, "OriginalStart": "35", "End": 800, "OriginalEnd": "45", "Description": "put('x', 'w')", "Metadata": "" },
                    { "ClientId": 5, "Start": 400, "OriginalStart": "25", "End": 600, "OriginalEnd": "35", "Description": "get('x') -> 'z'", "Metadata": "" },
                    { "ClientId": 3, "Start": 500, "OriginalStart": "30", "End": 700, "OriginalEnd": "40", "Description": "get('x') -> 'y'", "Metadata": "" }
                  ],
                  "PartialLinearizations": [
                    [
                      { "Index": 2, "StateDescription": "z" }, { "Index": 1, "StateDescription": "y" }, { "Index": 3, "StateDescription": "y" },
                      { "Index": 6, "StateDescription": "y" }, { "Index": 4, "StateDescription": "w" }, { "Index": 0, "StateDescription": "w" }
                    ],
                    [ { "Index": 1, "StateDescription": "y" }, { "Index": 2, "StateDescription": "z" }, { "Index": 5, "StateDescription": "z" } ]
                  ],
                  "Largest": { "0": 0, "1": 0, "2": 0, "3": 0, "4": 0, "5": 1, "6": 0 }
                },
                {
                  "History": [
                    { "ClientId": 4, "Start": 900, "OriginalStart": "50", "End": 1200, "OriginalEnd": "90", "Description": "get('y') -> 'a'", "Metadata": "" },
                    { "ClientId": 2, "Start": 1000, "OriginalStart": "55", "End": 1100, "OriginalEnd": "85", "Description": "put('y', 'a')", "Metadata": "" }
                  ],
                  "PartialLinearizations": [ [ { "Index": 1, "StateDescription": "a" }, { "Index": 0, "StateDescription": "a" } ] ],
                  "Largest": { "0": 0, "1": 0 }
                }
              ],
              "Annotations": []
            }
            """);
        var actual = JsonNode.Parse(PorcupineVisualization.Data(analysis));
        Assert.True(JsonNode.DeepEquals(expected, actual), actual!.ToJsonString());
    }

    /// <summary>
    /// Porcupine's <c>TestVisualizationAnnotations</c>: annotations share the history's timestamp mapping, a point in
    /// time ends where it starts, and an annotation on a tag's row has client 0.
    /// </summary>
    [Fact]
    public void AnnotationsShareTheHistorysTimeline()
    {
        var analysis = LinearizabilityChecker.CheckVerbose(KvModel.Instance, MultipleLengths, TestContext.Current.CancellationToken);
        analysis.AddAnnotations(
        [
            new(10, "get('y') timeout") { ClientId = 4, End = 31, BackgroundColor = "#ff9191" },
            new(80, "get('x') timeout") { ClientId = 5, BackgroundColor = "#ff9191" },
            new(30, "leader") { Tag = "Server 1", Details = "became leader in term 3 with 2 votes" },
            new(40, "partition [2] [1 3]") { Tag = "Test Framework", End = 20, TextColor = "#000000" },
        ]);

        var annotations = JsonNode.Parse(PorcupineVisualization.Data(analysis))!["Annotations"];

        // The timestamps are the history's (0, 5, 10, 20, 25, 30, 35, 40, 45, 50, 55, 85, 90, 100) and the annotations'
        // own 31 and 80, each mapped to its rank times 100.
        var expected = JsonNode.Parse(
            """
            [
              { "ClientId": 4, "Tag": "", "Start": 200, "End": 600, "Description": "get('y') timeout", "Details": "", "Annotation": true, "TextColor": "", "BackgroundColor": "#ff9191" },
              { "ClientId": 5, "Tag": "", "Start": 1200, "End": 1200, "Description": "get('x') timeout", "Details": "", "Annotation": true, "TextColor": "", "BackgroundColor": "#ff9191" },
              { "ClientId": 0, "Tag": "Server 1", "Start": 500, "End": 500, "Description": "leader", "Details": "became leader in term 3 with 2 votes", "Annotation": true, "TextColor": "", "BackgroundColor": "" },
              { "ClientId": 0, "Tag": "Test Framework", "Start": 800, "End": 800, "Description": "partition [2] [1 3]", "Details": "", "Annotation": true, "TextColor": "#000000", "BackgroundColor": "" }
            ]
            """);
        Assert.True(JsonNode.DeepEquals(expected, annotations), annotations!.ToJsonString());
        Assert.Equal(1500, (long)JsonNode.Parse(PorcupineVisualization.Data(analysis))!["Partitions"]![0]!["History"]![0]!["End"]!);
    }

    /// <summary>
    /// The page is Porcupine's <c>index.html</c> with its three placeholders filled, as <c>fmt.Fprintf</c> fills them, by
    /// the unchanged stylesheet, script, and the data on one line.
    /// </summary>
    [Fact]
    public void HtmlIsPorcupinesPageWithTheData()
    {
        var analysis = LinearizabilityChecker.CheckVerbose(KvModel.Instance, MultipleLengths, TestContext.Current.CancellationToken);
        var template = Asset("index.html").Split("%s");

        var html = PorcupineVisualization.Html(analysis);

        Assert.Equal(4, template.Length);
        var data = Encoding.UTF8.GetString(PorcupineVisualization.Data(analysis));
        Assert.Equal(string.Concat(template[0], Asset("index.css"), template[1], Asset("index.js"), template[2], data, template[3]), html);
        Assert.DoesNotContain('\n', data);
        Assert.Contains("const data = " + data + "\n", html.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Text the model describes is escaped, so the data cannot end the script element it is embedded in.
    /// </summary>
    [Fact]
    public void DescriptionsCannotEscapeTheScript()
    {
        KvHistoryOperation[] history = [new(0, KvInput.Put("x", "</script><b>&'"), 0, string.Empty, 1)];
        var analysis = LinearizabilityChecker.CheckVerbose(KvModel.Instance, history, TestContext.Current.CancellationToken);

        var data = Encoding.UTF8.GetString(PorcupineVisualization.Data(analysis));

        Assert.DoesNotContain("</script>", data, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('<', data);
        Assert.Equal("put('x', '</script><b>&'')", (string?)JsonNode.Parse(data)!["Partitions"]![0]!["History"]![0]!["Description"]);
    }

    /// <summary>
    /// Porcupine's <c>TestVisualizationCallAndReturnTime</c> and metadata tests: original timestamps are kept as decimal
    /// strings, an unbounded response included, and metadata is described on every operation, linearized or not.
    /// </summary>
    [Fact]
    public void OriginalTimesAndMetadataAreKept()
    {
        KvHistoryOperation[] history =
        [
            new(0, KvInput.Put("x", "a"), 0, string.Empty, 100, "meta_linearizable"),
            new(1, KvInput.Get("x"), 10, "b", 110, 7),
            new(2, KvInput.Get("x"), 120, "a", HistoryOperation.Unbounded),
        ];
        var analysis = LinearizabilityChecker.CheckVerbose(KvModel.Instance, history, TestContext.Current.CancellationToken);

        var elements = JsonNode.Parse(PorcupineVisualization.Data(analysis))!["Partitions"]![0]!["History"]!.AsArray();

        Assert.Equal(LinearizabilityVerdict.Illegal, analysis.Verdict);
        Assert.Equal(["0", "10", "120"], elements.Select(static element => (string?)element!["OriginalStart"]));
        Assert.Equal(["100", "110", long.MaxValue.ToString(CultureInfo.InvariantCulture)], elements.Select(static element => (string?)element!["OriginalEnd"]));
        Assert.Equal(["meta_linearizable", "7", string.Empty], elements.Select(static element => (string?)element!["Metadata"]));
    }

    /// <summary>
    /// A file written for a failing history holds the page.
    /// </summary>
    [Fact]
    public void WriteFileWritesThePage()
    {
        var analysis = LinearizabilityChecker.CheckVerbose(KvModel.Instance, MultipleLengths, TestContext.Current.CancellationToken);
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".html");
        try
        {
            PorcupineVisualization.WriteFile(analysis, path);

            Assert.Equal(PorcupineVisualization.Html(analysis), File.ReadAllText(path));
            Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Asset(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Orleans.slnx")))
            {
                return File.ReadAllText(Path.Combine(directory.FullName, "test", "Durable.Storage", "shared", "Linearizability", "Visualization", name));
            }
        }

        throw new DirectoryNotFoundException("Could not find the Orleans repository root.");
    }
}
