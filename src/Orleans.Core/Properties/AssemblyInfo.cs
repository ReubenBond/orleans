using System;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Codecs;

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
        KeepType(typeof(TupleCodec<Orleans.Runtime.SiloAddress, System.DateTime>));
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<NetworkProtocolVersion>)); // These types are probably not needed: can avoid DI and request the codec from CodecProvider directly.
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<DateTime>));
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<bool>));
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<GrainInterfaceType>));
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<GrainType>));
        KeepType(typeof(Orleans.Serialization.ServiceCollectionExtensions.FieldCodecHolder<Orleans.Runtime.GrainId>));
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<float>));
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<long>));
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<int>));
        KeepType(typeof(ServiceCollectionExtensions.FieldCodecHolder<AddressAndTag>));
        KeepType(typeof(ServiceCollectionExtensions.CopierHolder<bool>));
        KeepType(typeof(IFieldCodec<NetworkProtocolVersion>));
        KeepType(typeof(ImmutableDictionaryCodec<Orleans.Runtime.GrainType, Orleans.Metadata.GrainProperties>));
        KeepType(typeof(Orleans.Serialization.ServiceCollectionExtensions.ValueSerializerHolder<Orleans.Serialization.Codecs.ImmutableDictionarySurrogate<Orleans.Runtime.GrainType, Orleans.Metadata.GrainProperties>>));
        KeepType(typeof(Orleans.Serialization.ServiceCollectionExtensions.ValueSerializerHolder<Orleans.Serialization.Codecs.ImmutableDictionarySurrogate<Orleans.Runtime.GrainInterfaceType, Orleans.Metadata.GrainInterfaceProperties>>));
        KeepType(typeof(Orleans.Serialization.Codecs.ImmutableDictionaryCodec<Orleans.Runtime.GrainInterfaceType, Orleans.Metadata.GrainInterfaceProperties>));
        KeepType(typeof(OrleansCodeGen.Orleans.Serialization.Codecs.Codec_ImmutableDictionarySurrogate<Orleans.Runtime.GrainInterfaceType, Orleans.Metadata.GrainInterfaceProperties>));
        KeepType(typeof(OrleansCodeGen.Orleans.Serialization.Codecs.Codec_ImmutableDictionarySurrogate<Orleans.Runtime.GrainType, Orleans.Metadata.GrainProperties>));
        KeepType(typeof(Orleans.Serialization.ServiceCollectionExtensions.ValueSerializerHolder<Orleans.Serialization.Codecs.ImmutableDictionarySurrogate<System.String, System.String>>));
        KeepType(typeof(Orleans.Serialization.ServiceCollectionExtensions.CopierHolder<System.ValueTuple<Orleans.Runtime.GrainId, System.Int32>>));
        KeepType(typeof(Orleans.Serialization.Codecs.ImmutableDictionaryCodec<Orleans.Runtime.SiloAddress, System.ValueTuple<System.Collections.Immutable.ImmutableHashSet<Orleans.Runtime.GrainId>, System.Int64>>));
        KeepType(typeof(Orleans.Serialization.ServiceCollectionExtensions.ValueSerializerHolder<Orleans.Serialization.Codecs.ImmutableDictionarySurrogate<Orleans.Runtime.SiloAddress, System.ValueTuple<System.Collections.Immutable.ImmutableHashSet<Orleans.Runtime.GrainId>, System.Int64>>>));
        KeepType(typeof(OrleansCodeGen.Orleans.Serialization.Codecs.Codec_ImmutableDictionarySurrogate<Orleans.Runtime.SiloAddress, System.ValueTuple<System.Collections.Immutable.ImmutableHashSet<Orleans.Runtime.GrainId>, System.Int64>>));
        KeepType(typeof(Orleans.Serialization.ServiceCollectionExtensions.FieldCodecHolder<System.ValueTuple<System.Collections.Immutable.ImmutableHashSet<Orleans.Runtime.GrainId>, System.Int64>>));
        KeepType(typeof(Orleans.Serialization.Codecs.ValueTupleCodec<System.Collections.Immutable.ImmutableHashSet<Orleans.Runtime.GrainId>, System.Int64>));
        KeepType(typeof(Orleans.Serialization.Codecs.ImmutableHashSetCodec<Orleans.Runtime.GrainId>));
        KeepType(typeof(Orleans.Serialization.ServiceCollectionExtensions.ValueSerializerHolder<Orleans.Serialization.Codecs.ImmutableHashSetSurrogate<Orleans.Runtime.GrainId>>));
        KeepType(typeof(Orleans.Serialization.Invocation.PooledResponseCopier<bool>));
        KeepType(typeof(Orleans.Serialization.Invocation.PooledResponseCodec<bool>));
        KeepType(typeof(OrleansCodeGen.Orleans.Serialization.Codecs.Codec_ImmutableHashSetSurrogate<Orleans.Runtime.GrainId>));
        KeepType(typeof(Orleans.Serialization.Codecs.TupleCopier<Orleans.Runtime.SiloAddress, System.DateTime>));
        KeepType(typeof(Orleans.Serialization.Invocation.PooledResponseCopier<Orleans.GrainDirectory.AddressAndTag>));
    }

    private static string KeepType([DynamicallyAccessedMembers(All)] Type type)
    {
        return type.ToString();
    }
}
