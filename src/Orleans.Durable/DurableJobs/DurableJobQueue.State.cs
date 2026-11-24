using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans.Durable.Journaling;

namespace Orleans.Durable.Jobs;

/// <summary>
/// A change to a grain's <see cref="DurableJobQueue"/> journaled state.
/// </summary>
[GenerateSerializer, Alias("Orleans.Durable.Jobs.DurableJobQueueCommand")]
[JsonUnion(TypeClassifier = typeof(DurableJobQueueCommandClassifier))]
internal union DurableJobQueueCommand(
    DurableJobQueueCommand.Schedule,
    DurableJobQueueCommand.Cancel,
    DurableJobQueueCommand.Update,
    DurableJobQueueCommand.Snapshot)
{
    [GenerateSerializer, Alias("Orleans.Durable.Jobs.DurableJobQueueCommand.Schedule")]
    internal sealed class Schedule(PendingGrainJob job)
    {
        [Id(0)]
        public PendingGrainJob Job { get; } = job;

        [JsonPropertyName("kind"), JsonPropertyOrder(-1)]
        public string Kind => "schedule";
    }

    [GenerateSerializer, Alias("Orleans.Durable.Jobs.DurableJobQueueCommand.Cancel")]
    internal sealed class Cancel(string jobId)
    {
        [Id(0)]
        public string JobId { get; } = jobId;

        [JsonPropertyName("kind"), JsonPropertyOrder(-1)]
        public string Kind => "cancel";
    }

    [GenerateSerializer, Alias("Orleans.Durable.Jobs.DurableJobQueueCommand.Update")]
    internal sealed class Update(string jobId, DateTimeOffset dueTime, int dequeueCount, long executionGeneration)
    {
        [Id(0)]
        public string JobId { get; } = jobId;

        [Id(1)]
        public DateTimeOffset DueTime { get; } = dueTime;

        [Id(2)]
        public int DequeueCount { get; } = dequeueCount;

        [Id(3)]
        public long ExecutionGeneration { get; } = executionGeneration;

        [JsonPropertyName("kind"), JsonPropertyOrder(-1)]
        public string Kind => "update";
    }

    [GenerateSerializer, Alias("Orleans.Durable.Jobs.DurableJobQueueCommand.Snapshot")]
    internal sealed class Snapshot(PendingGrainJob[] jobs, long nextGeneration)
    {
        [Id(0)]
        public PendingGrainJob[] Jobs { get; } = jobs;

        [Id(1)]
        public long NextGeneration { get; } = nextGeneration;

        [JsonPropertyName("kind"), JsonPropertyOrder(-1)]
        public string Kind => "snapshot";
    }
}

/// <summary>Selects a <see cref="DurableJobQueueCommand"/> case from its leading JSON <c>kind</c> property.</summary>
internal sealed class DurableJobQueueCommandClassifier : JsonTypeClassifierFactory
{
    public override bool CanClassify(JsonTypeClassifierContext context) => context.DeclaringType == typeof(DurableJobQueueCommand);

    public override JsonTypeClassifier CreateJsonClassifier(JsonTypeClassifierContext context, JsonSerializerOptions options) => Classify;

    private static Type? Classify(ref Utf8JsonReader reader)
    {
        var lookahead = reader;
        if (lookahead.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        while (lookahead.Read() && lookahead.TokenType == JsonTokenType.PropertyName)
        {
            var isKind = lookahead.ValueTextEquals("kind"u8);
            lookahead.Read();
            if (isKind)
            {
                return lookahead.GetString() switch
                {
                    "schedule" => typeof(DurableJobQueueCommand.Schedule),
                    "cancel" => typeof(DurableJobQueueCommand.Cancel),
                    "update" => typeof(DurableJobQueueCommand.Update),
                    "snapshot" => typeof(DurableJobQueueCommand.Snapshot),
                    _ => null,
                };
            }

            lookahead.Skip();
        }

        return null;
    }
}

[JsonSerializable(typeof(DurableJobQueueCommand))]
[JsonSerializable(typeof(PendingGrainJob))]
[JsonSerializable(typeof(PendingGrainJob[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
internal sealed partial class DurableJobQueueJsonContext : JsonSerializerContext;
