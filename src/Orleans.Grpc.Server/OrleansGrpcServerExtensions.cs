using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Grpc.AspNetCore.Server.Model;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.gRPC;
using Orleans.Serialization.Grpc.Internal;
using Orleans.Serialization.Invocation;

namespace Microsoft.Extensions.Hosting;

public static class OrleansGrpcClientExtension
{
}

public static class OrleansGrpcServerExtensions
{
    public static ISiloBuilder AddGrpcGrains(this ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.AddSingleton<IConfigureGrainTypeComponents, GrpcGrainTypeConfigurator>();
        siloBuilder.Services.AddSingleton<IGrainPropertiesProvider, GrpcGrainPropertiesPopulator>();
        siloBuilder.Services.AddSerializer(s => s.AddProtobufSerializer());
        return siloBuilder;
    }

    public static IEndpointRouteBuilder MapGrpcGrains(this IEndpointRouteBuilder endpoints)
    {
        // Note that this will not work in dynamic scenarios or when the service is not locally known at runtime.
        var grainManifest = endpoints.ServiceProvider.GetRequiredService<IClusterManifestProvider>().LocalGrainManifest;
        var servicesMap = new Dictionary<string, Dictionary<string, MethodType>>();
        foreach (var (grainType, properties) in grainManifest.Grains)
        {
            foreach (var (key, value) in properties.Properties)
            {
                if (!key.StartsWith(GrpcWellKnownGrainProperties.GrpcMethodPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var fullName = key[GrpcWellKnownGrainProperties.GrpcMethodPrefix.Length..].Split('/');
                if (fullName.Length != 2)
                {
                    throw new InvalidOperationException($"Invalid gRPC method property '{key}' on grain '{grainType}': key must contain a single service name and method name separated by a '/'.");
                }

                var serviceName = fullName[0];
                var methodName = fullName[1];

                if (!Enum.TryParse<MethodType>(value, ignoreCase: true, out var methodType))
                {
                    throw new InvalidOperationException($"Invalid gRPC method property '{key}' on grain '{grainType}': value '{value}' is not a valid method type.");
                }

                ref var serviceMap = ref CollectionsMarshal.GetValueRefOrAddDefault(servicesMap, serviceName, out _);
                serviceMap ??= [];
                serviceMap[methodName] = methodType;
            }
        }

        foreach (var (serviceName, methods) in servicesMap)
        {
            foreach (var (methodName, methodType) in methods)
            {
                var pattern = RoutePatternFactory.Parse($"/{serviceName}/{methodName}");
                /*
                var requestDelegate = methodType switch
                {
                    MethodType.Unary => new RequestDelegate(context => HandleUnaryCall(context, serviceName, methodName)),
                };
                endpoints.Map(pattern);
                */
                throw new NotImplementedException();
            }
        }

        return endpoints;
    }
}

internal sealed class GrpcServiceGrainCallInvoker(Dictionary<string, GrpcMethodModel> methods)
{
    private readonly FrozenDictionary<string, GrpcMethodModel> _methods = methods.ToFrozenDictionary();

    public bool TryGetMethod(GrpcGrainUnaryCall call, [NotNullWhen(true)] out IMethod? method)
    {
        if (_methods.TryGetValue(call.MethodName!, out var model))
        {
            method = model.Method;
            return true;
        }

        method = null;
        return false;
    }

    public ValueTask<Response> Invoke(object serviceInstance, GrpcGrainUnaryCall call)
    {
        if (!_methods.TryGetValue(call.MethodName!, out var method))
        {
            throw new InvalidOperationException($"Method '{call.MethodName}' not found.");
        }

        return method.Invoker(serviceInstance, call);
    }
}

internal delegate ValueTask<Response> GrpcMethodInvoker(object serviceInstance, GrpcGrainUnaryCall call);

internal sealed record class GrpcMethodModel(IMethod Method, GrpcMethodInvoker Invoker);

public class OrleansServerCallContext(DateTime deadline, CancellationToken cancellationToken) : ServerCallContext
{
    protected override string MethodCore { get; } = "Method";
    protected override string HostCore { get; } = "host";
    protected override string PeerCore { get; } = "peer";
    protected override DateTime DeadlineCore { get; } = deadline;
    protected override Metadata RequestHeadersCore { get; } = Metadata.Empty;
    protected override CancellationToken CancellationTokenCore { get; } = cancellationToken;
    protected override Metadata ResponseTrailersCore { get; } = Metadata.Empty;
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore { get; } = new AuthContext(null, []);

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
    {
        throw new NotImplementedException();
    }

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
    {
        throw new NotImplementedException();
    }
}

internal sealed class GrpcGrainTypeConfigurator(GrainClassMap grainClassMap) : IConfigureGrainTypeComponents
{
    public void Configure(GrainType grainType, GrainProperties properties, GrainTypeSharedContext shared)
    {
        if (!grainClassMap.TryGetGrainClass(grainType, out var grainClass))
        {
            return;
        }

        var bindMethodInfo = BindMethodFinder.GetBindMethod(grainClass);
        if (bindMethodInfo is null)
        {
            // Not a gRPC service grain.
            return;
        }

        // The second parameter is always the service base type
        // See: https://github.com/grpc/grpc-dotnet/blob/e9cc7e15796d39f1d2656178f56a45c09147d0fe/src/Grpc.AspNetCore.Server/Model/Internal/BinderServiceModelProvider.cs#L48
        var serviceParameter = bindMethodInfo.GetParameters()[1];

        var binder = new GrainServiceBinder(grainClass, serviceParameter.ParameterType);
        bindMethodInfo.Invoke(null, [binder, null]);

        var invoker = new GrpcServiceGrainCallInvoker(binder.Methods);
        shared.SetComponent(invoker);
    }

    private sealed class GrainServiceBinder(Type serviceType, Type declaringType) : ServiceBinderBase
    {
        public Dictionary<string, GrpcMethodModel> Methods { get; } = [];

        public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method, UnaryServerMethod<TRequest, TResponse>? handler)
        {
            var delegateType = typeof(UnaryServerMethod<,,>).MakeGenericType(serviceType, typeof(TRequest), typeof(TResponse));
            var methodDelegate = GetMethodDelegate(delegateType, method.Name, serviceType, [typeof(TRequest), typeof(ServerCallContext)]);

            Methods.Add(method.Name, new GrpcMethodModel(method, Invoker));

            async ValueTask<Response> Invoker(object serviceInstance, GrpcGrainUnaryCall call)
            {
                var callContext = new OrleansServerCallContext(DateTime.UtcNow.Add(TimeSpan.FromSeconds(30)), CancellationToken.None);
                var requestArgument = (TRequest)call.Argument!;
                try
                {
                    var immediateResult = methodDelegate.DynamicInvoke([serviceInstance, requestArgument, callContext]);
                    var response = await (Task<TResponse>)immediateResult!;

                    return Response.FromResult(response);
                }
                catch (Exception exception)
                {
                    return Response.FromException(exception);
                }
            }
        }

        public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method, ClientStreamingServerMethod<TRequest, TResponse>? handler)
            => AddMethodCore<ClientStreamingServerMethod<object, TRequest, TRequest>>(method, [serviceType, typeof(IAsyncStreamReader<TRequest>), typeof(ServerCallContext)]);
        public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method, ServerStreamingServerMethod<TRequest, TResponse>? handler)
            => AddMethodCore<ServerStreamingServerMethod<object, TRequest, TRequest>>(method, [serviceType, typeof(TRequest), typeof(IServerStreamWriter<TResponse>), typeof(ServerCallContext) ]);
        public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method, DuplexStreamingServerMethod<TRequest, TResponse>? handler)
            => AddMethodCore<DuplexStreamingServerMethod<object, TRequest, TRequest>>(method, [serviceType, typeof(IAsyncStreamReader<TRequest>), typeof(IServerStreamWriter<TResponse>), typeof(ServerCallContext)] );

        private void AddMethodCore<TDelegate>(IMethod method, Type[] methodParameters) where TDelegate : Delegate
        {
            throw new NotImplementedException();
        }

        private Delegate GetMethodDelegate(Type delegateType, string methodName, Type serviceType, Type[] methodParameters)
        {
            var handlerMethod = GetMethodCore(methodName, methodParameters);

            if (handlerMethod == null)
            {
                throw new InvalidOperationException($"Could not find '{methodName}' on {serviceType}.");
            }

            return Delegate.CreateDelegate(type: delegateType, firstArgument: null, method: handlerMethod, throwOnBindFailure: true)!;

            MethodInfo? GetMethodCore(string methodName, Type[] methodParameters)
            {
                Type? currentType = serviceType;
                while (currentType != null)
                {
                    // Specify binding flags explicitly because we don't want to match static methods.
                    var matchingMethod = currentType.GetMethod(
                        methodName,
                        BindingFlags.Public | BindingFlags.Instance,
                        binder: null,
                        types: methodParameters,
                        modifiers: null);

                    if (matchingMethod == null)
                    {
                        return null;
                    }

                    // Validate that the method overrides the virtual method on the base service type.
                    // If there is a method with the same name it will hide the base method. Ignore it,
                    // and continue searching on the base type.
                    if (matchingMethod.IsVirtual)
                    {
                        var baseDefinitionMethod = matchingMethod.GetBaseDefinition();
                        if (baseDefinitionMethod != null && baseDefinitionMethod.DeclaringType == declaringType)
                        {
                            return matchingMethod;
                        }
                    }

                    currentType = currentType.BaseType;
                }

                return null;
            }
        }
    }
}

