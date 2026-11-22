using System.Collections;

namespace Orleans.Testing.Simulation.Runtime.Racing;

/// <summary>
/// The most recent race scheduling points, oldest first. Points before the newest <see cref="Capacity"/> are dropped,
/// so a long run retains a bounded trace; replaying the run's seed with a larger capacity recovers them.
/// </summary>
internal sealed class RaceSchedulingTrace : IReadOnlyList<RaceSchedulingPoint>
{
    private RaceSchedulingPoint[] _points = [];
    private int _start;
    private int _count;
    private int _capacity;

    public RaceSchedulingTrace(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    /// <summary>Gets or sets the most points retained. Lowering it drops the oldest points beyond it.</summary>
    public int Capacity
    {
        get => _capacity;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            RaceSchedulingPoint[] retained = [.. this.Skip(Math.Max(0, _count - value))];
            _capacity = value;
            _points = retained;
            _start = 0;
            _count = retained.Length;
        }
    }

    public int Count => _count;

    public RaceSchedulingPoint this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
            return _points[(_start + index) % _points.Length];
        }
    }

    public void Add(RaceSchedulingPoint point)
    {
        if (_count < _capacity)
        {
            // Grow toward the capacity as points arrive, so a short run allocates only what it uses.
            if (_count == _points.Length)
            {
                var grown = new RaceSchedulingPoint[Math.Min(_capacity, Math.Max(16, 2 * _points.Length))];
                for (int index = 0; index < _count; index++)
                {
                    grown[index] = this[index];
                }

                _points = grown;
                _start = 0;
            }

            _points[(_start + _count) % _points.Length] = point;
            _count++;
            return;
        }

        _points[_start] = point;
        _start = (_start + 1) % _points.Length;
    }

    public IEnumerator<RaceSchedulingPoint> GetEnumerator()
    {
        for (int index = 0; index < _count; index++)
        {
            yield return this[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
