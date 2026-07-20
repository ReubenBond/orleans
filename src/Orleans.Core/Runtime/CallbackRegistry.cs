using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Orleans.Runtime;

internal sealed class CallbackRegistry<TValue> where TValue : class
{
    private const int SlotCount = 1_024;
    private const int SlotMask = SlotCount - 1;
    private const int StripeCount = 64;
    private const int StripeMask = StripeCount - 1;
    private readonly Func<TValue, CorrelationId> _getKey;
    private readonly TValue?[] _slots = new TValue[SlotCount];
    private readonly Stripe[] _stripes = new Stripe[StripeCount];

    public CallbackRegistry(Func<TValue, CorrelationId> getKey)
    {
        _getKey = getKey;
        for (var i = 0; i < _stripes.Length; i++)
        {
            _stripes[i] = new();
        }
    }

    public bool TryAdd(CorrelationId key, TValue value)
    {
        ref var slot = ref GetSlot(key);
        var existing = Interlocked.CompareExchange(ref slot, value, null);
        if (existing is null)
        {
            return true;
        }

        if (_getKey(existing) == key)
        {
            return false;
        }

        var stripe = GetFallbackStripe(key);
        lock (stripe)
        {
            return stripe.Values.TryAdd(key, value);
        }
    }

    public bool TryRemove(CorrelationId key, [NotNullWhen(true)] out TValue? value)
    {
        ref var slot = ref GetSlot(key);
        var existing = Volatile.Read(ref slot);
        if (existing is not null && _getKey(existing) == key)
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref slot, null, existing), existing))
            {
                value = existing;
                return true;
            }
        }

        var stripe = GetFallbackStripe(key);
        lock (stripe)
        {
            return stripe.Values.Remove(key, out value);
        }
    }

    public bool TryGetValue(CorrelationId key, [NotNullWhen(true)] out TValue? value)
    {
        value = Volatile.Read(ref GetSlot(key));
        if (value is not null && _getKey(value) == key)
        {
            return true;
        }

        var stripe = GetFallbackStripe(key);
        lock (stripe)
        {
            return stripe.Values.TryGetValue(key, out value);
        }
    }

    public List<TValue> GetValues()
    {
        var result = new List<TValue>();
        foreach (ref var slot in _slots.AsSpan())
        {
            if (Volatile.Read(ref slot) is { } value)
            {
                result.Add(value);
            }
        }

        foreach (var stripe in _stripes)
        {
            lock (stripe)
            {
                result.AddRange(stripe.Values.Values);
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref TValue? GetSlot(CorrelationId key) => ref _slots[(int)key.ToInt64() & SlotMask];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Stripe GetFallbackStripe(CorrelationId key) => _stripes[(int)key.ToInt64() & StripeMask];

    private sealed class Stripe
    {
        public Dictionary<CorrelationId, TValue> Values { get; } = new(4);
    }
}
