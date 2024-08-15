#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime.GrainDirectory;
partial class DistributedGrainDirectory
{
    private sealed class BatchWorker : IAsyncDisposable
    {
        private const int BatchedLookupLimit = 1_000;
        private const int BatchedRegistrationLimit = 1_000;
        private const int BatchedDeregistrationLimit = 1_000;

        private readonly DistributedGrainDirectory _directory;
        private readonly SingleWaiterAutoResetEvent _workSignal = new();
        private readonly ConcurrentQueue<(TaskCompletionSource<GrainAddress?> Completion, GrainId GrainId)> _lookupQueue = new();
        private readonly ConcurrentQueue<(TaskCompletionSource<GrainAddress?> Completion, GrainAddress NewAddress, GrainAddress? ExistingAddress)> _registrationQueue = new();
        private readonly ConcurrentQueue<(TaskCompletionSource<bool> Completion, GrainAddress Address)> _deregistrationQueue = new();
        private readonly CancellationTokenSource _cts = new();
#pragma warning disable IDE0052 // Remove unread private members
        private readonly Task _runTask;
#pragma warning restore IDE0052 // Remove unread private members
        private long _latestVersion = MembershipVersion.MinValue.Value;

        public BatchWorker(DistributedGrainDirectory directory)
        {
            ArgumentNullException.ThrowIfNull(directory);
            _directory = directory;
            _runTask = _directory.RunOrQueueTask(ProcessPendingBatches);
        }

        public Task<GrainAddress?> LookupAsync(GrainId grainId)
        {
            var tcs = new TaskCompletionSource<GrainAddress?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _cts.Token.ThrowIfCancellationRequested();
            _lookupQueue.Enqueue((tcs, grainId));
            _workSignal.Signal();
            return tcs.Task;
        }

        public Task<GrainAddress?> RegisterAsync(GrainAddress newAddress, GrainAddress? existingAddress)
        {
            ArgumentNullException.ThrowIfNull(newAddress);
            _cts.Token.ThrowIfCancellationRequested();
            var tcs = new TaskCompletionSource<GrainAddress?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _registrationQueue.Enqueue((tcs, newAddress, existingAddress));
            _workSignal.Signal();
            return tcs.Task;
        }

        public Task<bool> DeregisterAsync(GrainAddress address)
        {
            ArgumentNullException.ThrowIfNull(address);
            _cts.Token.ThrowIfCancellationRequested();
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _deregistrationQueue.Enqueue((tcs, address));
            _workSignal.Signal();
            return tcs.Task;
        }

