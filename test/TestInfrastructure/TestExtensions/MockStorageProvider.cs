using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using System.Collections.Concurrent;

namespace UnitTests.StorageTests
{
    public static class SiloBuilderExtensions
    {
        public static ISiloBuilder AddTestStorageProvider<T>(this ISiloBuilder builder, string name) where T : IGrainStorage
        {
            return builder.AddTestStorageProvider(name, (sp, n) => ActivatorUtilities.CreateInstance<T>(sp));
        }

        public static ISiloBuilder AddTestStorageProvider<T>(this ISiloBuilder builder, string name, Func<IServiceProvider, string, T> createInstance) where T : IGrainStorage
        {
            return builder.ConfigureServices(services =>
            {
                services.AddSingletonNamedService<IGrainStorage>(name, (sp, n) => createInstance(sp, n));

                if (typeof(ILifecycleParticipant<ISiloLifecycle>).IsAssignableFrom(typeof(T)))
                {
                    services.AddSingletonNamedService(name, (svc, n) => (ILifecycleParticipant<ISiloLifecycle>)svc.GetRequiredServiceByName<IGrainStorage>(name));
                }

                if (typeof(IControllable).IsAssignableFrom(typeof(T)))
                {
                    services.AddSingletonNamedService(name, (svc, n) => (IControllable)svc.GetRequiredServiceByName<IGrainStorage>(name));
                }
            });
        }
    }

    [DebuggerDisplay("MockStorageProvider:{Name}")]
    public class MockStorageProvider : IControllable, IGrainStorage
    {
        public enum Commands
        {
            InitCount,
            SetValue,
            GetProvideState,
            SetErrorInjection,
            GetLastState,
            ResetHistory
        }

        [Serializable]
        [GenerateSerializer]
        public class StateForTest 
        {
            [Id(0)]
            public int InitCount { get; set; }
            [Id(1)]
            public int CloseCount { get; set; }
            [Id(2)]
            public int ReadCount { get; set; }
            [Id(3)]
            public int WriteCount { get; set; }
            [Id(4)]
            public int DeleteCount { get; set; }
        }

        private static int _instanceNum;
        private readonly int _id;

        private int initCount, closeCount, readCount, writeCount, deleteCount;

        private readonly int numKeys;
        private readonly DeepCopier copier;
        private readonly Dictionary<string, (object State, string ETag)> _store = new();
        private const string stateStoreKey = "State";
        private ILogger logger;
        public string LastId { get; private set; }
        public object LastState { get; private set; }

        public string Name { get; private set; }

        public MockStorageProvider(ILoggerFactory loggerFactory, DeepCopier copier)
            : this(Guid.NewGuid().ToString(), 2, loggerFactory, copier)
        { }

        public MockStorageProvider(string name, ILoggerFactory loggerFactory, DeepCopier copier)
            : this(name, 2, loggerFactory, copier)
        { }

        public MockStorageProvider(string name, int numKeys, ILoggerFactory loggerFactory, DeepCopier copier)
        {
            _id = ++_instanceNum;
            this.numKeys = numKeys;
            this.copier = copier;
            this.Name = name;
            this.logger = loggerFactory.CreateLogger(string.Format("Storage.{0}-{1}", this.GetType().Name, this._id));

            logger.Info(0, "Init Name={0}", name);
            Interlocked.Increment(ref initCount);

            logger.Info(0, "Finished Init Name={0}", name);
        }

        public StateForTest GetProviderState()
        {
            var state = new StateForTest();
            state.InitCount = initCount;
            state.CloseCount = closeCount;
            state.DeleteCount = deleteCount;
            state.ReadCount = readCount;
            state.WriteCount = writeCount;
            return state;
        }

        [Serializable]
        [GenerateSerializer]
        public class SetValueArgs
        {
            [Id(0)]
            public Type StateType { get; set; }
            [Id(1)]
            public string GrainType { get; set; }
            [Id(2)]
            public GrainId GrainId { get; set; }
            [Id(3)]
            public string Name { get; set; }
            [Id(4)]
            public object Val { get; set; }

        }

        public void SetValue(SetValueArgs args)
        {
            SetValue(args.StateType, args.GrainType, args.GrainId, args.Name, args.Val);
        }

        private void SetValue(Type stateType, string grainType, GrainId grainId, string name, object val)
        {
            lock (_store)
            {
                this.logger.Info("Setting stored value field {0} for {1} to {2}", name, grainId, val);
                var key = GetStorageKey(grainType, grainId);
                _store.TryGetValue(key, out var existing);
                var stateValue = existing.State ?? Activator.CreateInstance(stateType);
                _store[key] = (State: stateValue, ETag: Guid.NewGuid().ToString());

                var field = stateValue.GetType().GetProperty(name).GetSetMethod(true);
                field.Invoke(stateValue, new[] { stateValue });
                LastId = GetId(grainId);
                LastState = stateValue;
            }
        }

