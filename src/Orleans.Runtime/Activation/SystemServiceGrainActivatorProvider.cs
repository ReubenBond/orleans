using System;
using Orleans.Metadata;

namespace Orleans.Runtime
{
    internal class SystemServiceGrainActivatorProvider : IGrainContextActivatorProvider
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly GrainTypeSharedContextResolver _sharedComponentsResolver;
        private readonly GrainClassMap _grainClassMap;

        public SystemServiceGrainActivatorProvider(
            GrainClassMap grainClassMap,
            IServiceProvider serviceProvider,
            GrainTypeSharedContextResolver sharedComponentsResolver)
        {
            _sharedComponentsResolver = sharedComponentsResolver;
            _grainClassMap = grainClassMap;
            _serviceProvider = serviceProvider;
        }

        public bool TryGet(GrainType grainType, out IGrainContextActivator activator)
        {
            if (!_grainClassMap.TryGetGrainClass(grainType, out var serviceClass) || !typeof(ISystemService).IsAssignableFrom(serviceClass))
            {
                activator = null;
                return false;
            }

            var sharedContext = _sharedComponentsResolver.GetComponents(grainType);
            var instanceActivator = sharedContext.GetComponent<IGrainActivator>();
            if (instanceActivator is null)
            {
                throw new InvalidOperationException($"Could not find a suitable {nameof(IGrainActivator)} implementation for grain type {grainType}");
            }

            activator = new SystemServiceGrainContextActivator(
                instanceActivator,
                _serviceProvider,
                sharedContext);

            return true;
        }

        private class SystemServiceGrainContextActivator : IGrainContextActivator
        {
            private readonly IGrainActivator _grainActivator;
            private readonly IServiceProvider _serviceProvider;
            private readonly GrainTypeSharedContext _sharedComponents;

            public SystemServiceGrainContextActivator(
                IGrainActivator grainActivator,
                IServiceProvider serviceProvider,
                GrainTypeSharedContext sharedComponents)
            {
                _grainActivator = grainActivator;
                _serviceProvider = serviceProvider;
                _sharedComponents = sharedComponents;
            }

            public IGrainContext CreateContext(GrainAddress activationAddress)
            {
                var context = new SystemServiceGrainContext(
                    activationAddress,
                    _serviceProvider,
                    _sharedComponents);

                RuntimeContext.SetExecutionContext(context, out var existingContext);

                try
                {
                    // Instantiate the grain itself
                    var instance = _grainActivator.CreateInstance(context);
                    context.SetServiceInstance(instance);
                }
                finally
                {
                    RuntimeContext.SetExecutionContext(existingContext);
                }

                return context;
            }
        }
    }
}