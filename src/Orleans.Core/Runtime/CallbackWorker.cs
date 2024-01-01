#nullable enable
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime;

internal sealed class CallbackWorker(ILogger logger) : IThreadPoolWorkItem
{
    private readonly ConcurrentQueue<WorkItem> _workItems = new();
    private readonly Dictionary<long, CallbackData> _callbacks = [];
    private readonly ILogger _logger = logger;
    private int _doingWork;

    private readonly struct WorkItem(object? state, WorkItemType type)
    {
        private readonly object? _state = state;
        public readonly WorkItemType Type = type;
        public Message ResponseMessage => Unsafe.As<Message>(_state!);
        public SiloAddress DefunctSilo => Unsafe.As<SiloAddress>(_state!);
        public CallbackData Callback => Unsafe.As<CallbackData>(_state!);
    }

    public enum WorkItemType
    {
        ReceiveResponse = 0,
        RegisterCallback = 1,
        DefunctSilo = 2,
        ExpireCallbacks = 3,
    }

    public void RegisterCallback(CallbackData callback)
    {
        if (TryBecomeWorker())
        {
            RegisterCallbackInternal(callback);
            Execute();
        }
        else
        {
            _workItems.Enqueue(new (callback, WorkItemType.RegisterCallback));
        }
    }

    public void ReceiveResponse(Message message)
    {
        if (TryBecomeWorker())
        {
            // Ensure that the request is processed requests before the response.
            ProcessWorkItems();
            ReceiveResponseInternal(message);
            Execute();
        }
        else
        {
            _workItems.Enqueue(new(message, WorkItemType.ReceiveResponse));
            Schedule();
        }
    }

    public void BreakOutstandingMessagesToDeadSilo(SiloAddress defunctSilo)
    {
        _workItems.Enqueue(new (defunctSilo, WorkItemType.DefunctSilo));
        Schedule();
    }

    public void CheckForExpiredCallbacks()
    {
        _workItems.Enqueue(new(null, WorkItemType.ExpireCallbacks));
        Schedule();
    }

    private void RegisterCallbackInternal(CallbackData callback)
    {
        var message = callback.Message;
        _callbacks.TryAdd(message.Id.Value, callback);
    }

    private void BreakOutstandingMessagesToDeadSiloInternal(SiloAddress siloAddress)
    {
        List<long> removed = [];
        foreach (var kvp in _callbacks)
        {
            if (siloAddress.Equals(kvp.Value.Message.TargetSilo))
            {
                kvp.Value.OnTargetSiloFail();
                removed.Add(kvp.Key);
            }
        }

        foreach (var key in removed)
        {
            _callbacks.Remove(key);
        }
    }

    private void Schedule()
    {
        // Set working if it wasn't (via atomic Interlocked).
        if (TryBecomeWorker())
        {
            // Wasn't working, schedule work.
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
        }
    }

    private bool TryBecomeWorker() => Interlocked.CompareExchange(ref _doingWork, 1, 0) == 0;

    private void ReceiveResponseInternal(Message message)
    {
        if (message.Result == Message.ResponseTypes.Status)
        {
            var status = (StatusResponse)message.BodyObject;
            _callbacks.TryGetValue(message.Id.Value, out var callback);
            if (callback is not null && callback.Message is { } request)
            {
                callback.OnStatusUpdate(status);
                if (status.Diagnostics != null && status.Diagnostics.Count > 0 && _logger.IsEnabled(LogLevel.Information))
                {
                    var diagnosticsString = string.Join("\n", status.Diagnostics);
                    _logger.LogInformation("Received status update for pending request, Request: {RequestMessage}. Status: {Diagnostics}", request, diagnosticsString);
                }
            }
            else
            {
                if (status.Diagnostics != null && status.Diagnostics.Count > 0 && _logger.IsEnabled(LogLevel.Information))
                {
                    var diagnosticsString = string.Join("\n", status.Diagnostics);
                    _logger.LogInformation("Received status update for unknown request. Message: {StatusMessage}. Status: {Diagnostics}", message, diagnosticsString);
                }
            }

            return;
        }

        _callbacks.Remove(message.Id.Value, out var callbackData);
        if (callbackData is not null)
        {
            // IMPORTANT: we do not schedule the response callback via the scheduler, since the only thing it does
            // is to resolve/break the resolver. The continuations/waits that are based on this resolution will be scheduled as work items.
            callbackData.DoCallback(message);
        }
        else
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug((int)ErrorCode.Dispatcher_NoCallbackForResp, "No callback for response message {Message}", message);
            }
        }
    }

    private void CheckForExpiredCallbacksInternal()
    {
        var currentStopwatchTicks = ValueStopwatch.GetTimestamp();
        List<long>? removed = null; 

        foreach (var (key, callback) in _callbacks)
        {
            if (callback.IsCompleted) continue;
            if (callback.IsExpired(currentStopwatchTicks))
            {
                callback.OnTimeout();
                removed ??= [];
                removed.Add(key);
            }
        }

        if (removed is not null)
        {
            foreach (var key in removed)
            {
                _callbacks.Remove(key);
            }
        }
    }

    public void Execute()
    {
        do
        {
            ProcessWorkItems();

            // Has work & wasn't already scheduled so continue loop.
        } while (ShouldProcessWorkItems());
    }

    private bool ShouldProcessWorkItems()
    {
        // All work done.

        // Set _doingWork (0 == false) prior to checking IsEmpty to catch any missed work in interim.
        // This doesn't need to be volatile due to the following barrier (i.e. it is volatile).
        _doingWork = 0;

        // Ensure _doingWork is written before IsEmpty is read.
        // As they are two different memory locations, we insert a barrier to guarantee ordering.
        Thread.MemoryBarrier();

        // Check if there is work to do
        if (_workItems.IsEmpty || Interlocked.Exchange(ref _doingWork, 1) == 1)
        {
            // Nothing to do, exit.
            return false;
        }

        return true;
    }

    private void ProcessWorkItems()
    {
        while (_workItems.TryDequeue(out var workItem))
        {
            switch (workItem.Type)
            {
                case WorkItemType.RegisterCallback:
                    RegisterCallbackInternal(workItem.Callback);
                    break;
                case WorkItemType.ReceiveResponse:
                    ReceiveResponseInternal(workItem.ResponseMessage);
                    break;
                case WorkItemType.DefunctSilo:
                    BreakOutstandingMessagesToDeadSiloInternal(workItem.DefunctSilo);
                    break;
                case WorkItemType.ExpireCallbacks:
                    CheckForExpiredCallbacksInternal();
                    break;
            }
        }
    }
}
