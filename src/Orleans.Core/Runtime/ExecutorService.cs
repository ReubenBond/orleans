﻿using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Messaging;

namespace Orleans.Runtime
{
    internal interface IExecutor : IHealthCheckable
    {
        void QueueWorkItem(WaitCallback callback, object state = null);

        int WorkQueueCount { get; }
    }

    internal interface IStageAttribute { }

    internal interface IQueueDrainable : IStageAttribute { }

    internal class ExecutorService
    {
        public IExecutor GetExecutor(ExecutorOptions executorOptions)
        {
            switch (executorOptions)
            {
                case ThreadPoolExecutorOptions options:
                    return new ThreadPoolExecutor(options);
                case SingleThreadExecutorOptions options:
                    return new ThreadPerTaskExecutor(options);
                default:
                    throw new NotImplementedException();
            }
        }
    }
    
    internal abstract class ExecutorOptions
    {
        protected ExecutorOptions(string stageName,
            ILogger log)
        {
            StageName = stageName;
            Log = log;
        }

        public string StageName { get; }

        public ILogger Log { get; }
    }

    internal class ThreadPoolExecutorOptions : ExecutorOptions
    {
        public ThreadPoolExecutorOptions(
            Type stageType,
            string stageName,
            CancellationToken ct,
            ILogger log,
            int degreeOfParallelism = 1,
            bool drainAfterCancel = false,
            TimeSpan? workItemExecutionTimeTreshold = null,
            TimeSpan? delayWarningThreshold = null,
            WorkItemStatusProvider workItemStatusProvider = null)
            : base(stageName, log)
        {
            StageType = stageType;
            CancellationToken = ct;
            DegreeOfParallelism = degreeOfParallelism;
            DrainAfterCancel = drainAfterCancel;
            WorkItemExecutionTimeTreshold = workItemExecutionTimeTreshold ?? TimeSpan.MaxValue;
            DelayWarningThreshold = delayWarningThreshold ?? TimeSpan.MaxValue;
            WorkItemStatusProvider = workItemStatusProvider;
        }

        public Type StageType { get; }
        
        public CancellationToken CancellationToken { get; }

        public int DegreeOfParallelism { get; }

        public bool DrainAfterCancel { get; }

        public TimeSpan WorkItemExecutionTimeTreshold { get; }

        public TimeSpan DelayWarningThreshold { get; }

        public WorkItemStatusProvider WorkItemStatusProvider { get; }
    }

    internal class SingleThreadExecutorOptions : ExecutorOptions
    {
        public SingleThreadExecutorOptions(string stageName, ILogger log) : base(stageName, log)
        {
        }
    }
}