using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    public class TraceGrainCallFilter : IOutgoingGrainCallFilter, IIncomingGrainCallFilter
    {
        public const string ActivityIdKey = "$ctx";
        private readonly DiagnosticListener _diagnosticListener;

        public TraceGrainCallFilter()
        {
            _diagnosticListener = new DiagnosticListener("Microsoft.Orleans");
        }

        public async Task Invoke(IOutgoingGrainCallContext context)
        {
            var method = context?.InterfaceMethod;

            string activityName;
            if (method is object)
            {
                activityName = $"Call {method.DeclaringType}.{method.Name}()";
            }
            else
            {
                activityName = "SendRequest";
            }

            var activity = new Activity(activityName);
            if (GetParentActivity() is string parentId) activity.SetParentId(parentId);
            try
            {
                _diagnosticListener.StartActivity(activity, context);
                RequestContext.Set(ActivityIdKey, activity.Id);
                await context.Invoke();
            }
            finally
            {
                _diagnosticListener.StopActivity(activity, context);
            }
        }

        public async Task Invoke(IIncomingGrainCallContext context)
        {
            var method = context?.InterfaceMethod;

            string activityName;
            if (method is object)
            {
                activityName = $"Invoke {context.Grain?.GetType()}.{method.Name}()";
            }
            else
            {
                activityName = "ReceiveRequest";
            }

            var activity = new Activity(activityName);
            if (GetParentActivity() is string parentId) activity.SetParentId(parentId);
            try
            {
                _diagnosticListener.StartActivity(activity, context);
                RequestContext.Set(ActivityIdKey, activity.Id);
                await context.Invoke();
            }
            finally
            {
                _diagnosticListener.StopActivity(activity, context);
            }
        }

        internal void BeginInvoke(Message message, out Activity activity)
        {
            activity = new Activity("InvokeMessage");
            if (GetParentActivity(message) is string parentId) activity.SetParentId(parentId);
            _diagnosticListener.StartActivity(activity, message);
            RequestContext.Set(ActivityIdKey, activity.Id);
        }

        internal void EndInvoke(Message message, Activity activity)
        {
            RequestContext.Remove(ActivityIdKey);
            if (activity is object) _diagnosticListener.StopActivity(activity, message);
        }

        private static string GetParentActivity(Message message)
        {
            if (message.RequestContextData is Dictionary<string, object> requestContext)
            {
                if (requestContext.TryGetValue(ActivityIdKey, out var parentId) && parentId is string s) return s;
            }

            return GetParentActivity();
        }

        private static string GetParentActivity()
        {
            return Activity.Current?.Id ?? RequestContext.Get(ActivityIdKey) as string;
        }
    }
}
