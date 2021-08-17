using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Grpc.AspNetCore.Server.Model;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Grpc.Net.ClientFactory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.CoreRPC;
using Orleans.Serialization.Invocation;

namespace Orleans.Serialization.Grpc
{
    public static class CoreRpcHostingExtensions
    {
        public static IServiceCollection AddCoreRpc(this IServiceCollection services)
        {
            services.AddSerializer();
            services.AddGrpc();
            services.AddSingleton<CoreRpcCallInvokerFactory>();
            services.AddSingleton<CoreRpcClientFactory, DefaultCoreRpcClientFactory>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IServiceMethodProvider<>), typeof(CoreRpcServiceMethodProvider<>)));
            services.AddHttpClient();
            return services;
        }

        public static IServiceCollection AddStandaloneCoreRpc(this IServiceCollection services)
        {
            services.AddSerializer();
            services.AddSingleton<CoreRpcCallInvokerFactory>();
            services.AddSingleton<CoreRpcClientFactory, DefaultCoreRpcClientFactory>();
            services.AddHttpClient();
            return services;
        }
    }

    /// <summary>
    /// A factory abstraction for a component that can create CoreRPC client instances with custom
    /// configuration for a given logical name.
    /// </summary>
    public abstract class CoreRpcClientFactory
    {
        /// <summary>
        /// Create a CoreRPC client instance for the specified <typeparamref name="TClient"/> and configuration name.
        /// </summary>
        /// <typeparam name="TClient">The CoreRPC client type.</typeparam>
        /// <param name="name">The configuration name.</param>
        /// <returns>A CoreRPC client instance.</returns>
        public abstract TClient CreateClient<TClient>(string name) where TClient : class;

        public abstract TClient CreateClient<TClient>(CallInvoker callInvoker) where TClient : class;
    }

    internal class CoreRpcCallInvokerFactory
    {
        private readonly ILoggerFactory _loggerFactory;

        public CoreRpcCallInvokerFactory(ILoggerFactory loggerFactory)
        {
            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            _loggerFactory = loggerFactory;
        }

        public CallInvoker CreateCallInvoker(HttpMessageHandler httpHandler, string name, Type type, GrpcClientFactoryOptions clientFactoryOptions)
        {
            if (httpHandler == null)
            {
                throw new ArgumentNullException(nameof(httpHandler));
            }

            var channelOptions = new GrpcChannelOptions();
            channelOptions.HttpHandler = httpHandler;
            channelOptions.LoggerFactory = _loggerFactory;

            if (clientFactoryOptions.ChannelOptionsActions.Count > 0)
            {
                foreach (var applyOptions in clientFactoryOptions.ChannelOptionsActions)
                {
                    applyOptions(channelOptions);
                }
            }

            var address = clientFactoryOptions.Address;
            if (address == null)
            {
                throw new InvalidOperationException($@"Could not resolve the address for gRPC client '{name}'. Set an address when registering the client: services.AddCoreRpcClient<{type.Name}>(o => o.Address = new Uri(""https://localhost:5001""))");
            }

            var channel = GrpcChannel.ForAddress(address, channelOptions);

            var httpClientCallInvoker = channel.CreateCallInvoker();

            var resolvedCallInvoker = clientFactoryOptions.Interceptors.Count == 0
                ? httpClientCallInvoker
                : httpClientCallInvoker.Intercept(clientFactoryOptions.Interceptors.ToArray());

            return resolvedCallInvoker;
        }
    }

    internal class DefaultCoreRpcClientFactory : CoreRpcClientFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly CoreRpcCallInvokerFactory _callInvokerFactory;
        private readonly IOptionsMonitor<GrpcClientFactoryOptions> _grpcClientFactoryOptionsMonitor;
        private readonly IOptionsMonitor<HttpClientFactoryOptions> _httpClientFactoryOptionsMonitor;
        private readonly IHttpMessageHandlerFactory _messageHandlerFactory;
        private readonly HashSet<Type> _knownProxyTypes;
        private readonly ConcurrentDictionary<Type, Type> _proxyMap = new();

        public DefaultCoreRpcClientFactory(
            IServiceProvider serviceProvider,
            CoreRpcCallInvokerFactory callInvokerFactory,
            IOptionsMonitor<GrpcClientFactoryOptions> grpcClientFactoryOptionsMonitor,
            IOptionsMonitor<HttpClientFactoryOptions> httpClientFactoryOptionsMonitor,
            IHttpMessageHandlerFactory messageHandlerFactory,
            IOptions<TypeManifestOptions> typeManifestOptions)
        {
            _serviceProvider = serviceProvider;
            _callInvokerFactory = callInvokerFactory;
            _grpcClientFactoryOptionsMonitor = grpcClientFactoryOptionsMonitor;
            _httpClientFactoryOptionsMonitor = httpClientFactoryOptionsMonitor;
            _messageHandlerFactory = messageHandlerFactory;
            _knownProxyTypes = new HashSet<Type>(typeManifestOptions.Value.InterfaceProxies);
        }

        private Type GetProxyType(Type interfaceType)
        {
            if (interfaceType.IsGenericType)
            {
                var unbound = interfaceType.GetGenericTypeDefinition();
                var parameters = interfaceType.GetGenericArguments();
                foreach (var proxyType in _knownProxyTypes)
                {
                    if (!proxyType.IsGenericType)
                    {
                        continue;
                    }

                    var matching = proxyType.FindInterfaces(
                            (type, criteria) =>
                                type.IsGenericType && type.GetGenericTypeDefinition() == (Type)criteria,
                            unbound)
                        .FirstOrDefault();
                    if (matching != null)
                    {
                        return proxyType.GetGenericTypeDefinition().MakeGenericType(parameters);
                    }
                }
            }

            return _knownProxyTypes.First(interfaceType.IsAssignableFrom);
        }

        private TInterface CreateClientInternal<TInterface>(CallInvoker callInvoker)
        {
            if (!_proxyMap.TryGetValue(typeof(TInterface), out var proxyType))
            {
                proxyType = _proxyMap[typeof(TInterface)] = GetProxyType(typeof(TInterface));
            }

            return (TInterface)ActivatorUtilities.CreateInstance(_serviceProvider, proxyType, new[] { callInvoker });
        }

        public override TClient CreateClient<TClient>(string name) where TClient : class
        {
            var httpClientFactoryOptions = _httpClientFactoryOptionsMonitor.Get(name);
            if (httpClientFactoryOptions.HttpClientActions.Count > 0)
            {
                throw new InvalidOperationException($"The ConfigureHttpClient method is not supported when creating CoreRPC clients. Unable to create client with name '{name}'.");
            }

            var clientFactoryOptions = _grpcClientFactoryOptionsMonitor.Get(name);
            var httpHandler = _messageHandlerFactory.CreateHandler(name);
            var callInvoker = _callInvokerFactory.CreateCallInvoker(httpHandler, name, typeof(TClient), clientFactoryOptions);

            if (clientFactoryOptions.Creator != null)
            {
                var c = clientFactoryOptions.Creator(callInvoker);
                if (c is TClient client)
                {
                    return client;
                }
                else if (c == null)
                {
                    throw new InvalidOperationException("A null instance was returned by the configured client creator.");
                }

                throw new InvalidOperationException($"The {c.GetType().FullName} instance returned by the configured client creator is not compatible with {typeof(TClient).FullName}.");
            }
            else
            {
                return CreateClientInternal<TClient>(callInvoker);
            }
        }

        public override TClient CreateClient<TClient>(CallInvoker callInvoker) where TClient : class => CreateClientInternal<TClient>(callInvoker);
    }

    internal class CoreRpcServiceMethodProvider<TService> : IServiceMethodProvider<TService> where TService : class
    {
        private readonly Marshaller<RequestBase> _requestMarshaller;
        private readonly Marshaller<Response> _responseMarshaller;

        public CoreRpcServiceMethodProvider(IServiceProvider serviceProvider)
        {
            (_requestMarshaller, _responseMarshaller) = RpcProxyBase.GetMarshallers(serviceProvider);
        }

        public void OnServiceMethodDiscovery(ServiceMethodProviderContext<TService> context)
        {
            if (!typeof(IRpcService).IsAssignableFrom(typeof(TService)))
            {
                return;
            }

            foreach (var iface in typeof(TService).GetInterfaces())
            {
                if (iface == typeof(IRpcService) || !typeof(IRpcService).IsAssignableFrom(iface))
                {
                    // Skip irrelevant interfaces.
                    continue;
                }

                foreach (var method in iface.GetMethods())
                {
                    // TODO: Extract meaningful service name and method name values.
                    var methodDescriptor = new Method<RequestBase, Response>(MethodType.Unary, iface.Name, method.Name, _requestMarshaller, _responseMarshaller);
                    var handler = new UnaryServerMethod<TService, RequestBase, Response>((service, request, context) =>
                    {
                        request.SetTarget(new TargetHolder(service));

                        // If there were interceptors/filters/etc, they would go here.
                        // request.SetArgument(i, value);
                        // request.GetArgument<string>(i)

                        return request.Invoke().AsTask();
                    });

                    context.AddUnaryMethod(methodDescriptor, Array.Empty<object>(), handler);
                }
            }
        }
    }

    public static class ServiceDefinitionCollectionExtensions
    {
        public static ServerServiceDefinition.Builder AddService<TService>(this ServerServiceDefinition.Builder builder, IServiceProvider serviceProvider)
        {
            if (!typeof(IRpcService).IsAssignableFrom(typeof(TService)))
            {
                throw new InvalidOperationException($"Service of type {typeof(TService)} is not a CoreRPC service");
            }

            var instance = ActivatorUtilities.GetServiceOrCreateInstance<TService>(serviceProvider);
            var (requestMarshaller, responseMarshaller) = RpcProxyBase.GetMarshallers(serviceProvider);
            foreach (var iface in typeof(TService).GetInterfaces())
            {
                if (iface == typeof(IRpcService) || !typeof(IRpcService).IsAssignableFrom(iface))
                {
                    // Skip irrelevant interfaces.
                    continue;
                }

                foreach (var method in iface.GetMethods())
                {
                    // TODO: Extract meaningful service name and method name values.
                    var methodDescriptor = new Method<RequestBase, Response>(MethodType.Unary, iface.Name, method.Name, requestMarshaller, responseMarshaller);
                    var handler = new UnaryServerMethod<RequestBase, Response>((request, context) =>
                    {
                        request.SetTarget(new TargetHolder(instance));

                        // If there were interceptors/filters/etc, they would go here.
                        // request.SetArgument(i, value);
                        // request.GetArgument<string>(i)

                        return request.Invoke().AsTask();
                    });

                    builder.AddMethod(methodDescriptor, handler);
                }
            }

            return builder;
        }
    }

    internal readonly struct TargetHolder : ITargetHolder
    {
        public readonly object _service;
        public TargetHolder(object service) => _service = service;

        public TComponent GetComponent<TComponent>() => (TComponent)_service;
        public TTarget GetTarget<TTarget>() => (TTarget)_service;
    }
}
