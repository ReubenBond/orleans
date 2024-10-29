using BenchmarkDotNet.Attributes;
using Benchmarks.Models;
using Benchmarks.Utilities;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Xunit;
using SerializerSession = Orleans.Serialization.Session.SerializerSession;
using Utf8JsonNS = Utf8Json;
using Hyperion;
using ZeroFormatter;
    using global::Orleans.Serialization.Codecs;
    using global::Orleans.Serialization.GeneratedCodeHelpers;
using BenchmarkDotNet.Diagnostics.Windows.Configs;

namespace Benchmarks.Comparison;

[Trait("Category", "Benchmark")]
[Config(typeof(BenchmarkConfig))]
[DisassemblyDiagnoser(maxDepth: 5, printSource: true, exportHtml: true, exportCombinedDisassemblyReport: true)]
[RyuJitX64Job]
[EtwProfiler]
[HardwareCounters]
[InliningDiagnoser(logFailuresOnly: false, filterByNamespace: false)]
[JitStatsDiagnoser]
public class StructDeserializeBenchmark
{
    private static readonly MemoryStream ProtoInput;
    private static readonly string NewtonsoftJsonInput = JsonConvert.SerializeObject(IntStruct.Create());

    private static readonly byte[] SpanJsonInput = SpanJson.JsonSerializer.Generic.Utf8.Serialize(IntStruct.Create());

    private static readonly byte[] MsgPackInput = MessagePack.MessagePackSerializer.Serialize(IntStruct.Create());
    private static readonly byte[] ZeroFormatterInput = ZeroFormatterSerializer.Serialize(IntStruct.Create());

    private static readonly Hyperion.Serializer HyperionSerializer = new(SerializerOptions.Default.WithKnownTypes(new[] { typeof(IntStruct) }));
    private static readonly MemoryStream HyperionInput;
    private static readonly DeserializerSession HyperionSession;

    private static readonly ValueSerializer<IntStruct> Serializer;
    private static readonly byte[] Input;
    private static readonly SerializerSession Session;

    private static readonly Utf8JsonNS.IJsonFormatterResolver Utf8JsonResolver = Utf8JsonNS.Resolvers.StandardResolver.Default;
    private static readonly byte[] Utf8JsonInput;
    private static readonly byte[] SystemTextJsonInput;

    static StructDeserializeBenchmark()
    {
        ProtoInput = new MemoryStream();
        ProtoBuf.Serializer.Serialize(ProtoInput, IntStruct.Create());

        HyperionInput = new MemoryStream();
        HyperionSerializer.Serialize(IntStruct.Create(), HyperionInput);

        // 
        var services = new ServiceCollection()
            .AddSerializer()
            .BuildServiceProvider();
        Serializer = services.GetRequiredService<ValueSerializer<IntStruct>>();
        Session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var bytes = new byte[1000];
        var writer = new SingleSegmentBuffer(bytes).CreateWriter(Session);
        IntStruct intStruct = IntStruct.Create();
        Serializer.Serialize(ref intStruct, ref writer);
        Input = bytes;

        HyperionSession = HyperionSerializer.GetDeserializerSession();

        Utf8JsonInput = Utf8JsonNS.JsonSerializer.Serialize(IntStruct.Create(), Utf8JsonResolver);

        var stream = new MemoryStream();
        using (var jsonWriter = new System.Text.Json.Utf8JsonWriter(stream))
        {
            System.Text.Json.JsonSerializer.Serialize(jsonWriter, IntStruct.Create());
        }

        SystemTextJsonInput = stream.ToArray();
    }

    private static int SumResult(in IntStruct result) => result.MyProperty1 +
               result.MyProperty2 +
               result.MyProperty3 +
               result.MyProperty4 +
               result.MyProperty5 +
               result.MyProperty6 +
               result.MyProperty7 +
               result.MyProperty8 +
               result.MyProperty9;

    [Benchmark(Baseline = true)]
    public int Orleans()
    {
        IntStruct result = default;
        Serializer.Deserialize(Input, ref result, Session);
        return result.MyProperty1;
    }

