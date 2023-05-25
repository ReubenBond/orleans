
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

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
        /// <summary>
        /// Registers a grain migration participant.
        /// </summary>
        /// <param name="participant">The participant.</param>
        void AddMigrationParticipant(IGrainMigrationParticipant participant);

        /// <summary>
        /// Unregisters a grain migration participant.
        /// </summary>
        /// <param name="participant">The participant.</param>
        void RemoveMigrationParticipant(IGrainMigrationParticipant participant);
    }

    public interface IGrainMigrationParticipant
    {
        /// <summary>
        /// Called on the original activation when migration is initiated, before <see cref="IGrainBase.OnDeactivateAsync(DeactivationReason, CancellationToken)"/>.
        /// The participant can access and update the dehydration context.
        /// </summary>
        /// <param name="dehydrationContext"></param>
        void OnDehydrate(IDehydrationContext dehydrationContext);

        /// <summary>
        /// Called on the new activation after a migration, before <see cref="IGrainBase.OnActivateAsync(CancellationToken)"/>.
        /// The participant can restore state from the migration context.
        /// </summary>
        /// <param name="rehydrationContext">The rehydration context.</param>
        void OnRehydrate(IRehydrationContext rehydrationContext);

        /// <summary>
        /// Called to get the cost of migrating this activation.
        /// </summary>
        /// <returns>The cost of migrating this activation.</returns>
        MigrationCost GetMigrationCost() => MigrationCost.Medium;
    }

    /// <summary>
    /// Records the state of a grain activation for migration.
    /// </summary>
    public interface IDehydrationContext
    {
        /// <summary>
        /// Gets the keys in the context.
        /// </summary>
        IEnumerable<string> Keys { get; }

        /// <summary>
        /// Adds a value to the dehydration context, associated with the provided key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        void Add(string key, ReadOnlySpan<byte> value);

        /// <summary>
        /// Adds a value to the dehydration context, associated with the provided key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="valueWriter">A delegate used to write the provided value to the context.</param>
        /// <param name="value">The value to provide to <paramref name="valueWriter"/>.</param>
        void Add(string key, Action<object, IBufferWriter<byte>> valueWriter, object value);
    }

    /// <summary>
    /// Contains the state of a grain activation for migration.
    /// </summary>
    public interface IRehydrationContext
    {
        /// <summary>
        /// Gets the keys in the context.
        /// </summary>
        IEnumerable<string> Keys { get; }

        /// <summary>
        /// Tries to get a value from the rehydration context, associated with the provided key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value, if present.</param>
        /// <returns><see langword="true"/> if the key exists in the context, otherwise <see langword="false"/>.</returns>
        bool TryGetValue(string key, out ReadOnlySequence<byte> value);
    }

    /// <summary>
    /// Describes the cost of migrating a grain.
    /// </summary>
    public readonly struct MigrationCost
    {
        private MigrationCost(int value)
        {
            Value = value;
        }

        internal int Value { get; }

        /// <summary>
        /// Gets a value describing a very low migration cost.
        /// </summary>
        public static MigrationCost VeryLow => new (0);

        /// <summary>
        /// Gets a value describing a low migration cost.
        /// </summary>
        public static MigrationCost Low => new (250);

        /// <summary>
        /// Gets a value describing a medium migration cost.
        /// </summary>
        public static MigrationCost Medium => new (500);

        /// <summary>
        /// Gets a value describing a medium migration cost.
        /// </summary>
        public static MigrationCost Default => Medium;

        /// <summary>
        /// Gets a value describing a high migration cost.
        /// </summary>
        public static MigrationCost High => new (750);

        /// <summary>
        /// Gets a value describing a very high migration cost.
        /// </summary>
        public static MigrationCost VeryHigh => new (1000);

        /// <summary>
        /// Gets a value indicating that this instance cannot be moved.
        /// </summary>
        public static MigrationCost Immovable => new (int.MaxValue);

        /// <summary>
        /// Returns the maximum of the provided values.
        /// </summary>
        /// <param name="left">The first value.</param>
        /// <param name="right">The second value.</param>
        /// <returns>The maximum of the provided value.</returns>
        internal static MigrationCost Max(MigrationCost left, MigrationCost right) => new(Math.Max(left.Value, right.Value));
    }
}
