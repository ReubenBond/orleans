// Ported from Porcupine (https://github.com/anishathalye/porcupine, commit 97cd067), Copyright (c) Anish Athalye,
// under the MIT License in PORCUPINE-LICENSE.md.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Renders a <see cref="LinearizabilityAnalysis{TState, TInput, TOutput}"/> as Porcupine's visualization: one HTML file
/// with Porcupine's stylesheet, script, and the history's data, which a browser opens without a server.
/// </summary>
/// <remarks>
/// The page shows each client's operations on a time line and, for each partition, the longest (partial)
/// linearizations with the model's state after each step; an operation which no partial linearization reaches is
/// where the history stops being linearizable. The assets under <c>Visualization/</c> are Porcupine's, unchanged; a
/// test project embeds them as <c>Orleans.Durable.Storage.Testing.Linearizability.index.html</c>, <c>.index.css</c>, and
/// <c>.index.js</c>.
/// </remarks>
internal static class PorcupineVisualization
{
    private const string ResourcePrefix = "Orleans.Durable.Storage.Testing.Linearizability.";

    /// <summary>
    /// The distance between consecutive distinct timestamps in the visualization's data. Porcupine's
    /// <c>timestampMapping</c> keeps it at least 100 so <c>index.js</c> can offset timestamps by small amounts without
    /// overlapping, and keeps the values below JavaScript's largest safe integer.
    /// </summary>
    private const long TimestampSpacing = 100;

    /// <summary>
    /// Writes the visualization to a file.
    /// </summary>
    /// <param name="analysis">The checked history.</param>
    /// <param name="path">The file to write.</param>
    public static void WriteFile<TState, TInput, TOutput>(LinearizabilityAnalysis<TState, TInput, TOutput> analysis, string path)
        where TInput : notnull =>
        File.WriteAllText(path, Html(analysis), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>
    /// Returns the visualization's HTML.
    /// </summary>
    /// <param name="analysis">The checked history.</param>
    /// <returns>The page.</returns>
    public static string Html<TState, TInput, TOutput>(LinearizabilityAnalysis<TState, TInput, TOutput> analysis)
        where TInput : notnull
    {
        // index.html has three %s placeholders, which Porcupine's Visualize fills with the stylesheet, the script, and
        // the data through fmt.Fprintf.
        var template = Resource("index.html").Split("%s");
        if (template.Length != 4)
        {
            throw new InvalidOperationException("The visualization template must have exactly three placeholders.");
        }

        return string.Concat(
            template[0], Resource("index.css"), template[1], Resource("index.js"), template[2], Encoding.UTF8.GetString(Data(analysis)), template[3]);
    }

    /// <summary>
    /// Returns the visualization's data as Porcupine's <c>computeVisualizationData</c> encodes it: UTF-8 JSON with
    /// HTML-sensitive characters escaped, so it can be embedded in a script element.
    /// </summary>
    /// <param name="analysis">The checked history.</param>
    /// <returns>The JSON.</returns>
    public static byte[] Data<TState, TInput, TOutput>(LinearizabilityAnalysis<TState, TInput, TOutput> analysis)
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var times = TimestampMapping(analysis);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("Partitions");
            foreach (var partition in analysis.Partitions)
            {
                WritePartition(writer, analysis.Model, partition, times);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("Annotations");
            foreach (var annotation in analysis.Annotations)
            {
                writer.WriteStartObject();
                writer.WriteNumber("ClientId", annotation.ClientId);
                writer.WriteString("Tag", annotation.Tag ?? string.Empty);
                writer.WriteNumber("Start", times[annotation.Start]);
                writer.WriteNumber("End", times[End(annotation)]);
                writer.WriteString("Description", annotation.Description);
                writer.WriteString("Details", annotation.Details ?? string.Empty);
                writer.WriteBoolean("Annotation", true);
                writer.WriteString("TextColor", annotation.TextColor ?? string.Empty);
                writer.WriteString("BackgroundColor", annotation.BackgroundColor ?? string.Empty);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WritePartition<TState, TInput, TOutput>(
        Utf8JsonWriter writer,
        LinearizabilityModel<TState, TInput, TOutput> model,
        LinearizabilityPartition<TInput, TOutput> partition,
        Dictionary<long, long> times)
        where TInput : notnull
    {
        var operations = partition.Operations;
        writer.WriteStartObject();
        writer.WriteStartArray("History");
        foreach (var operation in operations)
        {
            writer.WriteStartObject();
            writer.WriteNumber("ClientId", operation.ClientId);
            writer.WriteNumber("Start", times[operation.Call]);
            writer.WriteString("OriginalStart", operation.Call.ToString(CultureInfo.InvariantCulture));
            writer.WriteNumber("End", times[operation.Return]);
            writer.WriteString("OriginalEnd", operation.Return.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("Description", model.DescribeOperation(operation.Input, operation.Output));
            writer.WriteString("Metadata", model.DescribeMetadata(operation.Metadata));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        // For each operation, the longest partial linearization which includes it; the first among equals.
        var largestSize = new int[operations.Count];
        var largestIndex = new int[operations.Count];
        writer.WriteStartArray("PartialLinearizations");
        for (var index = 0; index < partition.PartialLinearizations.Count; index++)
        {
            var linearization = partition.PartialLinearizations[index];
            var state = model.Init();
            writer.WriteStartArray();
            foreach (var operation in linearization)
            {
                if (!model.Step(state, operations[operation].Input, operations[operation].Output, out state))
                {
                    throw new InvalidOperationException(
                        "The model rejects a step of a linearization the checker found, so its Step is not a pure function.");
                }

                writer.WriteStartObject();
                writer.WriteNumber("Index", operation);
                writer.WriteString("StateDescription", model.DescribeState(state));
                writer.WriteEndObject();
                if (largestSize[operation] < linearization.Count)
                {
                    largestSize[operation] = linearization.Count;
                    largestIndex[operation] = index;
                }
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        writer.WriteStartObject("Largest");
        for (var operation = 0; operation < operations.Count; operation++)
        {
            if (largestSize[operation] > 0)
            {
                writer.WriteNumber(operation.ToString(CultureInfo.InvariantCulture), largestIndex[operation]);
            }
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Maps every timestamp of the history and its annotations, in order, to multiples of
    /// <see cref="TimestampSpacing"/>.
    /// </summary>
    private static Dictionary<long, long> TimestampMapping<TState, TInput, TOutput>(LinearizabilityAnalysis<TState, TInput, TOutput> analysis)
        where TInput : notnull
    {
        var timestamps = new SortedSet<long>();
        foreach (var partition in analysis.Partitions)
        {
            foreach (var operation in partition.Operations)
            {
                _ = timestamps.Add(operation.Call);
                _ = timestamps.Add(operation.Return);
            }
        }

        foreach (var annotation in analysis.Annotations)
        {
            _ = timestamps.Add(annotation.Start);
            _ = timestamps.Add(End(annotation));
        }

        var mapping = new Dictionary<long, long>(timestamps.Count);
        foreach (var timestamp in timestamps)
        {
            mapping.Add(timestamp, mapping.Count * TimestampSpacing);
        }

        return mapping;
    }

    private static long End(LinearizabilityAnnotation annotation) => Math.Max(annotation.End ?? annotation.Start, annotation.Start);

    private static string Resource(string name)
    {
        using var stream = typeof(PorcupineVisualization).Assembly.GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidOperationException(
                $"The test assembly does not embed {ResourcePrefix}{name}; embed test/Durable.Storage/shared/Linearizability/Visualization/{name} with that logical name.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
