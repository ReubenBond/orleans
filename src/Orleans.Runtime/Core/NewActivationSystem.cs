using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Runtime.Core
{
    using System;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Orleans.Runtime;

    namespace NewNewNew
    {
        public interface IComponentRoot
        {
            T GetComponent<T>() where T : class;
            void SetComponent<T>(T feature) where T : class;
        }

        public interface IMessage : IComponentRoot
        {
            GrainId Target { get; }
            GrainId Source { get; }
        }

        public interface IGrainContext
        {
            GrainId GrainId { get; }
            ValueTask ActivateAsync(CancellationToken cancellation);
            ValueTask DeactivateAsync(CancellationToken cancellation);
            void QueueMessage(IMessage message);
        }

        // Per 'GrainId'
        public class DefaultGrainContext : IGrainContext
        {
            private readonly GrainPrototype prototype;
            private readonly SpanId key;

            public DefaultGrainContext(GrainPrototype prototype, SpanId key)
            {
                this.prototype = prototype;
                this.key = key;
            }

            public GrainId GrainId => new GrainId(this.prototype.GrainType, this.key);

            public ValueTask ActivateAsync(CancellationToken cancellation)
            {
                return default;
            }

            public void QueueMessage(IMessage message)
            {
            }

            public ValueTask DeactivateAsync(CancellationToken cancellation)
            {
                return default;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private void ThrowInvalidState(string operation, string invalidState)
            {
                throw new InvalidOperationException($"Grain {this.GrainId} is in an invalid state ({invalidState}) for operation {operation}");
            }

            private enum ActivationStatus : byte
            {
                None = 0x0,
                Created = 0x1,
                Activating = 0x2,
                Valid = 0x3,
                Deactivating = 0x4,
                Invalid = 0x5
            }
        }

        // Configured per grain *type*
        // Contains immutable, shared fields such as policy, etc.
        public class GrainPrototype
        {
            public GrainPrototype(GrainType grainType, IGrainRuntime grainRuntime)
            {
                this.GrainType = grainType;
                this.Runtime = grainRuntime;
            }

            // Everything shared by a grain for given type
            public GrainType GrainType { get; }

            public IGrainRuntime Runtime { get; }
        }

        public interface IGrainContextActivator
        {
            bool CanCreate(GrainType grainType);
            public IGrainContext CreateContext(GrainId grainId);
        }

        /// <summary>
        /// The central point 
        /// </summary>
        public sealed class GrainContextActivator
        {
            private readonly object lockObj = new object();
            private readonly IGrainContextActivator[] grainContextFactories;
            private readonly DefaultGrainContextActivator defaultActivator;
            private ImmutableDictionary<GrainType, IGrainContextActivator> factories = ImmutableDictionary<GrainType, IGrainContextActivator>.Empty;

            public GrainContextActivator(DefaultGrainContextActivator defaultActivator, IEnumerable<IGrainContextActivator> grainContextFactories)
            {
                this.grainContextFactories = grainContextFactories.ToArray();
                this.defaultActivator = defaultActivator;
            }

            public IGrainContext CreateContext(GrainId grainId)
            {
                if (this.factories.TryGetValue(grainId.Type, out var factory))
                {
                    return factory.CreateContext(grainId);
                }

                return CreateSlow(grainId);

                IGrainContext CreateSlow(GrainId grainId)
                {
                    lock (this.lockObj)
                    {
                        var grainType = grainId.Type;
                        if (!this.factories.TryGetValue(grainType, out var factory))
                        {
                            foreach (var item in this.grainContextFactories)
                            {
                                if (item.CanCreate(grainType))
                                {
                                    factory = item;
                                    this.factories = this.factories.SetItem(grainType, factory);
                                    break;
                                }
                            }
                        }

                        // Fallback to the default.
                        if (factory is null)
                        {
                            factory = this.defaultActivator;
                            this.factories = this.factories.SetItem(grainType, factory);
                        }

                        return factory.CreateContext(grainId);
                    }
                }
            }
        }

        public class DefaultGrainContextActivator : IGrainContextActivator
        {
            private readonly object lockObj = new object();
            private readonly IConfigureGrainPrototype[] configurePrototypeActions;
            private readonly IConfigureGrainContext[] configureActivationActions;
            private readonly IGrainRuntime grainRuntime;
            private ImmutableDictionary<GrainType, ContextActivator> factories = ImmutableDictionary<GrainType, ContextActivator>.Empty;

            public DefaultGrainContextActivator(
                IEnumerable<IConfigureGrainContext> configureActivationActions,
                IEnumerable<IConfigureGrainPrototype> configurePrototypeActions,
                IGrainRuntime grainRuntime)
            {
                this.configurePrototypeActions = configurePrototypeActions.ToArray();
                this.configureActivationActions = configureActivationActions.ToArray();
                this.grainRuntime = grainRuntime;
            }

            public bool CanCreate(GrainType grainType) => true;

            public IGrainContext CreateContext(GrainId grainId)
            {
                if (this.factories.TryGetValue(grainId.Type, out var factory))
                {
                    return factory.CreateContext(grainId);
                }

                return CreateSlow(grainId);

                IGrainContext CreateSlow(GrainId grainId)
                {
                    lock (this.lockObj)
                    {
                        var grainType = grainId.Type;
                        if (!this.factories.TryGetValue(grainType, out var factory))
                        {
                            var prototype = this.CreatePrototype(grainType);
                            factory = new ContextActivator(prototype, this.configureActivationActions);
                            this.factories = this.factories.SetItem(grainType, factory);
                        }

                        return factory.CreateContext(grainId);
                    }
                }
            }

            private GrainPrototype CreatePrototype(GrainType grainType)
            {
                var prototype = new GrainPrototype(grainType, this.grainRuntime);

                // Configure the prototype
                foreach (var configure in this.configurePrototypeActions)
                {
                    if (!configure.CanConfigure(grainType)) continue;

                    configure.Configure(prototype);
                }

                return prototype;
            }

            private struct ContextActivator
            {
                private readonly IConfigureGrainContext[] configureActions;
                private readonly GrainPrototype prototype;

                public ContextActivator(GrainPrototype prototype, IConfigureGrainContext[] configureActions)
                {
                    this.prototype = prototype;
                    this.configureActions = configureActions.Where(c => c.CanConfigure(prototype.GrainType)).ToArray();
                }

                public DefaultGrainContext CreateContext(GrainId grainId)
                {
                    var result = new DefaultGrainContext(this.prototype, grainId.Key);
                    foreach (var configure in this.configureActions)
                    {
                        configure.Configure(result);
                    }

                    return result;
                }
            }
        }

        public interface IConfigureGrainPrototype
        {
            bool CanConfigure(GrainType grainType);
            void Configure(GrainPrototype prototype);
        }

        public interface IConfigureGrainContext
        {
            bool CanConfigure(GrainType grainType);
            void Configure(IGrainContext context);
        }
    }
}