        private async Task ProcessPendingBatches()
        {
            await _workSignal.WaitAsync();
            var localReplica = _directory._localReplica;
            var batches = new Dictionary<SiloAddress, WorkBatch>();
            var tasks = new List<Task>();
            var stopwatch = ValueStopwatch.StartNew();
            while (true)
            {
                var smallBatch = true;
                var view = localReplica.View;

                if (_latestVersion > view.Version.Value)
                {
                    view = await localReplica.RefreshViewAsync(new(_latestVersion), _cts.Token);
                }

                if (view.Members.Length == 0)
                {
                    _latestVersion = view.Version.Value + 1;
                    continue;
                }

                while (_lookupQueue.TryDequeue(out var lookupRequest))
                {
                    var batch = GetBatchForGrain(view, lookupRequest.GrainId);
                    batch.LookupCompletions.Add(lookupRequest.Completion);
                    (batch.Request.Lookups ??= []).Add(lookupRequest.GrainId);

                    if (batch.Request.Lookups.Count >= BatchedLookupLimit)
                    {
                        smallBatch = false;
                        break;
                    }
                }

                while (_registrationQueue.TryDequeue(out var registrationRequest))
                {
                    var batch = GetBatchForGrain(view, registrationRequest.NewAddress.GrainId);
                    batch.RegistrationCompletions.Add(registrationRequest.Completion);
                    (batch.Request.Registrations ??= []).Add((registrationRequest.NewAddress, registrationRequest.ExistingAddress));

                    if (batch.Request.Registrations.Count >= BatchedRegistrationLimit)
                    {
                        smallBatch = false;
                        break;
                    }
                }

                while (_deregistrationQueue.TryDequeue(out var deregistrationRequest))
                {
                    var batch = GetBatchForGrain(view, deregistrationRequest.Address.GrainId);
                    batch.DeregistrationCompletions.Add(deregistrationRequest.Completion);
                    (batch.Request.Deregistrations ??= []).Add(deregistrationRequest.Address);

                    if (batch.Request.Deregistrations.Count >= BatchedDeregistrationLimit)
                    {
                        smallBatch = false;
                        break;
                    }
                }

                // Remove completed tasks.
                tasks.RemoveAll(t => t.IsCompleted);

                if (batches.Count == 0)
                {
                    if (_cts.IsCancellationRequested)
                    {
                        _directory._logger.LogDebug("Exiting batch worker processing loop");
                        break;
                    }

                    // Wait for work.
                    await _workSignal.WaitAsync();
                }
                else
                {
                    // Submit the batch.
                    foreach (var (owner, batch) in batches)
                    {
                        tasks.Add(ProcessBatchAsync(owner, batch));
                    }

                    batches.Clear();
                }

                var elapsed = stopwatch.Elapsed.TotalMilliseconds;
                if (smallBatch && elapsed < 50)
                {
                    await Task.Delay(50 - (int)elapsed);
                }

                stopwatch.Restart();
            }

            await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            WorkBatch GetBatchForGrain(DirectoryMembershipSnapshot view, GrainId grainId)
            {
                Debug.Assert(view.Members.Length > 0); 
                if (view.TryGetOwner(grainId, out var owner))
                {
                    ref var valueRef = ref CollectionsMarshal.GetValueRefOrAddDefault(batches, owner, out _);
                    valueRef ??= new(view.Version);
                    return valueRef;
                }

                throw new InvalidOperationException("No owner found for grain ID.");
            }
        }

