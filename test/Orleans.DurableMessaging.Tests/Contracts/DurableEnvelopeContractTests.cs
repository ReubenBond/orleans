using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DurableEnvelopeContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(100000)]
    public void EnvelopeSerializer_RoundTripsGCBytesAcrossIndependentProviders(int length)
    {
        var expected = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        var envelope = Envelope(expected);
        using var sending = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var receiving = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = sending.GetRequiredService<Serializer<DurableEnvelope>>();
        var wire = serializer.SerializeToArray(envelope);
        var decoded = receiving.GetRequiredService<Serializer<DurableEnvelope>>().Deserialize(wire);
        AssertEnvelope(decoded, expected);
        Assert.Equal(envelope.MessageId, decoded.MessageId);
        Assert.Equal(envelope.SenderId, decoded.SenderId);
        Assert.Equal(envelope.ReceiverId, decoded.ReceiverId);
        Assert.Equal(envelope.Subject, decoded.Subject);
        if (length > 0)
        {
            Assert.NotSame(expected, decoded.Payload);
            decoded.Payload[0] ^= 255;
            Assert.Equal((byte)0, expected[0]);
            Assert.NotEqual(expected[0], decoded.Payload[0]);
        }
        Assert.Equal(wire, serializer.SerializeToArray(envelope));
    }

    [Fact]
    public void EnvelopeDeepCopy_IsolatesMutablePayloadArrays()
    {
        var envelope = Envelope([0, 255, 128, 234]);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var copied = services.GetRequiredService<DeepCopier>().Copy(envelope);
        AssertEnvelope(copied, [0, 255, 128, 234]);
        Assert.NotSame(envelope.Payload, copied.Payload);
        copied.Payload[0] = 42;
        envelope.Payload[1] = 81;
        Assert.Equal(new byte[] { 0, 81, 128, 234 }, envelope.Payload);
        Assert.Equal(new byte[] { 42, 255, 128, 234 }, copied.Payload);
    }

    [Fact]
    public void StructAssignment_SharesOrdinaryArrayUntilExplicitlyCopied()
    {
        var envelope = Envelope([0, 255, 128]);
        var assigned = envelope;
        Assert.Same(envelope.Payload, assigned.Payload);
        assigned.Payload[0] = 42;
        Assert.Equal(new byte[] { 42, 255, 128 }, envelope.Payload);
        Assert.Equal(envelope.MessageId, assigned.MessageId);
        Assert.DoesNotContain(typeof(IDisposable), typeof(DurableEnvelope).GetInterfaces());
    }

    [Theory]
    [InlineData("Envelope")]
    [InlineData("InboxDeadLetter")]
    [InlineData("OutboxDeadLetter")]
    public void SharedContracts_OrdinarySerializationAndCopyIsolateGCBytePayload(string contract)
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var envelope = Envelope([0, 255, 128]);
        object original = envelope;
        if (contract != "Envelope")
        {
            var type = typeof(DurableEnvelope).Assembly.GetType($"Orleans.DurableMessaging.{contract}", throwOnError: true)!;
            original = Activator.CreateInstance(type, nonPublic: true)!;
            type.GetProperty("Envelope")!.SetValue(original, envelope);
            type.GetProperty("DeadLetteredAt")!.SetValue(original, DateTimeOffset.UnixEpoch);
            type.GetProperty("Reason")!.SetValue(original, "failure");
            type.GetProperty("AttemptCount")!.SetValue(original, 3);
        }
        var serializer = services.GetRequiredService<Serializer>();
        var decoded = serializer.Deserialize<object>(serializer.SerializeToArray(original));
        Assert.NotNull(decoded);
        var copied = services.GetRequiredService<DeepCopier>().Copy(original);
        foreach (var value in new[] { decoded, copied })
        {
            Assert.NotNull(value);
            var actual = value is DurableEnvelope e ? e : Assert.IsType<DurableEnvelope>(value.GetType().GetProperty("Envelope")!.GetValue(value));
            AssertEnvelope(actual, [0, 255, 128]);
            Assert.NotSame(envelope.Payload, actual.Payload);
            Assert.Equal(envelope.MessageId, actual.MessageId);
            Assert.Equal(envelope.SenderId, actual.SenderId);
            Assert.Equal(envelope.ReceiverId, actual.ReceiverId);
            if (contract != "Envelope")
            {
                Assert.Equal(DateTimeOffset.UnixEpoch, value.GetType().GetProperty("DeadLetteredAt")!.GetValue(value));
                Assert.Equal("failure", value.GetType().GetProperty("Reason")!.GetValue(value));
                Assert.Equal(3, value.GetType().GetProperty("AttemptCount")!.GetValue(value));
            }
            actual.Payload[0] = 42;
            Assert.Equal((byte)0, envelope.Payload[0]);
        }
    }

    [Fact]
    public void OpaqueContracts_ExposeExactPublicSurfaceAndContiguousIds()
    {
        Assert.True(typeof(DurableEnvelope).IsPublic);
        Assert.True(typeof(DurableEnvelope).IsValueType);
        Assert.Single(typeof(DurableEnvelope).GetCustomAttributes<IsReadOnlyAttribute>());
        Assert.Single(typeof(DurableEnvelope).GetCustomAttributes<GenerateSerializerAttribute>());
        Assert.Equal("Orleans.DurableMessaging.DurableEnvelope", Assert.Single(
            typeof(DurableEnvelope).GetCustomAttributes<AliasAttribute>()).Alias);
        AssertSurface(typeof(DurableEnvelope),
            Property("MessageId", typeof(HierarchicalKey), true),
            Property("SenderId", typeof(GrainId), true),
            Property("ReceiverId", typeof(GrainId), true),
            Property("Payload", typeof(byte[]), true),
            Property("Subject", typeof(string), true));
        AssertIds(typeof(DurableEnvelope), ("MessageId", 0u), ("SenderId", 1u), ("ReceiverId", 2u), ("Payload", 3u), ("Subject", 4u));
        foreach (var property in typeof(DurableEnvelope).GetProperties())
        {
            Assert.Single(property.GetCustomAttributes<RequiredMemberAttribute>());
            Assert.Contains(typeof(IsExternalInit), property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
        }

        Assert.False(typeof(IInboxHandler).IsGenericType);
        AssertSurface(typeof(IInboxHandler),
            Method("HandleAsync", typeof(ValueTask), typeof(IInboxHandlerContext), typeof(CancellationToken)));
        AssertSurface(typeof(IInboxHandlerContext),
            Property("Envelope", typeof(DurableEnvelope)), Method("Complete", typeof(void)));
        AssertSurface(typeof(IDurableInbox),
            Property("Count", typeof(int)), Property("Capacity", typeof(int)),
            Property("Messages", typeof(IEnumerable<DurableEnvelope>)),
            Method("RegisterHandler", typeof(void), typeof(IInboxHandler)),
            Method("TryGetMessage", typeof(bool), typeof(HierarchicalKey), typeof(DurableEnvelope).MakeByRefType()));
        AssertSurface(typeof(IDurableOutbox),
            Property("SenderId", typeof(GrainId)),
            Property("Count", typeof(int)), Property("Messages", typeof(IEnumerable<DurableEnvelope>)),
            Method("Send", typeof(void), typeof(DurableEnvelope)),
            Method("TryGetMessage", typeof(bool), typeof(HierarchicalKey), typeof(DurableEnvelope).MakeByRefType()));
        var sender = typeof(IDurableOutbox).GetProperty(nameof(IDurableOutbox.SenderId))!;
        Assert.True(sender.GetMethod!.IsAbstract);
        Assert.Null(sender.SetMethod);
        Assert.True(Assert.Single(typeof(IDurableInbox).GetMethod("TryGetMessage")!.GetParameters(), p => p.ParameterType.IsByRef).IsOut);
        Assert.True(Assert.Single(typeof(IDurableOutbox).GetMethod("TryGetMessage")!.GetParameters(), p => p.ParameterType.IsByRef).IsOut);

        Assert.Equal(new[] { "Accepted:0", "Backpressured:2", "DeadLettered:4", "Duplicate:1", "HandlerNotFound:3" },
            Enum.GetNames<DeliveryStatus>().Select(name => $"{name}:{(int)Enum.Parse<DeliveryStatus>(name)}").Order(StringComparer.Ordinal));
        AssertIds(typeof(DeliveryResult), ("Status", 0u), ("Message", 1u));
        AssertSurface(typeof(DeliveryResult), Property("Status", typeof(DeliveryStatus), true), Property("Message", typeof(string), true),
            StaticMethod("Accepted", typeof(DeliveryResult)), StaticMethod("Duplicate", typeof(DeliveryResult)),
            StaticMethod("Backpressured", typeof(DeliveryResult)), StaticMethod("HandlerNotFound", typeof(DeliveryResult)),
            StaticMethod("DeadLettered", typeof(DeliveryResult), typeof(string)));

        Assert.DoesNotContain(typeof(IDisposable), typeof(DurableEnvelope).GetInterfaces());
        Assert.Null(typeof(DurableEnvelope).GetMethod("Retain"));

    }

    [Theory]
    [InlineData("id", 1024, true)]
    [InlineData("id", 1025, false)]
    [InlineData("subject", 256, true)]
    [InlineData("subject", 257, false)]
    [InlineData("depth", 32, true)]
    [InlineData("depth", 33, false)]
    public void Admission_IdentityAndSubjectLimits_EnforceExactBoundaries(string field, int length, bool valid)
    {
        var envelope = Envelope([]) with
        {
            MessageId = field switch
            {
                "id" => HierarchicalKey.Create(new string('x', length)),
                "depth" => HierarchicalKey.Create(Enumerable.Repeat("x", length).ToArray()),
                _ => HierarchicalKey.Create("command")
            },
            Subject = field == "subject" ? new string('s', length) : "contract.v1"
        };
        if (valid)
        {
            Validate(envelope);
            Assert.Empty(envelope.Payload);
        }
        else
        {
            var error = Assert.Throws<ArgumentException>(() => Validate(envelope));
            Assert.Contains(field == "depth" ? "segments" : "UTF-8 bytes", error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("id", 512, true)]
    [InlineData("id", 513, false)]
    [InlineData("subject", 128, true)]
    [InlineData("subject", 129, false)]
    public void Admission_CountsUTF8BytesRatherThanUTF16Characters(string field, int repetitions, bool valid)
    {
        var unicode = new string((char)0xE9, repetitions);
        var envelope = Envelope([]) with
        {
            MessageId = field == "id" ? HierarchicalKey.Create(unicode) : HierarchicalKey.Create("command"),
            Subject = field == "subject" ? unicode : "contract.v1"
        };
        if (valid) Validate(envelope);
        else Assert.Throws<ArgumentException>(() => Validate(envelope));
        Assert.Equal(repetitions * 2, System.Text.Encoding.UTF8.GetByteCount(unicode));
    }

    [Theory]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public void Admission_CountsEscapedCanonicalKeyBytes(int repetitions, bool valid)
    {
        var envelope = Envelope([]) with { MessageId = HierarchicalKey.Create(new string('/', repetitions)) };
        Assert.Equal(repetitions * 2, envelope.MessageId.Length);
        Assert.Equal(1, envelope.MessageId.SegmentCount);
        if (valid) Validate(envelope);
        else Assert.Throws<ArgumentException>(() => Validate(envelope));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("sender")]
    [InlineData("receiver")]
    [InlineData("empty-subject")]
    [InlineData("null-subject")]
    [InlineData("invalid-unicode-id")]
    [InlineData("invalid-unicode-subject")]
    [InlineData("null-payload")]
    public void Admission_InvalidMetadata_RejectsBeforePayloadUse(string field)
    {
        var envelope = Envelope([]);
        var invalid = field switch
        {
            "id" => envelope with { MessageId = default },
            "null-payload" => envelope with { Payload = null! },
            "sender" => envelope with { SenderId = default },
            "receiver" => envelope with { ReceiverId = default },
            "empty-subject" => envelope with { Subject = "" },
            "null-subject" => envelope with { Subject = null! },
            "invalid-unicode-id" => envelope with { MessageId = HierarchicalKey.Create(new string((char)0xD800, 1)) },
            "invalid-unicode-subject" => envelope with { Subject = new string((char)0xD800, 1) },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        Assert.ThrowsAny<ArgumentException>(() => Validate(invalid));
        Assert.Equal("contract.v1", envelope.Subject);
        Assert.False(envelope.MessageId.IsDefault);
        Assert.Empty(envelope.Payload);
    }

    [Theory]
    [InlineData("subject.v1")]
    [InlineData("Subject.V1")]
    [InlineData(" subject.v1 ")]
    [InlineData(" ")]
    public void ValidateSubject_PreservesExactOrdinalSpelling(string subject)
    {
        InvokeInternal("DurableEnvelopeValidation", "ValidateSubject", subject);
        var envelope = Envelope([]) with { Subject = subject };
        Validate(envelope);
        Assert.Equal(subject, envelope.Subject);
    }

    [Fact]
    public void KeyGeneralUtility_RemainsUnboundedByTransportAdmissionPolicy()
    {
        var key = HierarchicalKey.Create(Enumerable.Repeat(new string('x', 1024), 33).ToArray());
        Assert.Equal(33, key.SegmentCount);
        Assert.Equal(33 * 1024 + 32, key.Length);
        Assert.Equal(key, HierarchicalKey.Parse(key.ToString()));
        var envelope = Envelope([]) with { MessageId = key };
        Assert.Throws<ArgumentException>(() => Validate(envelope));
    }

    [Theory]
    [InlineData("same", true, true)]
    [InlineData("id", false, false)]
    [InlineData("child", false, false)]
    [InlineData("sender", false, true)]
    [InlineData("receiver", false, false)]
    [InlineData("subject", false, false)]
    [InlineData("subject-case", false, false)]
    [InlineData("payload", false, false)]
    [InlineData("payload-length", false, false)]
    public void Equivalence_PreservesOutboxIntentAndSenderIndependentPendingCommand(string change, bool outbox, bool inbox)
    {
        var original = Envelope([0, 255, 128]);
        var repeated = original with
        {
            Payload = change switch { "payload" => [0, 255, 129], "payload-length" => [0, 255], _ => [0, 255, 128] },
            MessageId = change == "id" ? HierarchicalKey.Create("other") : change == "child" ? original.MessageId.CreateChildKey("child") : original.MessageId,
            SenderId = change == "sender" ? GrainId.Create("sender", "forwarder") : original.SenderId,
            ReceiverId = change == "receiver" ? GrainId.Create("receiver", "other") : original.ReceiverId,
            Subject = change == "subject" ? "other.v1" : change == "subject-case" ? "Contract.v1" : original.Subject
        };
        Assert.Equal(outbox, InvokeInternal<bool>("DurableEnvelopeEquivalence", "AreEquivalent", original, repeated));
        Assert.Equal(inbox, InvokeInternal<bool>("DurableEnvelopeEquivalence", "AreSameCommand", original, repeated));
        Assert.Equal(outbox, InvokeInternal<bool>("DurableEnvelopeEquivalence", "AreEquivalent", repeated, original));
        Assert.Equal(inbox, InvokeInternal<bool>("DurableEnvelopeEquivalence", "AreSameCommand", repeated, original));
        Assert.Equal(new byte[] { 0, 255, 128 }, original.Payload);
    }

    private static void Validate(DurableEnvelope envelope) => InvokeInternal("DurableEnvelopeValidation", "Validate", envelope);

    private static T InvokeInternal<T>(string typeName, string methodName, params object[] arguments) =>
        Assert.IsType<T>(InvokeInternal(typeName, methodName, arguments));

    private static object? InvokeInternal(string typeName, string methodName, params object[] arguments)
    {
        var type = typeof(DurableEnvelope).Assembly.GetType($"Orleans.DurableMessaging.{typeName}", throwOnError: true)!;
        try
        {
            return type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, arguments);
        }
        catch (TargetInvocationException error) when (error.InnerException is { } cause)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw();
            throw;
        }
    }

    private static void AssertIds(Type type, params (string Name, uint Id)[] expected)
    {
        var actual = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(member => member.GetCustomAttributes<IdAttribute>().Select(id => (member.Name, id.Id)))
            .OrderBy(pair => pair.Name, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.OrderBy(pair => pair.Name, StringComparer.Ordinal), actual);
        Assert.Equal(expected.Length, actual.Select(pair => pair.Id).Distinct().Count());
    }

    private static void AssertSurface(Type type, params string[] expected)
    {
        var types = new[] { type }.Concat(type.GetInterfaces());
        var actual = types.SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(member => member.DeclaringType != typeof(object) && member.DeclaringType != typeof(ValueType))
            .Select(member => member switch
            {
                PropertyInfo property => (property.GetMethod!.IsStatic ? "static " : "") +
                    Property(property.Name, property.PropertyType, property.SetMethod is not null),
                MethodInfo method when !method.IsSpecialName => (method.IsStatic ? "static " : "") +
                    Method(method.Name, method.ReturnType, method.GetParameters().Select(p => p.ParameterType).ToArray()) +
                    (method.IsGenericMethod ? $" generic:{method.GetGenericArguments().Length}" : ""),
                MethodInfo => null,
                ConstructorInfo => null,
                _ => $"unexpected:{member.MemberType}:{member.Name}"
            }).Where(signature => signature is not null).Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    private static string Property(string name, Type type, bool set = false) =>
        $"property {TypeName(type)} {name} get{(set ? "/set" : "")}";
    private static string Method(string name, Type result, params Type[] parameters) =>
        $"method {TypeName(result)} {name}({string.Join(",", parameters.Select(TypeName))})";
    private static string StaticMethod(string name, Type result, params Type[] parameters) => "static " + Method(name, result, parameters);
    private static string TypeName(Type type) => type.FullName!;

    private static DurableEnvelope Envelope(byte[] payload) => new()
    {
        MessageId = HierarchicalKey.Create("11111111-1111-1111-1111-111111111111"),
        SenderId = GrainId.Create("sender", "contract"),
        ReceiverId = GrainId.Create("receiver", "contract"),
        Subject = "contract.v1",
        Payload = payload
    };

    private static void AssertEnvelope(DurableEnvelope envelope, byte[] expected)
    {
        Assert.Equal(HierarchicalKey.Create("11111111-1111-1111-1111-111111111111"), envelope.MessageId);
        Assert.Equal(GrainId.Create("sender", "contract"), envelope.SenderId);
        Assert.Equal(GrainId.Create("receiver", "contract"), envelope.ReceiverId);
        Assert.Equal("contract.v1", envelope.Subject);
        Assert.Equal(expected.Length, envelope.Payload.Length);
        Assert.Equal(expected, envelope.Payload);

    }

}
