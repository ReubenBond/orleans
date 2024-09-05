using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Invocation;

namespace Orleans.Serialization.CoreRPC
{
    [GenerateMethodSerializers(typeof(RpcProxyBase))]
    public interface IRpcService
    {
    }

    [DefaultInvokableBaseType(typeof(ValueTask<>), typeof(Request<>))]
    [DefaultInvokableBaseType(typeof(ValueTask), typeof(Request))]
    [DefaultInvokableBaseType(typeof(Task<>), typeof(TaskRequest<>))]
    [DefaultInvokableBaseType(typeof(Task), typeof(TaskRequest))]
    [DefaultInvokableBaseType(typeof(void), typeof(VoidRequest))]
    public abstract class RpcProxyBase : ClientBase
    {
        private ConcurrentDictionary<Type, Method<RequestBase, Response>> _requestMethods = new();
        private readonly IServiceProvider _serviceProvider;
        private readonly Marshaller<RequestBase> _requestMarshaller;
        private readonly Marshaller<Response> _responseMarshaller;

        protected RpcProxyBase(CallInvoker callInvoker, IServiceProvider serviceProvider) : base(callInvoker)
        {
            _serviceProvider = serviceProvider;
            (_requestMarshaller, _responseMarshaller) = GetMarshallers(serviceProvider);
            CopyContextPool = serviceProvider.GetRequiredService<CopyContextPool>();
        }

        /// <summary>
        /// Gets the serialization copy context pool.
        /// </summary>
        protected CopyContextPool CopyContextPool { get; }

        internal static (Marshaller<RequestBase>, Marshaller<Response>) GetMarshallers(IServiceProvider serviceProvider)
        {
            var requestSerializer = serviceProvider.GetRequiredService<Serializer<RequestBase>>();
            var requestMarshaller = new Marshaller<RequestBase>(
                (value, context) =>
                {
                    requestSerializer.Serialize(value, context.GetBufferWriter());
                    context.Complete();
                },
                (context) => requestSerializer.Deserialize(context.PayloadAsReadOnlySequence())); ;

            var responseSerializer = serviceProvider.GetRequiredService<Serializer<Response>>();
            var responseMarshaller = new Marshaller<Response>(
                (value, context) =>
                {
                    responseSerializer.Serialize(value, context.GetBufferWriter());
                    context.Complete();
                },
                (context) => responseSerializer.Deserialize(context.PayloadAsReadOnlySequence()));
            return (requestMarshaller, responseMarshaller);
        }

        protected TInvokable GetInvokable<TInvokable>() => ActivatorUtilities.GetServiceOrCreateInstance<TInvokable>(_serviceProvider);

        protected async ValueTask InvokeAsync(IInvokable body)
        {
            var request = (RequestBase)body;
            var method = GetMethod(request);
            var call = CallInvoker.AsyncUnaryCall(method, null, new CallOptions(), request);
            var result = await call.ResponseAsync;
            result.ThrowIfExceptionResponse();
        }

        protected async ValueTask<T> InvokeAsync<T>(IInvokable body)
        {
            var request = (RequestBase)body;
            var method = GetMethod(request);
            var call = CallInvoker.AsyncUnaryCall(method, null, new CallOptions(), request);
            var result = await call.ResponseAsync;
            return result.GetResult<T>();
        }

        private Method<RequestBase, Response> GetMethod(RequestBase request)
        {
            var requestType = request.GetType();
            if (_requestMethods.TryGetValue(requestType, out var method))
            {
                return method;
            }

            method = new Method<RequestBase, Response>(
                MethodType.Unary,
                request.GetInterfaceType().Name,
                request.GetMethod().Name,
                _requestMarshaller,
                _responseMarshaller);
            _requestMethods[requestType] = method;

            return method;
        }
    }

    [GenerateSerializer]
    public abstract class RequestBase : IInvokable
    {
        public abstract void Dispose();
        public abstract string GetActivityName();
        public abstract object GetArgument(int index);
        public abstract int GetArgumentCount();
        public virtual TimeSpan? GetDefaultResponseTimeout() => null;
        public abstract string GetInterfaceName();
        public abstract Type GetInterfaceType();
        public abstract MethodInfo GetMethod();
        public abstract string GetMethodName();
        public abstract object GetTarget();
        public abstract ValueTask<Response> Invoke();
        public abstract void SetArgument(int index, object value);
        public abstract void SetTarget(ITargetHolder holder);
    }

    [GenerateSerializer]
    public abstract class Request : RequestBase
    {
        public override ValueTask<Response> Invoke()
        {
            try
            {
                var resultTask = InvokeInner();
                if (resultTask.IsCompleted)
                {
                    resultTask.GetAwaiter().GetResult();
                    return new ValueTask<Response>(Response.Completed);
                }

                return CompleteInvokeAsync(resultTask);
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        private static async ValueTask<Response> CompleteInvokeAsync(ValueTask resultTask)
        {
            try
            {
                await resultTask;
                return Response.Completed;
            }
            catch (Exception exception)
            {
                return Response.FromException(exception);
            }
        }

        // Generated
        protected abstract ValueTask InvokeInner();
    }

    [GenerateSerializer]
    public abstract class Request<TResult> : RequestBase
    {
        public override ValueTask<Response> Invoke()
        {
            try
            {
                var resultTask = InvokeInner();
                if (resultTask.IsCompleted)
                {
                    return new ValueTask<Response>(Response.FromResult(resultTask.Result));
                }

                return CompleteInvokeAsync(resultTask);
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        private static async ValueTask<Response> CompleteInvokeAsync(ValueTask<TResult> resultTask)
        {
            try
            {
                var result = await resultTask;
                return Response.FromResult(result);
            }
            catch (Exception exception)
            {
                return Response.FromException(exception);
            }
        }

        // Generated
        protected abstract ValueTask<TResult> InvokeInner();
    }

    [GenerateSerializer]
    public abstract class TaskRequest<TResult> : RequestBase
    {
        public override ValueTask<Response> Invoke()
        {
            try
            {
                var resultTask = InvokeInner();
                var status = resultTask.Status;
                if (resultTask.IsCompleted)
                {
                    return new ValueTask<Response>(Response.FromResult(resultTask.GetAwaiter().GetResult()));
                }

                return CompleteInvokeAsync(resultTask);
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        private static async ValueTask<Response> CompleteInvokeAsync(Task<TResult> resultTask)
        {
            try
            {
                var result = await resultTask;
                return Response.FromResult(result);
            }
            catch (Exception exception)
            {
                return Response.FromException(exception);
            }
        }

        // Generated
        protected abstract Task<TResult> InvokeInner();
    }

    [GenerateSerializer]
    public abstract class TaskRequest : RequestBase
    {
        public override ValueTask<Response> Invoke()
        {
            try
            {
                var resultTask = InvokeInner();
                var status = resultTask.Status;
                if (resultTask.IsCompleted)
                {
                    resultTask.GetAwaiter().GetResult();
                    return new ValueTask<Response>(Response.Completed);
                }

                return CompleteInvokeAsync(resultTask);
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        private static async ValueTask<Response> CompleteInvokeAsync(Task resultTask)
        {
            try
            {
                await resultTask;
                return Response.Completed;
            }
            catch (Exception exception)
            {
                return Response.FromException(exception);
            }
        }

        // Generated
        protected abstract Task InvokeInner();
    }

    [GenerateSerializer]
    public abstract class VoidRequest : RequestBase
    {
        public override ValueTask<Response> Invoke()
        {
            try
            {
                InvokeInner();
                return new ValueTask<Response>(Response.Completed);
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        // Generated
        protected abstract void InvokeInner();
    }
}