        public object GetLastState()
        {
            return LastState;
        }

        public T GetLastState<T>()
        {
            return (T) LastState;
        }

        private object GetLastState<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            lock (_store)
            {
                var key = GetStorageKey(stateName, grainId);
                if (!_store.TryGetValue(key, out var storedState))
                {
                    storedState = _store[key] = (Activator.CreateInstance(grainState.State.GetType()), null);
                }

                LastId = GetId(grainId);
                LastState = storedState.State;
                return storedState;
            }
        }

        public virtual Task Close()
        {
            logger.Info(0, "Close");
            Interlocked.Increment(ref closeCount);
            _store.Clear();
            return Task.CompletedTask;
        }

        public virtual Task ReadStateAsync<T>(GrainId grainId, string stateName, IGrainState<T> grainState)
        {
            logger.Info(0, "ReadStateAsync for {0} {1}", stateName, grainId);
            Interlocked.Increment(ref readCount);
            lock (_store)
            {
                var storedState = GetLastState(stateName, grainId, grainState);
                grainState.RecordExists = storedState != null;
                grainState.State = (T)this.copier.Copy(storedState); // Read current state data
            }

            return Task.CompletedTask;
        }

        public virtual Task WriteStateAsync<T>(GrainId grainId, string stateName, IGrainState<T> grainState)
        {
            logger.Info(0, "WriteStateAsync for {0} {1}", stateName, grainId);
            Interlocked.Increment(ref writeCount);
            lock (_store)
            {
                var newValue = this.copier.Copy(grainState.State); // Store current state data
                var key = GetStorageKey(stateName, grainId);
                if (!_store.TryGetValue(key, out var storedState) || string.IsNullOrWhiteSpace(storedState.ETag) || string.Equals(storedState.ETag, grainState.ETag, StringComparison.OrdinalIgnoreCase))
                {
                    storedState = _store[key] = (newValue, Guid.NewGuid().ToString());
                    grainState.ETag = storedState.ETag;
                }
                else
                {
                    // Do we throw inconsistent state exception?
                }

                LastId = GetId(grainId);
                LastState = storedState;
                grainState.RecordExists = true;
            }
            return Task.CompletedTask;
        }

        public virtual Task ClearStateAsync<T>(GrainId grainId, string stateName, IGrainState<T> grainState)
        {
            logger.Info(0, "ClearStateAsync for {0} {1}", stateName, grainId);
            Interlocked.Increment(ref deleteCount);
            lock (_store)
            {
                var key = GetStorageKey(stateName, grainId);
                if (_store.TryGetValue(key, out var storedState))
                {
                    if (string.IsNullOrWhiteSpace(storedState.ETag) || string.Equals(storedState.ETag, grainState.ETag, StringComparison.OrdinalIgnoreCase))
                    {
                        _store.Remove(key);
                    }
                    else
                    {
                        // Throw inconsistent state exception?
                    }
                }

                LastId = GetId(grainId);
                LastState = null;
            }
            grainState.RecordExists = false;
            return Task.CompletedTask;
        }

        private static string GetId(GrainId grainId)
        {
            return grainId.ToString();
        }

        private static string GetStorageKey(string stateName, GrainId grainId)
        {
            return $"{grainId}_{stateName}";
        }

        public void ResetHistory()
        {
            // initCount = 0;
            closeCount = readCount = writeCount = deleteCount = 0;
            LastId = null;
            LastState = null;
        }

        /// <summary>
        /// A function to execute a control command.
        /// </summary>
        /// <param name="command">A serial number of the command.</param>
        /// <param name="arg">An opaque command argument</param>
        public virtual Task<object> ExecuteCommand(int command, object arg)
        {
            switch ((Commands)command)
            {
                case Commands.InitCount:
                    return Task.FromResult<object>(initCount);
                case Commands.SetValue:
                    SetValue((SetValueArgs) arg);
                    return Task.FromResult<object>(true); 
                case Commands.GetProvideState:
                    return Task.FromResult<object>(GetProviderState());
                case Commands.GetLastState:
                    return Task.FromResult(GetLastState());
                case Commands.ResetHistory:
                    ResetHistory();
                    return Task.FromResult<object>(true);
                default:
                    return Task.FromResult<object>(true); 
            }
        }
    }
}
