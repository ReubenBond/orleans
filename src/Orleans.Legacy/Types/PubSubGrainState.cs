using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Orleans.Concurrency;
using Orleans.Legacy;
using Orleans.Runtime;

namespace Orleans.Streams
{
    // This is the extension interface for stream consumers
    internal interface IStreamConsumerExtension : IGrainExtension
    {
    }

    // This is the extension interface for stream producers
    internal interface IStreamProducerExtension : IGrainExtension
    {
    }

    public interface IStreamIdentity
    {
        /// <summary> Stream primary key guid. </summary>
        Guid Guid { get; }

        /// <summary> Stream namespace. </summary>
        string Namespace { get; }
    }

    /// <summary>
    /// Identifier of an Orleans virtual stream.
    /// </summary>
    [Serializable]
    [Immutable]
    internal class StreamId : IStreamIdentity, IEquatable<StreamId>, IComparable<StreamId>, ISerializable
    {
        [NonSerialized]
        private static readonly Lazy<Interner<StreamIdInternerKey, StreamId>> streamIdInternCache = new Lazy<Interner<StreamIdInternerKey, StreamId>>(
            () => new Interner<StreamIdInternerKey, StreamId>(InternerConstants.SIZE_LARGE, InternerConstants.DefaultCacheCleanupFreq));

        [NonSerialized]
        private uint uniformHashCache;
        private readonly StreamIdInternerKey key;

        // Keep public, similar to GrainId.GetPrimaryKey. Some app scenarios might need that.
        public Guid Guid { get { return key.Guid; } }

        // I think it might be more clear if we called this the ActivationNamespace.
        public string Namespace { get { return key.Namespace; } }

        public string ProviderName { get { return key.ProviderName; } }

        // TODO: need to integrate with Orleans serializer to really use Interner.
        private StreamId(StreamIdInternerKey key)
        {
            this.key = key;
        }

        internal static StreamId GetStreamId(Guid guid, string providerName, string streamNamespace)
        {
            return FindOrCreateStreamId(new StreamIdInternerKey(guid, providerName, streamNamespace));
        }

        private static StreamId FindOrCreateStreamId(StreamIdInternerKey key)
        {
            return streamIdInternCache.Value.FindOrCreate(key, k => new StreamId(k));
        }

        public int CompareTo(StreamId other)
        {
            return key.CompareTo(other.key);
        }

        public bool Equals(StreamId other)
        {
            return other != null && key.Equals(other.key);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as StreamId);
        }

        public override int GetHashCode()
        {
            return key.GetHashCode();
        }

        public uint GetUniformHashCode()
        {
            if (uniformHashCache == 0)
            {
                byte[] guidBytes = Guid.ToByteArray();
                byte[] providerBytes = Encoding.UTF8.GetBytes(ProviderName);
                byte[] allBytes;
                if (Namespace == null)
                {
                    allBytes = new byte[guidBytes.Length + providerBytes.Length];
                    Array.Copy(guidBytes, allBytes, guidBytes.Length);
                    Array.Copy(providerBytes, 0, allBytes, guidBytes.Length, providerBytes.Length);
                }
                else
                {
                    byte[] namespaceBytes = Encoding.UTF8.GetBytes(Namespace);
                    allBytes = new byte[guidBytes.Length + providerBytes.Length + namespaceBytes.Length];
                    Array.Copy(guidBytes, allBytes, guidBytes.Length);
                    Array.Copy(providerBytes, 0, allBytes, guidBytes.Length, providerBytes.Length);
                    Array.Copy(namespaceBytes, 0, allBytes, guidBytes.Length + providerBytes.Length, namespaceBytes.Length);
                }
                uniformHashCache = JenkinsHash.ComputeHash(allBytes);
            }
            return uniformHashCache;
        }

