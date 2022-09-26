using Orleans.Serialization.Cloning;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Surrogate converter for <see cref="ImmutableDictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterConverter]
    public sealed class ImmutableDictionarySurrogateConverter<TKey, TValue> : IConverter<ImmutableDictionary<TKey, TValue>, ImmutableDictionarySurrogate<TKey, TValue>>
    {
        /// <inheritdoc/>
        public ImmutableDictionary<TKey, TValue> ConvertFromSurrogate(in ImmutableDictionarySurrogate<TKey, TValue> surrogate) => surrogate.Values switch
        {
            null => default,
            object => ImmutableDictionary.CreateRange(surrogate.Values)
        };

        /// <inheritdoc/>
        public ImmutableDictionarySurrogate<TKey, TValue> ConvertToSurrogate(in ImmutableDictionary<TKey, TValue> value) => value switch
        {
            null => default,
            _ => new ImmutableDictionarySurrogate<TKey, TValue>
            {
                Values = new Dictionary<TKey, TValue>(value)
            },
        };
    }

    /// <summary>
    /// Surrogate type used by <see cref="ImmutableDictionarySurrogateConverter{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [GenerateSerializer]
    public struct ImmutableDictionarySurrogate<TKey, TValue>
    {
        /// <summary>
        /// Gets or sets the values.
        /// </summary>
        /// <value>The values.</value>
        [Id(1)]
        public Dictionary<TKey, TValue> Values { get; set; }
    }

    /// <summary>
    /// Copier for <see cref="ImmutableDictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterCopier]
    public sealed class ImmutableDictionaryCopier<TKey, TValue> : IDeepCopier<ImmutableDictionary<TKey, TValue>>
    {
        /// <inheritdoc/>
        public ImmutableDictionary<TKey, TValue> DeepCopy(ImmutableDictionary<TKey, TValue> input, CopyContext _) => input;
    }
}