internal static class GrpcWellKnownGrainProperties
{
    public const string GrpcMethodPrefix = "gRPC/";
}

internal sealed class GrpcGrainPropertiesPopulator : IGrainPropertiesProvider
{
    public void Populate(Type grainClass, GrainType grainType, Dictionary<string, string> properties)
    {
        var bindMethodInfo = BindMethodFinder.GetBindMethod(grainClass);
        if (bindMethodInfo is null)
        {
            // Not a gRPC service grain.
            return;
        }

        // The second parameter is always the service base type
        // See: https://github.com/grpc/grpc-dotnet/blob/e9cc7e15796d39f1d2656178f56a45c09147d0fe/src/Grpc.AspNetCore.Server/Model/Internal/BinderServiceModelProvider.cs#L48
        var serviceParameter = bindMethodInfo.GetParameters()[1];

        var binder = new ServiceMethodCollection();
        bindMethodInfo.Invoke(null, [binder, null]);

        foreach (var method in binder.Methods)
        {
            properties[$"{GrpcWellKnownGrainProperties.GrpcMethodPrefix}{method.ServiceName}/{method.Name}"] = method.Type.ToString();
        }
    }

    private sealed class ServiceMethodCollection : ServiceBinderBase
    {
        public List<IMethod> Methods { get; } = [];

        public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method, UnaryServerMethod<TRequest, TResponse>? handler) => Methods.Add(method);
        public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method, ClientStreamingServerMethod<TRequest, TResponse>? handler) => Methods.Add(method);
        public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method, ServerStreamingServerMethod<TRequest, TResponse>? handler) => Methods.Add(method);
        public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method, DuplexStreamingServerMethod<TRequest, TResponse>? handler) => Methods.Add(method);
    }
}
