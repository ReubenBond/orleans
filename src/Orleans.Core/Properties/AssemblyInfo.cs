using System;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Invocation;
using OrleansCodeGen.Orleans.Serialization.Codecs;

[assembly: InternalsVisibleTo("Orleans.BroadcastChannel")]
[assembly: InternalsVisibleTo("Orleans.CodeGeneration")]
[assembly: InternalsVisibleTo("Orleans.CodeGeneration.Build")]
[assembly: InternalsVisibleTo("Orleans.Runtime")]
[assembly: InternalsVisibleTo("Orleans.Streaming")]
[assembly: InternalsVisibleTo("Orleans.TestingHost")]

[assembly: InternalsVisibleTo("AWSUtils.Tests")]
[assembly: InternalsVisibleTo("DefaultCluster.Tests")]
[assembly: InternalsVisibleTo("GoogleUtils.Tests")]
[assembly: InternalsVisibleTo("LoadTestGrains")]
[assembly: InternalsVisibleTo("NonSilo.Tests")]
[assembly: InternalsVisibleTo("Benchmarks")]
[assembly: InternalsVisibleTo("Tester")]
[assembly: InternalsVisibleTo("Tester.AzureUtils")]
[assembly: InternalsVisibleTo("Tester.AdoNet")]
[assembly: InternalsVisibleTo("Tester.Redis")]
[assembly: InternalsVisibleTo("Tester.ZooKeeperUtils")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("TestExtensions")]
[assembly: InternalsVisibleTo("TestInternalGrains")]
[assembly: InternalsVisibleTo("CodeGenerator.Tests")]

[assembly: InternalsVisibleTo("Orleans.Reminders")]

// Mocking libraries
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]

public static class PinnedTypes
{
    public static void GoBePinned()
    {
        // DI resolution shims
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<NetworkProtocolVersion>)); // These types are probably not needed: can avoid DI and request the codec from CodecProvider directly.
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<DateTime>));
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<bool>));
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<GrainInterfaceType>));
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<GrainType>));
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<GrainId>));
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<float>));
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<long>));
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<int>));
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<AddressAndTag>));
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<ValueTuple<ImmutableHashSet<GrainId>, long>>));
        KeepType(typeof(ServiceCollectionExtensions.CopierHolder<bool>));
        KeepType(typeof(ServiceCollectionExtensions.CopierHolder<ValueTuple<GrainId, int>>));
        KeepType(typeof(ServiceCollectionExtensions.CopierHolder<AddressAndTag>));
        KeepType(typeof(ServiceCollectionExtensions.ValueSerializerHolder<ImmutableDictionarySurrogate<string, string>>));
        KeepType(typeof(ServiceCollectionExtensions.ValueSerializerHolder<ImmutableDictionarySurrogate<SiloAddress, ValueTuple<ImmutableHashSet<GrainId>, long>>>));
        KeepType(typeof(ServiceCollectionExtensions.ValueSerializerHolder<ImmutableHashSetSurrogate<GrainId>>));
        KeepType(typeof(ServiceCollectionExtensions.ValueSerializerHolder<ImmutableDictionarySurrogate<GrainType, GrainProperties>>));
        KeepType(typeof(ServiceCollectionExtensions.ValueSerializerHolder<ImmutableDictionarySurrogate<GrainInterfaceType, GrainInterfaceProperties>>));

        // Hand-codec codecs
        KeepType(typeof(ImmutableDictionaryCodec<GrainType, GrainProperties>));
        KeepType(typeof(ImmutableDictionaryCodec<GrainInterfaceType, GrainInterfaceProperties>));
        KeepType(typeof(ImmutableDictionaryCodec<SiloAddress, ValueTuple<ImmutableHashSet<GrainId>, long>>));
        KeepType(typeof(ValueTupleCodec<ImmutableHashSet<GrainId>, long>));
        KeepType(typeof(ImmutableHashSetCodec<GrainId>));
        KeepType(typeof(PooledResponseCopier<bool>));
        KeepType(typeof(PooledResponseCopier<AddressAndTag>));
        KeepType(typeof(PooledResponseCodec<bool>));
        KeepType(typeof(PooledResponseCodec<AddressAndTag>));
        KeepType(typeof(TupleCopier<SiloAddress, DateTime>));
        KeepType(typeof(TupleCodec<SiloAddress, DateTime>));

        // Generated codecs
        KeepType(typeof(Codec_ImmutableDictionarySurrogate<GrainInterfaceType, GrainInterfaceProperties>));
        KeepType(typeof(Codec_ImmutableDictionarySurrogate<GrainType, GrainProperties>));
        KeepType(typeof(Codec_ImmutableDictionarySurrogate<SiloAddress, ValueTuple<ImmutableHashSet<GrainId>, long>>));
        KeepType(typeof(Codec_ImmutableHashSetSurrogate<GrainId>));
    }

    private static string KeepType([DynamicallyAccessedMembers(All)] Type type)
    {
        return type.ToString();
    }
}
