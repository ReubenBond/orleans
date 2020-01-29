using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    /// <summary>
    /// Associates a <see cref="GrainType"/> with a <see cref="Type" />.
    /// </summary>
    public class GrainTypeProvider
    {
        private readonly IGrainTypeProvider[] providers;

        public GrainTypeProvider(IEnumerable<IGrainTypeProvider> providers)
        {
            this.providers = providers.ToArray();
        }

        /// <summary>
        /// Returns the grain type for the provided class.
        /// </summary>
        /// <param name="type">The grain class.</param>
        /// <returns>The grain type for the provided class.</returns>
        public GrainType GetGrainType(Type type)
        {
            if (!type.IsClass)
            {
                throw new ArgumentException($"Argument {nameof(type)} must be a class. Provided value, \"{type}\", is not a class.", nameof(type));
            }

            // Configured providers take precedence
            foreach (var provider in this.providers)
            {
                if (provider.GetGrainType(type) is GrainType grainType)
                {
                    return grainType;
                }
            }

            // Conventions are used as a fallback
            return GetGrainTypeByConvention(type);
        }

        public static GrainType GetGrainTypeByConvention(Type type)
        {
            var name = type.Name.ToLowerInvariant();

            // Trim generic arity
            var index = name.IndexOf('`');
            if (index > 0)
            {
                name = name.Substring(0, index);
            }

            // Trim "Grain" suffix
            index = name.LastIndexOf("grain");
            if (index > 0)
            {
                name = name.Substring(0, index);
            }

            // Append the generic arity, eg typeof(MyListGrain<T>) would eventually become mylist`1
            if (type.IsGenericType)
            {
                name = name + '`' + type.GetGenericArguments().Length;
            }

            return GrainType.Create(name);
        }
    }

    public interface IGrainTypeProvider
    {
        /// <summary>
        /// Returns the grain type corresponding to the class identified by <paramref name="type"/> or <see langword="null" /> if
        /// this provider does not apply to the provided type.
        /// </summary>
        GrainType? GetGrainType(Type type);
    }

    public class AttributeGrainTypeProvider : IGrainTypeProvider
    {
        private readonly IServiceProvider serviceProvider;

        public AttributeGrainTypeProvider(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        public GrainType? GetGrainType(Type grainClass)
        {
            foreach (var attr in grainClass.GetCustomAttributes(inherit: true))
            {
                if (attr is IGrainTypeProviderAttribute typeProviderAttribute)
                {
                    return typeProviderAttribute.GetGrainType(this.serviceProvider, grainClass);
                }
            }

            return default;
        }
    }

    /// <summary>
    /// An <see cref="Attribute"/> which implements this specifies the <see cref="GrainType"/> of the
    /// type which it is attached to.
    /// </summary>
    public interface IGrainTypeProviderAttribute
    {
        GrainType GetGrainType(IServiceProvider services, Type type);
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public abstract class GrainTypeProviderAttribute : Attribute, IGrainTypeProviderAttribute
    {
        public abstract GrainType GetGrainType(IServiceProvider services, Type type);
    }

    /// <summary>
    /// Specifies the grain type of the type which it is attached to.
    /// </summary>
    public sealed class GrainTypeAttribute : GrainTypeProviderAttribute
    {
        private readonly GrainType grainType;

        public GrainTypeAttribute(string grainType)
        {
            this.grainType = GrainType.Create(grainType);
        }

        public override GrainType GetGrainType(IServiceProvider services, Type type) => this.grainType;
    }
}
