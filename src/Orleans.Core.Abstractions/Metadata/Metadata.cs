using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    /// <summary>
    /// Information about a logical grain type <see cref="GrainType"/>.
    /// </summary>
    [Serializable]
    public class GrainMetadata
    {
        public GrainMetadata(ImmutableDictionary<string, string> values)
        {
            this.Values = values;
        }

        // 'unordered' -> Calls to this grain have no assumption about ordering (currently this is used on client side for
        // routing [StatelessWorker] grain calls in a round-robin fashion instead of routing them all to one gateway.
        //
        // 'placement' -> indicates which placement policy is used for this grain. Placement directors can access this metadata
        // in order to access individual parameters (eg, # of stateless workers, some hash key or storage info?)
        //
        // 'multicluster-registration' -> delete? Used to indicate which multicluster registration strategy is used.
        //
        // 'directory' -> indicates directory policy.
        //
        // 'legacygrainidtype' -> 'guid', 'string', 'guid+string', etc. This grain implements one of the legacy grain id types (IGrainWith*Key) and
        // therefore a LegacyGrainId should be used with it. This is important for loading state from storage, for example.
        // Does this need to be here, though? Either the interface is present locally or it isn't.
        public ImmutableDictionary<string, string> Values { get; }

        public string ToDetailedString()
        {
            if (this.Values is null) return string.Empty;
            var result = new StringBuilder("[");
            bool first = true;
            foreach (var entry in this.Values)
            {
                if (!first)
                {
                    result.Append(", ");
                }

                result.Append($"\"{entry.Key}\": \"{entry.Value}\"");
                first = false;
            }
            result.Append("]");

            return result.ToString();
        }
    }

    /// <summary>
    /// Information about a communication interface
    /// </summary>
    [Serializable]
    public class cGrainInterfaceMetadata
    {
        // 'version' -> populated by the [Version(x)] attribute, which will implement IGrainInterfaceMetadataProviderAttribute
        // and therefore automatically populate the value during startup.
        //
        // 'primary-implementation' -> encoded GrainType of primary implementation, if it exists
        public ImmutableDictionary<string, string> Values { get; }
    }

    public interface IGrainInterfaceMetadataProviderAttribute
    {
        void Populate(IServiceProvider services, Type type, Dictionary<string, string> properties);
    }

    /// <summary>
    /// Specifies the default grain type to use when this interface appears without a grain type.
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface, AllowMultiple = false)]
    public sealed class DefaultGrainTypeAttribute : Attribute, IGrainInterfaceMetadataProviderAttribute
    {
        private readonly GrainType grainType;

        public DefaultGrainTypeAttribute(string grainType)
        {
            this.grainType = GrainType.Create(grainType);
        }

        public void Populate(IServiceProvider services, Type type, Dictionary<string, string> properties)
        {
            properties[WellKnownGrainInterfaceProperties.DefaultGrainType] = this.grainType.ToString();
        }
    }

    // An [Attribute] can implement this and it will be called upon when populating metadata about the grain it is attached to.
    // Attributes are instantiated by the runtime, so a different interface may be useful for them instead.
    public interface IGrainMetadataProviderAttribute
    {
        // How does this map to a GrainType?
        // Should there be some separation between GrainType and a 'behavior' class?
        // * Eg, consider grain extensions, or multiple classes per 'type' by some other means. Do they also need this kind of data?
        // * If so, how?
        void Populate(IServiceProvider services, Type type, Dictionary<string, string> properties);
    }

    // For grain interfaces (communication interfaces)
    public interface IGrainInterfaceMetadataProvider
    {
        // Q: Type or GrainInterfaceId? Type is probably more useful. It will not be available on silos without that type.
        // This is only supposed to run locally
        void Populate(Type type, Dictionary<string, string> properties);
    }

    public interface IGrainMetadataProvider
    {
        void Populate(GrainType type, Dictionary<string, string> properties);
    }
}