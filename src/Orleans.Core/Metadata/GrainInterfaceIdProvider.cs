using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Utilities;

namespace Orleans.Metadata
{
    /// <summary>
    /// Associates a <see cref="GrainInterfaceId"/> with a <see cref="Type" />.
    /// </summary>
    public class GrainInterfaceIdProvider
    {
        private readonly IGrainInterfaceIdProvider[] providers;

        public GrainInterfaceIdProvider(IEnumerable<IGrainInterfaceIdProvider> providers)
        {
            this.providers = providers.ToArray();
        }

        /// <summary>
        /// Returns the <see cref="GrainInterfaceId"/> for the provided interface.
        /// </summary>
        /// <param name="type">The grain interface.</param>
        /// <returns>The <see cref="GrainInterfaceId"/> for the provided interface.</returns>
        public GrainInterfaceId GetGrainInterfaceId(Type type)
        {
            if (!type.IsInterface)
            {
                throw new ArgumentException($"Argument {nameof(type)} must be an interface. Provided value, \"{type}\", is not an interface.", nameof(type));
            }

            // Configured providers take precedence
            foreach (var provider in this.providers)
            {
                if (provider.GetGrainInterfaceId(type) is GrainInterfaceId interfaceId)
                {
                    return interfaceId;
                }
            }

            // Conventions are used as a fallback
            return GetGrainInterfaceIdByConvention(type);
        }

        public static GrainInterfaceId GetGrainInterfaceIdByConvention(Type type) => GrainInterfaceId.Create(RuntimeTypeNameFormatter.Format(type));
    }
}
