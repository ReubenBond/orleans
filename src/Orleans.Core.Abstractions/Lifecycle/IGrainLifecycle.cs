
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    /// <summary>
    /// The observable grain lifecycle.
    /// </summary>
    /// <remarks>
    /// This type is usually used as the generic parameter in <see cref="ILifecycleParticipant{IGrainLifecycle}"/> as
    /// a means of participating in the lifecycle stages of a grain activation.
    /// </remarks>
    public interface IGrainLifecycle : ILifecycleObservable
    {
        void AddMigrationParticipant(IGrainMigrationParticipant participant);
        void RemoveMigrationParticipant(IGrainMigrationParticipant participant);
    }

    public interface IGrainMigrationParticipant
    {
        // Called on the original activation when migration is initiated, before OnDeactivateAsync.
        // The participant can access and update the migration context dictionary.
        // Methods are void, since we do not want components to perform any IO before
        // the grain is activated.
        void OnDehydrate(IDehydrationContext migrationContext);

        // Called on the new activation after a migration, before OnActivateAsync.
        // The participant can restore state from the migration context dictionary.
        // Method is void since we only want to set in-memory context here and do 
        void OnRehydrate(IRehydrationContext migrationContext);

        // Future work:
        // Perhaps this should return an `int` or a `float` to express the migration
        // cost. The interface can be updated later to add this, with a default return
        // value indicating no/minor cost.
        MigrationCost GetMigrationCost();
    }

    public interface IDehydrationContext
    {
        void Add(string key, ReadOnlySpan<byte> value);
        void Add(string key, Action<object, IBufferWriter<byte>> valueWriter, object value);
    }

    public interface IRehydrationContext
    {
        bool TryGetValue(string key, out ReadOnlySequence<byte> value);
    }

    public readonly struct MigrationCost
    {
        private MigrationCost(int value)
        {
            Value = value;
        }

        internal int Value { get; }

        public static MigrationCost VeryLow => new (0);

        public static MigrationCost Low => new (250);

        public static MigrationCost Medium => new (500);

        public static MigrationCost High => new (750);

        public static MigrationCost VeryHigh => new (1000);

        public static MigrationCost Immovable => new (int.MaxValue);

        public static MigrationCost Max(MigrationCost left, MigrationCost right) => new(Math.Max(left.Value, right.Value));
    }
}
