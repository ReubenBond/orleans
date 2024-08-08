using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Utilities;

internal static class SearchAlgorithms
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BinarySearch<TState>(int length, TState state, Func<int, TState, int> comparer)
    {
        var left = 0;
        var right = length - 1;

        while (left <= right)
        {
            var mid = left + (right - left) / 2;
            var comparison = comparer(mid, state);

            if (comparison == 0)
            {
                return mid;
            }
            else if (comparison < 0)
            {
                left = mid + 1;
            }
            else
            {
                right = mid - 1;
            }
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RingRangeBinarySearch<TCollection, TElement, TKey>(int length, TCollection collection, Func<TCollection, int, TElement> getEntry, TKey key) where TElement : IComparable<TKey>
    {
        if (length == 0) return -1;

        var left = 0;
        var right = length - 1;

        while (left <= right)
        {
            var mid = left + (right - left) / 2;
            var comparison = getEntry(collection, mid).CompareTo(key);

            if (comparison == 0)
            {
                return mid;
            }
            else if (comparison < 0)
            {
                // Go right.
                left = mid + 1;
            }
            else
            {
                // Go left.
                right = mid - 1;
            }
        }

        // Try the last element.
        if (getEntry(collection, length - 1).CompareTo(key) == 0)
        {
            return length - 1;
        }

#if DEBUG
        // Try the first element.
        if (getEntry(collection, 0).CompareTo(key) == 0)
        {
            Debug.Fail("Sort order invariant violated.");
        }
#endif

        return -1;
    }
}
