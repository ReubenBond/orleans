using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    [EventSource(Name = "Microsoft-Orleans-Runtime")]
    internal class OrleansRuntimeEvents : EventSource
    {
        public static readonly OrleansRuntimeEvents Log = new OrleansRuntimeEvents();

        private OrleansRuntimeEvents() { }

        [Event(1, Level = EventLevel.Informational)]
        public void InvokeRequestStart(Guid relatedActivityId) => this.WriteEventWithRelatedActivityId(1, relatedActivityId);

        [Event(2, Level = EventLevel.Informational)]
        public void InvokeRequestStop() => this.WriteEvent(2);

        [Event(3, Level = EventLevel.Informational)]
        public void IssueRequestStart() => this.WriteEvent(3);

        [Event(4, Level = EventLevel.Informational)]
        public void IssueRequestStop() => this.WriteEvent(4);
    }

    public static class RequestContextExtensions
    {
        public static void Import(Dictionary<string, object> contextData)
        {
            if (RequestContext.PropagateActivityId)
            {
                object activityIdObj = Guid.Empty;
                if (contextData?.TryGetValue(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER, out activityIdObj) == true)
                {
                    var activityId = (Guid)activityIdObj;
                    Trace.CorrelationManager.ActivityId = activityId;
                }
                else
                {
                    Trace.CorrelationManager.ActivityId = Guid.Empty;
                }
            }

            if (contextData != null && contextData.Count > 0)
            {
                var values = contextData.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                // We have some data, so store RC data into the async local field.
                RequestContext.CallContextData.Value = values;
            }
            else
            {
                // Clear any previous RC data from the async local field.
                // MUST CLEAR the LLC, so that previous request LLC does not leak into this one.
                RequestContext.Clear();
            }
        }

        public static Dictionary<string, object> Export(SerializationManager serializationManager)
        {
            var values = RequestContext.CallContextData.Value;

            if (RequestContext.PropagateActivityId)
            {
                var activityIdOverride = Trace.CorrelationManager.ActivityId;
                if (activityIdOverride != Guid.Empty)
                {
                    object existingActivityId;
                    if (values == null
                        || !values.TryGetValue(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER, out existingActivityId)
                        || activityIdOverride != (Guid)existingActivityId)
                    {
                        // Create new copy before mutating data
                        values = values == null ? new Dictionary<string, object>() : new Dictionary<string, object>(values);
                        values[RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER] = activityIdOverride;
                    }
                }
            }

            return (values != null && values.Count > 0)
                ? (Dictionary<string, object>)serializationManager.DeepCopy(values)
                : null;
        }
    }
}
