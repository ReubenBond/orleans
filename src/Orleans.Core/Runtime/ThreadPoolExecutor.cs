﻿using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Orleans.Runtime
{
    /// <summary>
    /// Essentially FixedThreadPool
    /// </summary>
    internal class ThreadPoolExecutor : IExecutor
    {
#if TRACK_DETAILED_STATS
        internal protected ThreadTrackingStatistic threadTracking;
#endif
        private readonly QueueTrackingStatistic queueTracking;

        private readonly QueueWorkItemCallback[] runningWorkItems;

        private readonly BlockingCollection<QueueWorkItemCallback> workQueue;

        private readonly ThreadPoolExecutorOptions executorOptions;

        public ThreadPoolExecutor(ThreadPoolExecutorOptions options)
        {
            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking = new QueueTrackingStatistic(options.Name);
            }

#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                threadTracking = new ThreadTrackingStatistic(Name);
            }
#endif
            workQueue = new BlockingCollection<QueueWorkItemCallback>(options.PreserveOrder ?
                (IProducerConsumerCollection<QueueWorkItemCallback>)new ConcurrentQueue<QueueWorkItemCallback>() :
                new ConcurrentBag<QueueWorkItemCallback>());

            executorOptions = options;
            executorOptions.CancellationToken.Register(() =>
            {
                // allow threads to get a chance to exit gracefully.
                workQueue.Add(QueueWorkItemCallback.NoOpQueueWorkItemCallback);
                workQueue.CompleteAdding();
            });

            // padding reduces false sharing
            const int padding = 100;
            runningWorkItems = new QueueWorkItemCallback[options.DegreeOfParallelism * padding];
            for (var createThreadCount = 0; createThreadCount < options.DegreeOfParallelism; createThreadCount++)
            {
                var executorWorkItemSlotIndex = createThreadCount * padding;
                new ThreadPerTaskExecutor(
                    new SingleThreadExecutorOptions(
                        options.Name + createThreadCount,
                        options.StageType,
                        options.CancellationToken,
                        options.Log,
                        options.FaultHandler))
                    .QueueWorkItem(_ => ProcessQueue(executorWorkItemSlotIndex));
            }
        }

        public int WorkQueueCount => workQueue.Count;

        public void QueueWorkItem(WaitCallback callback, object state = null)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            var workItem = new QueueWorkItemCallback(
                callback,
                state,
                executorOptions.WorkItemExecutionTimeTreshold,
                executorOptions.WorkItemStatusProvider);

            TrackRequestEnqueue(workItem);
            workQueue.Add(workItem);
        }

        protected void ProcessQueue(int workItemSlotIndex)
        {
            TrackExecutionStart();

            try
            {
                RunNonBatching(workItemSlotIndex);
            }
            finally
            {
                TrackExecutionStop();
            }
        }

        protected void RunNonBatching(int workItemSlotIndex)
        {
            while (!workQueue.IsCompleted &&
                (!executorOptions.CancellationToken.IsCancellationRequested || executorOptions.DrainAfterCancel))
            {
                QueueWorkItemCallback workItem;
                try
                {
                    workItem = workQueue.Take();
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                runningWorkItems[workItemSlotIndex] = workItem;
                TrackRequestDequeue(workItem);
                TrackProcessingStart();

                // todo: try .. catch from WorkerPoolThread
                workItem.ExecuteWorkItem();

                TrackProcessingStop();
                runningWorkItems[workItemSlotIndex] = null;
            }
        }

        #region StatisticsTracking

        private void TrackRequestEnqueue(QueueWorkItemCallback workItem)
        {
#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking.OnEnQueueRequest(1, WorkQueueCount, workItem);
            }
#endif
        }

        private void TrackExecutionStart()
        {

#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                queueTracking.OnStartExecution();
            }
#endif
        }

        private void TrackExecutionStop()
        {

#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    queueTracking.OnStopExecution();
                }
#endif
        }

        private void TrackRequestDequeue(QueueWorkItemCallback workItem)
        {
            // Capture the queue wait time for this task
            var waitTime = workItem.TimeSinceQueued;
            if (waitTime > executorOptions.DelayWarningThreshold && !System.Diagnostics.Debugger.IsAttached)
            {
                SchedulerStatisticsGroup.NumLongQueueWaitTimes.Increment();
                executorOptions.Log.Warn(
                    ErrorCode.SchedulerWorkerPoolThreadQueueWaitTime,
                    "Queue wait time of {0} for Item {1}", waitTime, workItem.State);
            }

#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectQueueStats)
                {
                    queueTracking.OnDeQueueRequest(workItem);
                }
#endif
        }

        private void TrackProcessingStart()
        {
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStartProcessing();
                }
#endif
        }

        private void TrackProcessingStop()
        {
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStopProcessing();
                    threadTracking.IncrementNumberOfProcessed();
                }
#endif
        }

        #endregion

        internal class QueueWorkItemCallback : ITimeInterval
        {
            public static QueueWorkItemCallback NoOpQueueWorkItemCallback = new QueueWorkItemCallback(s => { }, null, TimeSpan.MaxValue);

            private readonly WaitCallback callback;

            private readonly WorkItemStatusProvider statusProvider;

            private readonly object state;

            private readonly TimeSpan executionTimeTreshold;

            private readonly DateTime enqueueTime;

            private ITimeInterval timeInterval;

            // for lightweight execution time tracking 
            private DateTime executionStart;

            public QueueWorkItemCallback(
                WaitCallback callback,
                object state,
                TimeSpan executionTimeTreshold,
                WorkItemStatusProvider statusProvider = null)
            {
                this.callback = callback;
                this.state = state;
                this.executionTimeTreshold = executionTimeTreshold;
                this.statusProvider = statusProvider;
                this.enqueueTime = DateTime.UtcNow;
            }

            public TimeSpan Elapsed => timeInterval.Elapsed;

            internal TimeSpan TimeSinceQueued => Utils.Since(enqueueTime);

            internal object State => state;

            public void ExecuteWorkItem()
            {
                executionStart = DateTime.UtcNow;
                callback.Invoke(state);
            }

            public void Start()
            {
                timeInterval = TimeIntervalFactory.CreateTimeInterval(true);
                timeInterval.Start();
            }

            public void Stop()
            {
                timeInterval.Stop();
            }

            public void Restart()
            {
                timeInterval.Restart();
            }

            internal string GetWorkItemStatus(bool detailed)
            {
                return $"WorkItem={state} Executing for {Utils.Since(executionStart)} {statusProvider?.Invoke(state, detailed)}";
            }

            internal bool IsFrozen()
            {
                if (timeInterval != null)
                {
                    return timeInterval.Elapsed > executionTimeTreshold;
                }

                return false;
            }
        }

        public bool CheckHealth(DateTime lastCheckTime)
        {
            var healthy = true;
            foreach (var workItem in runningWorkItems)
            {
                if (workItem != null && workItem.IsFrozen())
                {
                    healthy = false;
                    executorOptions.Log.Error(ErrorCode.SchedulerTurnTooLong,
                        $"Work item {workItem.GetWorkItemStatus(true)} has been executing for long time.");
                }
            }

            return healthy;
        }
    }

    internal delegate string WorkItemStatusProvider(object state, bool detailed);
}