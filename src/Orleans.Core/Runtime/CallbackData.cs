using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    internal class CallbackData
    {
        private readonly SharedCallbackData shared;
        private readonly IResponseCompletionSource context;
        private int completed;
        private StatusResponse lastKnownStatus;
        private ValueStopwatch stopwatch;

        public CallbackData(
            SharedCallbackData shared,
            IResponseCompletionSource ctx, 
            Message msg)
        {
            this.shared = shared;
            this.context = ctx;
            this.Message = msg;
            this.stopwatch = ValueStopwatch.StartNew();
            if (!Message.TryPreserve())
            {
                ThrowMessageTryPreserveFatalError();

                [DoesNotReturn]
                [MethodImpl(MethodImplOptions.NoInlining)]
                static void ThrowMessageTryPreserveFatalError() => throw new InvalidOperationException($"{nameof(Message)}.{nameof(Message.TryPreserve)} returned false in the {nameof(CallbackData)} constructor. This is a fatal error.");
            }
        }

        public Message Message { get; } // might hold metadata used by response pipeline

        public bool IsCompleted => this.completed == 1;

        public void OnStatusUpdate(StatusResponse status)
        {
            this.lastKnownStatus = status;
        }
        
        public bool IsExpired(long currentTimestamp)
        {
            var duration = currentTimestamp - this.stopwatch.GetRawTimestamp();
            return duration > shared.ResponseTimeoutStopwatchTicks;
        }

        public void OnTimeout(TimeSpan timeout)
        {
            if (Interlocked.CompareExchange(ref completed, 1, 0) != 0)
            {
                return;
            }

            this.shared.Unregister(this.Message);

            var requestStatistics = this.shared.RequestStatistics;
            if (requestStatistics.CollectApplicationRequestsStats)
            {
                this.stopwatch.Stop();
                requestStatistics.OnAppRequestsEnd(this.stopwatch.Elapsed);
                requestStatistics.OnAppRequestsTimedOut();
            }

            OrleansCallBackDataEvent.Log.OnTimeout(this.Message);

            var msg = this.Message; // Local working copy

            string messageHistory = msg.GetTargetHistory();
            var statusMessage = lastKnownStatus is StatusResponse status ? $"Last known status is {status}. " : string.Empty;
            string errorMsg = $"Response did not arrive on time in {timeout} for message: {msg}. {statusMessage}Target History is: {messageHistory}.";
            this.shared.Logger.Warn(ErrorCode.Runtime_Error_100157, "{0} About to break its promise.", errorMsg);

            var error = Message.CreatePromptExceptionResponse(msg, new TimeoutException(errorMsg));
            ResponseCallback(error, this.context);
        }

        public void OnTargetSiloFail()
        {
            if (Interlocked.CompareExchange(ref this.completed, 1, 0) != 0)
            {
                return;
            }

            this.shared.Unregister(this.Message);
            var requestStatistics = this.shared.RequestStatistics;
            if (requestStatistics.CollectApplicationRequestsStats)
            {
                this.stopwatch.Stop();
                requestStatistics.OnAppRequestsEnd(this.stopwatch.Elapsed);
            }

            OrleansCallBackDataEvent.Log.OnTargetSiloFail(this.Message);
            var msg = this.Message;
            var messageHistory = msg.GetTargetHistory();
            var statusMessage = lastKnownStatus is StatusResponse status ? $"Last known status is {status}. " : string.Empty;
            var errorMsg =
                $"The target silo became unavailable for message: {msg}. {statusMessage}Target History is: {messageHistory}. See {Constants.TroubleshootingHelpLink} for troubleshooting help.";
            this.shared.Logger.Warn(ErrorCode.Runtime_Error_100157, "{0} About to break its promise.", errorMsg);
            var error = Message.CreatePromptExceptionResponse(msg, new SiloUnavailableException(errorMsg));
            ResponseCallback(error, this.context);
        }

        public void DoCallback(Message response)
        {
            if (Interlocked.CompareExchange(ref this.completed, 1, 0) != 0)
            {
                response.Release();
                return;
            }

            OrleansCallBackDataEvent.Log.DoCallback(this.Message);

            var requestStatistics = this.shared.RequestStatistics;
            if (requestStatistics.CollectApplicationRequestsStats)
            {
                this.stopwatch.Stop();
                requestStatistics.OnAppRequestsEnd(this.stopwatch.Elapsed);
            }

            // do callback outside the CallbackData lock. Just not a good practice to hold a lock for this unrelated operation.
            ResponseCallback(response, this.context);
        }

        public void ResponseCallback(Message response, IResponseCompletionSource context)
        {
            try
            {
                if (response.Result != Message.ResponseTypes.Rejection)
                {
                    context.Complete((Response)response.BodyObject);
                }
                else
                {
                    context.Complete(GetRejectionResponse(response));
                }
            }
            catch (Exception exception)
            {
                context.Complete(Response.FromException(exception));
            }
            finally
            {
                response.Release();
                Message.Release();
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            static Response GetRejectionResponse(Message response)
            {
                var rejection = response.RejectionType switch
                {
                    Message.RejectionTypes.GatewayTooBusy => new GatewayTooBusyException(),
                    _ => response.BodyObject as Exception ?? new OrleansMessageRejectionException(response.RejectionInfo ?? "Unable to send request - no rejection info available"),
                };

                return Response.FromException(rejection);
            }
        }
    }
}