    [Benchmark]
    public int Utf8Json() => SumResult(Utf8JsonNS.JsonSerializer.Deserialize<IntStruct>(Utf8JsonInput, Utf8JsonResolver));

    [Benchmark]
    public int SystemTextJson() => SumResult(System.Text.Json.JsonSerializer.Deserialize<IntStruct>(SystemTextJsonInput));

    [Benchmark]
    public int MessagePackCSharp() => SumResult(MessagePack.MessagePackSerializer.Deserialize<IntStruct>(MsgPackInput));

    [Benchmark]
    public int ProtobufNet()
    {
        ProtoInput.Position = 0;
        return SumResult(ProtoBuf.Serializer.Deserialize<IntStruct>(ProtoInput));
    }

    [Benchmark]
    public int Hyperion()
    {
        HyperionInput.Position = 0;
        return SumResult(HyperionSerializer.Deserialize<IntStruct>(HyperionInput, HyperionSession));
    }

    //[Benchmark]
    public int ZeroFormatter() => SumResult(ZeroFormatterSerializer.Deserialize<IntStruct>(ZeroFormatterInput));

    [Benchmark]
    public int NewtonsoftJson() => SumResult(JsonConvert.DeserializeObject<IntStruct>(NewtonsoftJsonInput));

    [Benchmark(Description = "SpanJson")]
    public int SpanJsonUtf8() => SumResult(SpanJson.JsonSerializer.Generic.Utf8.Deserialize<IntStruct>(SpanJsonInput));
}

[global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "8.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never)]
//[RegisterSerializer]
public sealed class Codec_IntStruct_Switch : global::Orleans.Serialization.Codecs.IFieldCodec<global::Benchmarks.Models.IntStruct>, global::Orleans.Serialization.Serializers.IValueSerializer<global::Benchmarks.Models.IntStruct>
{
    private readonly global::System.Type _codecFieldType = typeof(global::Benchmarks.Models.IntStruct);
    [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, scoped ref global::Benchmarks.Models.IntStruct instance)
        where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
    {
        global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 0U, instance.MyProperty1);
        global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 1U, instance.MyProperty2);
        global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 1U, instance.MyProperty3);
        global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 1U, instance.MyProperty4);
        global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 1U, instance.MyProperty5);
        global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 1U, instance.MyProperty6);
        global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 1U, instance.MyProperty7);
        global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 1U, instance.MyProperty8);
        global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 1U, instance.MyProperty9);
    }

    [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, scoped ref global::Benchmarks.Models.IntStruct instance)
    {
        uint id = 0U;
        global::Orleans.Serialization.WireProtocol.Field header = default;
        while (reader.ReadFieldHeader(ref header, ref id))
        {
            switch ((int)id)
            {
                case 0:
                    instance.MyProperty1 = global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header);
                    break;
                case 1:
                    instance.MyProperty2 = global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header);
                    break;
                case 2:
                    instance.MyProperty3 = global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header);
                    break;
                case 3:
                    instance.MyProperty4 = global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header);
                    break;
                case 4:
                    instance.MyProperty5 = global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header);
                    break;
                case 5:
                    instance.MyProperty6 = global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header);
                    break;
                case 6:
                    instance.MyProperty7 = global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header);
                    break;
                case 7:
                    instance.MyProperty8 = global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header);
                    break;
                case 8:
                    instance.MyProperty9 = global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header);
                    break;
                default:
                    reader.ConsumeEndBaseOrEndObject(ref header);
                    break;
            }
        }
    }

    [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::Benchmarks.Models.IntStruct @value)
        where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
        Serialize(ref writer, ref @value);
        writer.WriteEndObject();
    }

    [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public global::Benchmarks.Models.IntStruct ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
    {
        field.EnsureWireTypeTagDelimited();
        var result = default(global::Benchmarks.Models.IntStruct);
        ReferenceCodec.MarkValueField(reader.Session);
        Deserialize(ref reader, ref result);
        return result;
    }
}