        private async Task ProcessBatchAsync(SiloAddress owner, WorkBatch batch)
        {
            var replica = _directory._localReplica.GetReplica(owner);
            List<(TaskCompletionSource<GrainAddress?> Completion, GrainId GrainId)>? lookupRetries = null;
            List<(TaskCompletionSource<GrainAddress?> Completion, GrainAddress NewAddress, GrainAddress? ExistingAddress)>? registrationRetries = null;
            List<(TaskCompletionSource<bool> Completion, GrainAddress Address)>? deregistrationRetries = null;
            var request = batch.Request;
            try
            {
                _directory._logger.LogDebug("Submitting batch with '{LookupCount}' lookups, '{RegistrationCount}' registrations, and '{DeregistrationCount}' deregistrations.", request.Lookups?.Count ?? 0, request.Registrations?.Count ?? 0, request.Deregistrations?.Count ?? 0);

                var response = await replica.ApplyBulk(request);
                Debug.Assert(response.Lookups is null == request.Lookups is null);
                Debug.Assert(response.Registrations is null == request.Registrations is null);
                Debug.Assert(response.Deregistrations is null == request.Deregistrations is null);
                if (response.Lookups is not null)
                {
                    var result = response.Lookups;
                    Debug.Assert(request.Lookups is not null);
                    Debug.Assert(result.Count == batch.LookupCompletions.Count);
                    Debug.Assert(request.Lookups.Count == batch.LookupCompletions.Count);
                    for (var i = 0; i < result.Count; i++)
                    {
                        if (result[i] is null or { SiloAddress: not null })
                        {
                            batch.LookupCompletions[i].SetResult(result[i]);
                        }
                        else
                        {
                            (lookupRetries ??= []).Add((batch.LookupCompletions[i], request.Lookups[i]));
                        }
                    }
                }

                if (response.Registrations is not null)
                {
                    var result = response.Registrations;
                    Debug.Assert(request.Registrations is not null);
                    Debug.Assert(result.Count == batch.RegistrationCompletions.Count);
                    Debug.Assert(request.Registrations.Count == batch.RegistrationCompletions.Count);
                    for (var i = 0; i < result.Count; i++)
                    {
                        if (result[i] is not { } registrationResult)
                        {
                            var (newAddress, existingAddress) = request.Registrations[i];
                            (registrationRetries ??= []).Add((batch.RegistrationCompletions[i], newAddress, existingAddress));
                        }
                        else
                        {
                            batch.RegistrationCompletions[i].SetResult(registrationResult);
                        }
                    }
                }

                if (response.Deregistrations is not null)
                {
                    var result = response.Deregistrations;
                    Debug.Assert(request.Deregistrations is not null);
                    Debug.Assert(result.Count == batch.DeregistrationCompletions.Count);
                    Debug.Assert(request.Deregistrations.Count == batch.DeregistrationCompletions.Count);
                    for (var i = 0; i < result.Count; i++)
                    {
                        if (result[i] is not { } deregistrationResult)
                        {
                            (deregistrationRetries ??= []).Add((batch.DeregistrationCompletions[i], request.Deregistrations[i]));
                        }
                        else
                        {
                            batch.DeregistrationCompletions[i].SetResult(deregistrationResult);
                        }
                    }
                }

                // Refresh the view before resubmitting misdirected requests.
                if (lookupRetries is not null || registrationRetries is not null || deregistrationRetries is not null)
                {
                    await _directory._localReplica.RefreshViewAsync(response.Version, CancellationToken.None);

                    if (lookupRetries is not null)
                    {
                        foreach (var entry in lookupRetries)
                        {
                            _lookupQueue.Enqueue(entry);
                        }
                    }

                    if (registrationRetries is not null)
                    {
                        foreach (var entry in registrationRetries)
                        {
                            _registrationQueue.Enqueue(entry);
                        }
                    }

                    if (deregistrationRetries is not null)
                    {
                        foreach (var entry in deregistrationRetries)
                        {
                            _deregistrationQueue.Enqueue(entry);
                        }
                    }

                    _workSignal.Signal();
                }
            }
            catch (Exception exception)
            {
                _directory._logger.LogError(exception, "Error processing bulk request.");
                if (exception is OrleansMessageRejectionException)
                {
                    // Retry the entire batch.
                    if (request.Lookups is { } lookups)
                    {
                        for (var i = 0; i < lookups.Count; i++)
                        {
                            _lookupQueue.Enqueue((batch.LookupCompletions[i], lookups[i]));
                        }
                    }

                    if (request.Registrations is { } registrations)
                    {
                        for (var i = 0; i < registrations.Count; i++)
                        {
                            var (newAddress, existingAddress) = registrations[i];
                            _registrationQueue.Enqueue((batch.RegistrationCompletions[i], newAddress, existingAddress));
                        }
                    }

                    if (request.Deregistrations is { } deregistrations)
                    {
                        for (var i = 0; i < deregistrations.Count; i++)
                        {
                            _deregistrationQueue.Enqueue((batch.DeregistrationCompletions[i], deregistrations[i]));
                        }
                    }

                    _workSignal.Signal();
                }
                else
                {
                    foreach (var completion in batch.LookupCompletions)
                    {
                        completion.TrySetException(exception);
                    }

                    foreach (var completion in batch.RegistrationCompletions)
                    {
                        completion.TrySetException(exception);
                    }

                    foreach (var completion in batch.DeregistrationCompletions)
                    {
                        completion.TrySetException(exception);
                    }
                }
            }
        }

        async public Task StopAsync(CancellationToken cancellationToken)
        {
            await _cts.CancelAsync();
            _workSignal.Signal();
            if (_runTask is Task t)
            {
                await t.WaitAsync(cancellationToken);
            }
        }

        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        private sealed class WorkBatch(MembershipVersion version)
        {
            public BulkDirectoryRequest Request { get; } = new() { Version = version };
            public List<TaskCompletionSource<GrainAddress?>> LookupCompletions { get; } = [];
            public List<TaskCompletionSource<GrainAddress?>> RegistrationCompletions { get; } = [];
            public List<TaskCompletionSource<bool>> DeregistrationCompletions { get; } = [];
        }
    }
}