        public override string ToString()
        {
            return Namespace == null ? 
                Guid.ToString() : 
                String.Format("{0}{1}-{2}", Namespace != null ? (String.Format("{0}-", Namespace)) : "", Guid, ProviderName);
        }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            // Use the AddValue method to specify serialized values.
            info.AddValue("Guid", Guid, typeof(Guid));
            info.AddValue("ProviderName", ProviderName, typeof(string));
            info.AddValue("Namespace", Namespace, typeof(string));
        }

        // The special constructor is used to deserialize values. 
        protected StreamId(SerializationInfo info, StreamingContext context)
        {
            // Reset the property value using the GetValue method.
            var guid = (Guid) info.GetValue("Guid", typeof(Guid));
            var providerName = (string) info.GetValue("ProviderName", typeof(string));
            var nameSpace = (string) info.GetValue("Namespace", typeof(string));
            key = new StreamIdInternerKey(guid, providerName, nameSpace);
        }
    }

    [Serializable]
    [Immutable]
    internal struct StreamIdInternerKey : IComparable<StreamIdInternerKey>, IEquatable<StreamIdInternerKey>
    {
        internal readonly Guid Guid;
        internal readonly string ProviderName;
        internal readonly string Namespace;

        public StreamIdInternerKey(Guid guid, string providerName, string streamNamespace)
        {
            if (string.IsNullOrWhiteSpace(providerName))
            {
                throw new ArgumentException("Provider name is null or whitespace", "providerName");
            }

            Guid = guid;
            ProviderName = providerName;
            if (streamNamespace == null)
            {
                Namespace = null;
            }
            else
            {
                if (String.IsNullOrWhiteSpace(streamNamespace))
                {
                    throw new ArgumentException("namespace must be null or substantive (not empty or whitespace).");
                }

                Namespace = streamNamespace.Trim();
            }
        }

        public int CompareTo(StreamIdInternerKey other)
        {
            int cmp1 = Guid.CompareTo(other.Guid);
            if (cmp1 == 0)
            {
                int cmp2 = string.Compare(ProviderName, other.ProviderName, StringComparison.Ordinal);
                return cmp2 == 0 ? string.Compare(Namespace, other.Namespace, StringComparison.Ordinal) : cmp2;
            }
            
            return cmp1;
        }

        public bool Equals(StreamIdInternerKey other)
        {
            return Guid.Equals(other.Guid) && Object.Equals(ProviderName, other.ProviderName) && Object.Equals(Namespace, other.Namespace);
        }

        public override int GetHashCode()
        {
            return Guid.GetHashCode() ^ (ProviderName != null ? ProviderName.GetHashCode() : 0) ^ (Namespace != null ? Namespace.GetHashCode() : 0);
        }
    }

    /// <summary>
    /// Filter predicate for streams. 
    /// Classes implementing this interface MUST be [Serializable]
    /// </summary>
    internal interface IStreamFilterPredicateWrapper
    {
        object FilterData { get; }

        /// <summary>
        /// Should this item be delivered to the intended receiver?
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="filterData"></param>
        /// <param name="item">Item sent through the stream.</param>
        /// <returns>Return <c>true</c> if this item should be delivered to the intended recipient.</returns>
        bool ShouldReceive(IStreamIdentity stream, object filterData, object item);
    }

    internal class DefaultStreamFilterPredicateWrapper : IStreamFilterPredicateWrapper
    {
        public object FilterData { get { return default(object); } }
        public bool ShouldReceive(IStreamIdentity stream, object filterData, object item)
        {
            return true;
        }
    }

    public delegate bool StreamFilterPredicate(IStreamIdentity stream, object filterData, object item);

    /// <summary>
    /// This class is a [Serializable] function pointer to a static predicate method, used for stream filtering.
    /// The predicate function / lambda is not directly serialized, only the class / method info details required to reconstruct the function reference on the other side.
    /// Predicate filter functions must be static (non-abstract) methods, so full class name and method name are sufficient info to rehydrate.
    /// </summary>
    [Serializable]
    internal class FilterPredicateWrapperData : IStreamFilterPredicateWrapper, ISerializable
    {
        private static readonly ITypeResolver TypeResolver = new CachedTypeResolver();

        public object FilterData { get; private set; }

        private string methodName;
        private string className;

        private const string SER_FIELD_CLASS  = "ClassName";
        private const string SER_FIELD_DATA   = "FilterData";
        private const string SER_FIELD_METHOD = "MethodName";

        [NonSerialized]
        private StreamFilterPredicate predicateFunc;

        internal FilterPredicateWrapperData(object filterData, StreamFilterPredicate pred)
        {
            CheckFilterPredicateFunc(pred); // Assert expected pre-conditions are always true.

            FilterData = filterData;
            predicateFunc = pred;

            DehydrateStaticFunc(pred);
        }

        protected FilterPredicateWrapperData(SerializationInfo info, StreamingContext context)
        {
            FilterData = info.GetValue(SER_FIELD_DATA, typeof(object));
            methodName = info.GetString(SER_FIELD_METHOD);
            className  = info.GetString(SER_FIELD_CLASS);

            predicateFunc = RehydrateStaticFuncion(className, methodName);
        }
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(SER_FIELD_DATA,   FilterData);
            info.AddValue(SER_FIELD_METHOD, methodName);
            info.AddValue(SER_FIELD_CLASS,  className);
        }

        public bool ShouldReceive(IStreamIdentity stream, object filterData, object item)
        {
            if (predicateFunc == null)
            {
                predicateFunc = RehydrateStaticFuncion(className, methodName);
            }
            return predicateFunc(stream, filterData, item);
        }

        private static StreamFilterPredicate RehydrateStaticFuncion(string funcClassName, string funcMethodName)
        {
            Type funcClassType = TypeResolver.ResolveType(funcClassName);
            MethodInfo method = funcClassType.GetMethod(funcMethodName);
            StreamFilterPredicate pred = (StreamFilterPredicate) method.CreateDelegate(typeof(StreamFilterPredicate));
#if DEBUG
            CheckFilterPredicateFunc(pred); // Assert expected pre-conditions are always true.
#endif
            return pred;
        }

        private void DehydrateStaticFunc(StreamFilterPredicate pred)
        {
#if DEBUG
            CheckFilterPredicateFunc(pred); // Assert expected pre-conditions are always true.
#endif
            MethodInfo method = pred.GetMethodInfo();
            className = method.DeclaringType.FullName;
            methodName = method.Name;
        }

        /// <summary>
        /// Check that the user-supplied stream predicate function is valid.
        /// Stream predicate functions must be static and not abstract.
        /// </summary>
        private static void CheckFilterPredicateFunc(StreamFilterPredicate predicate)
        {
            if (predicate == null)
            {
                throw new ArgumentNullException("predicate", "Stream Filter predicate function must not be null.");
            }

            MethodInfo method = predicate.GetMethodInfo();

            if (!method.IsStatic || method.IsAbstract)
            {
                throw new ArgumentException("Stream Filter predicate function must be static and not abstract.");
            }
        }

        public override string ToString()
        {
            return string.Format("StreamFilterFunction:Class={0},Method={1}", className, methodName);
        }
    }

    /// <summary>
    /// This class is a [Serializable] holder for a logical-or composite predicate function.
    /// </summary>
    [Serializable]
    internal class OrFilter : IStreamFilterPredicateWrapper, ISerializable
    {
        public object FilterData
        {
            get
            {
                // This FilterData field is only evey passed in to our own ShouldReceive function below, 
                // which does not actually use it for anything.
                // Underlying filters are passed their own FilterData objects which were passed in 
                // when the original Subscribe call was made.
                return null;
            }
        }

        private readonly List<IStreamFilterPredicateWrapper> filters; // Serializable func info

        [NonSerialized]
        private static readonly Type serializedType = typeof(List<IStreamFilterPredicateWrapper>);

        public OrFilter(IStreamFilterPredicateWrapper filter1, IStreamFilterPredicateWrapper filter2)
        {
            filters = new List<IStreamFilterPredicateWrapper> { filter1, filter2 };
        }

        public void AddFilter(IStreamFilterPredicateWrapper filter)
        {
            filters.Add(filter);
        }

        protected OrFilter(SerializationInfo info, StreamingContext context)
        {
            filters = (List<IStreamFilterPredicateWrapper>)info.GetValue("Filters", serializedType);
        }
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("Filters", this.filters, serializedType);
        }

        public bool ShouldReceive(IStreamIdentity stream, object filterData, object item)
        {
            if (filters == null || filters.Count == 0) return true;

            foreach (var filter in filters)
            {
                if (filter.ShouldReceive(stream, filter.FilterData, item))
                    return true; // We got the answer for logical-or predicate
            }

            return false; // Everybody said 'no'
        }
    }

    [Serializable]
    internal class PubSubGrainState
    {
        public HashSet<PubSubPublisherState> Producers { get; set; } = new HashSet<PubSubPublisherState>();
        public HashSet<PubSubSubscriptionState> Consumers { get; set; } = new HashSet<PubSubSubscriptionState>();
    }

    [Serializable]
    [JsonObject(MemberSerialization.OptIn)]
    internal class PubSubSubscriptionState : IEquatable<PubSubSubscriptionState>
    {
        internal enum SubscriptionStates
        {
            Active,
            Faulted,
        }

        // IMPORTANT!!!!!
        // These fields have to be public non-readonly for JSonSerialization to work!
        // Implement ISerializable if changing any of them to readonly
        [JsonProperty]
        public GuidId SubscriptionId;
        [JsonProperty]
        public StreamId Stream;
        [JsonProperty]
        public GrainReference consumerReference; // the field needs to be of a public type, otherwise we will not generate an Orleans serializer for that class.
        [JsonProperty]
        public object filterWrapper; // Serialized func info
        [JsonProperty]
        public SubscriptionStates state;

        // This property does not need to be Json serialized, since we already have producerReference.
        [JsonIgnore]
        public IStreamConsumerExtension Consumer { get { return consumerReference as IStreamConsumerExtension; } }
        [JsonIgnore]
        public IStreamFilterPredicateWrapper Filter { get { return filterWrapper as IStreamFilterPredicateWrapper; } }
        [JsonIgnore]
        public bool IsFaulted { get { return state == SubscriptionStates.Faulted; } }

        // This constructor has to be public for JSonSerialization to work!
        // Implement ISerializable if changing it to non-public
        public PubSubSubscriptionState(
            GuidId subscriptionId,
            StreamId streamId,
            IStreamConsumerExtension streamConsumer)
        {
            SubscriptionId = subscriptionId;
            Stream = streamId;
            consumerReference = streamConsumer as GrainReference;
            state = SubscriptionStates.Active;
        }

        internal void AddFilter(IStreamFilterPredicateWrapper newFilter)
        {
            if (filterWrapper == null)
            {
                // No existing filter - add single
                filterWrapper = newFilter;
            }
            else if (filterWrapper is OrFilter)
            {
                // Existing multi-filter - add new filter to it
                ((OrFilter)filterWrapper).AddFilter(newFilter);
            }
            else
            {
                // Exsiting single filter - convert to multi-filter
                filterWrapper = new OrFilter(Filter, newFilter);
            }
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            // Note: Can't use the 'as' operator on PubSubSubscriptionState because it is a struct.
            return obj is PubSubSubscriptionState && Equals((PubSubSubscriptionState) obj);
        }
        public bool Equals(PubSubSubscriptionState other)
        {
            if ((object)other == null)
                return false;
            // Note: PubSubSubscriptionState is a struct, so 'other' can never be null.
            return Equals(other.SubscriptionId);
        }
        public bool Equals(GuidId subscriptionId)
        {
            if (ReferenceEquals(null, subscriptionId)) return false;
            return SubscriptionId.Equals(subscriptionId);
        }

        public override int GetHashCode()
        {
            return SubscriptionId.GetHashCode();
        }

        public static bool operator ==(PubSubSubscriptionState left, PubSubSubscriptionState right)
        {
            if ((object)left == null && (object)right == null)
                return true;
            if ((object)left != null)
            {
                return left.Equals(right);
            }
            return false;
        }

        public static bool operator !=(PubSubSubscriptionState left, PubSubSubscriptionState right)
        {
            return !(left == right);
        }

        public override string ToString()
        {
            return string.Format("PubSubSubscriptionState:SubscriptionId={0},StreamId={1},Consumer={2}.",
                SubscriptionId, Stream, Consumer);
        }

        public void Fault()
        {
            state = SubscriptionStates.Faulted;
        }
    }

    [Serializable]
    [JsonObject(MemberSerialization.OptIn)]
    internal class PubSubPublisherState : IEquatable<PubSubPublisherState>
    {
        // IMPORTANT!!!!!
        // These fields have to be public non-readonly for JSonSerialization to work!
        // Implement ISerializable if changing any of them to readonly
        [JsonProperty]
        public StreamId Stream;
        [JsonProperty]
        public GrainReference producerReference; // the field needs to be of a public type, otherwise we will not generate an Orleans serializer for that class.
        // This property does not need to be Json serialized, since we already have producerReference.
        [JsonIgnore]
        public IStreamProducerExtension Producer { get { return producerReference as IStreamProducerExtension; } }

        // This constructor has to be public for JSonSerialization to work!
        // Implement ISerializable if changing it to non-public
        public PubSubPublisherState(StreamId streamId, IStreamProducerExtension streamProducer)
        {
            Stream = streamId;
            producerReference = streamProducer as GrainReference;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            // Note: Can't use the 'as' operator on PubSubPublisherState because it is a struct.
            return obj is PubSubPublisherState && Equals((PubSubPublisherState)obj);
        }
        public bool Equals(PubSubPublisherState other)
        {
            // Note: PubSubPublisherState is a struct, so 'other' can never be null.
            return Equals(other.Stream, other.Producer);
        }
        public bool Equals(StreamId streamId, IStreamProducerExtension streamProducer)
        {
            if (ReferenceEquals(null, Stream)) return false;
            if (ReferenceEquals(null, Producer)) return false;
            return Stream.Equals(streamId) && Producer.Equals(streamProducer);
        }

        public static bool operator ==(PubSubPublisherState left, PubSubPublisherState right)
        {
            return left.Equals(right);
        }
        public static bool operator !=(PubSubPublisherState left, PubSubPublisherState right)
        {
            return !left.Equals(right);
        }
        public override int GetHashCode()
        {
            // This code was auto-generated by ReSharper
            unchecked
            {
                return ((Stream != null ? Stream.GetHashCode() : 0) * 397) ^ (Producer != null ? Producer.GetHashCode() : 0);
            }
        }

        public override string ToString()
        {
            return string.Format("PubSubPublisherState:StreamId={0},Producer={1}.", Stream, Producer);
        }
    }
}
