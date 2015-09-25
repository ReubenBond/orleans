﻿/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

namespace Orleans.CodeGenerator
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Threading.Tasks;

    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    using Orleans.CodeGeneration;
    using Orleans.CodeGenerator.Utilities;
    using Orleans.Runtime;
    using Orleans.Serialization;

    using GrainInterfaceData = Orleans.CodeGeneration.GrainInterfaceData;
    using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

    /// <summary>
    /// Code generator which generates <see cref="GrainReference"/>s for grains.
    /// </summary>
    public static class GrainReferenceGenerator
    {
        /// <summary>
        /// The suffix appended to the name of generated classes.
        /// </summary>
        private const string ClassSuffix = "Reference";

        /// <summary>
        /// A reference to the CheckGrainObserverParamInternal method.
        /// </summary>
        private static readonly Expression<Action> CheckGrainObserverParamInternalExpression =
            () => GrainFactoryBase.CheckGrainObserverParamInternal(null);
        
        /// <summary>
        /// Registers GrainRefernece serializers for the provided <paramref name="assembly"/>.
        /// </summary>
        /// <param name="assembly">The assembly.</param>
        internal static void RegisterGrainReferenceSerializers(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                var attr = type.GetCustomAttribute<GrainReferenceAttribute>();
                if (attr == null || attr.GrainType == null)
                {
                    continue;
                }

                // Register GrainReference serialization methods.
                SerializationManager.Register(
                    type,
                    GrainReference.CopyGrainReference,
                    GrainReference.SerializeGrainReference,
                    (expected, stream) =>
                    {
                        var grainType = attr.GrainType;
                        if (expected.IsConstructedGenericType)
                        {
                            grainType = grainType.MakeGenericType(expected.GenericTypeArguments);
                        }

                        var deserialized = (IAddressable)GrainReference.DeserializeGrainReference(expected, stream);
                        return RuntimeClient.Current.InternalGrainFactory.Cast(deserialized, grainType);
                    });
            }
        }

        /// <summary>
        /// Generates the class for the provided grain types.
        /// </summary>
        /// <param name="grainType">
        /// The grain interface type.
        /// </param>
        /// <param name="onEncounteredType">
        /// The callback which is invoked when a type is encountered.
        /// </param>
        /// <returns>
        /// The generated class.
        /// </returns>
        internal static TypeDeclarationSyntax GenerateClass(Type grainType, Action<Type> onEncounteredType)
        {
            var genericTypes = grainType.IsGenericTypeDefinition
                                   ? grainType.GetGenericArguments()
                                         .Select(_ => SF.TypeParameter(_.ToString()))
                                         .ToArray()
                                   : new TypeParameterSyntax[0];
            
            // Create the special marker attribute.
            var markerAttribute =
                SF.Attribute(typeof(GrainReferenceAttribute).GetNameSyntax())
                    .AddArgumentListArguments(
                        SF.AttributeArgument(
                            SF.TypeOfExpression(grainType.GetTypeSyntax(includeGenericParameters: false))));
            var attributes = SF.AttributeList()
                .AddAttributes(
                    CodeGeneratorCommon.GetGeneratedCodeAttributeSyntax(),
                    SF.Attribute(typeof(SerializableAttribute).GetNameSyntax()),
                    markerAttribute);

            var className = CodeGeneratorCommon.ClassPrefix + TypeUtils.GetSuitableClassName(grainType) + ClassSuffix;
            var classDeclaration =
                SF.ClassDeclaration(className)
                    .AddModifiers(SF.Token(SyntaxKind.InternalKeyword))
                    .AddBaseListTypes(
                        SF.SimpleBaseType(typeof(GrainReference).GetTypeSyntax()),
                        SF.SimpleBaseType(grainType.GetTypeSyntax()))
                    .AddMembers(GenerateConstructors(className))
                    .AddMembers(
                        GenerateInterfaceIdProperty(grainType),
                        GenerateInterfaceNameProperty(grainType),
                        GenerateIsCompatibleMethod(grainType),
                        GenerateGetMethodNameMethod(grainType))
                    .AddMembers(GenerateInvokeMethods(grainType, onEncounteredType))
                    .AddAttributeLists(attributes);
            if (genericTypes.Length > 0)
            {
                classDeclaration = classDeclaration.AddTypeParameterListParameters(genericTypes);
            }

            return classDeclaration;
        }

        /// <summary>
        /// Generates constructors.
        /// </summary>
        /// <param name="className">The class name.</param>
        /// <returns>Constructor syntax for the provided class name.</returns>
        private static MemberDeclarationSyntax[] GenerateConstructors(string className)
        {
            var baseConstructors =
                typeof(GrainReference).GetConstructors(
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).Where(_ => !_.IsPrivate);
            var constructors = new List<MemberDeclarationSyntax>();
            foreach (var baseConstructor in baseConstructors)
            {
                var args = baseConstructor.GetParameters()
                    .Select(arg => SF.Argument(arg.Name.ToIdentifierName()))
                    .ToArray();
                var declaration =
                    baseConstructor.GetDeclarationSyntax(className)
                        .WithInitializer(
                            SF.ConstructorInitializer(SyntaxKind.BaseConstructorInitializer)
                                .AddArgumentListArguments(args))
                        .AddBodyStatements();
                constructors.Add(declaration);
            }

            return constructors.ToArray();
        }

        /// <summary>
        /// Generates invoker methods.
        /// </summary>
        /// <param name="grainType">The grain type.</param>
        /// <param name="onEncounteredType">
        /// The callback which is invoked when a type is encountered.
        /// </param>
        /// <returns>Invoker methods for the provided grain type.</returns>
        private static MemberDeclarationSyntax[] GenerateInvokeMethods(Type grainType, Action<Type> onEncounteredType)
        {
            var baseReference = SF.BaseExpression();
            var methods = GrainInterfaceData.GetMethods(grainType);
            var members = new List<MemberDeclarationSyntax>();
            foreach (var method in methods)
            {
                onEncounteredType(method.ReturnType);
                var methodId = GrainInterfaceData.ComputeMethodId(method);
                var methodIdArgument =
                    SF.Argument(SF.LiteralExpression(SyntaxKind.NumericLiteralExpression, SF.Literal(methodId)));

                // Construct a new object array from all method arguments.
                var parameters = method.GetParameters();
                var body = new List<StatementSyntax>();
                foreach (var parameter in parameters)
                {
                    onEncounteredType(parameter.ParameterType);
                    if (typeof(IGrainObserver).IsAssignableFrom(parameter.ParameterType))
                    {
                        body.Add(
                            SF.ExpressionStatement(
                                CheckGrainObserverParamInternalExpression.Invoke()
                                    .AddArgumentListArguments(SF.Argument(parameter.Name.ToIdentifierName()))));
                    }
                }

                // Get the parameters argument value.
                ExpressionSyntax args;
                if (parameters.Length == 0)
                {
                    args = SF.LiteralExpression(SyntaxKind.NullLiteralExpression);
                }
                else
                {
                    args =
                        SF.ArrayCreationExpression(typeof(object).GetArrayTypeSyntax())
                            .WithInitializer(
                                SF.InitializerExpression(SyntaxKind.ArrayInitializerExpression)
                                    .AddExpressions(parameters.Select(GetParameterForInvocation).ToArray()));
                }

                var options = GetInvokeOptions(method);

                // Construct the invocation call.
                if (method.ReturnType == typeof(void))
                {
                    var invocation = SF.InvocationExpression(baseReference.Member("InvokeOneWayMethod"))
                        .AddArgumentListArguments(methodIdArgument)
                        .AddArgumentListArguments(SF.Argument(args));

                    if (options != null)
                    {
                        invocation = invocation.AddArgumentListArguments(options);
                    }

                    body.Add(SF.ExpressionStatement(invocation));
                }
                else
                {
                    var returnType = method.ReturnType == typeof(Task)
                                         ? typeof(object)
                                         : method.ReturnType.GenericTypeArguments[0];
                    var invocation =
                        SF.InvocationExpression(baseReference.Member("InvokeMethodAsync", returnType))
                            .AddArgumentListArguments(methodIdArgument)
                            .AddArgumentListArguments(SF.Argument(args));

                    if (options != null)
                    {
                        invocation = invocation.AddArgumentListArguments(options);
                    }

                    body.Add(SF.ReturnStatement(invocation));
                }

                members.Add(method.GetDeclarationSyntax().AddBodyStatements(body.ToArray()));
            }

            return members.ToArray();
        }

        /// <summary>
        /// Returns syntax for the options argument to <see cref="GrainReference.InvokeMethodAsync{T}"/> and <see cref="GrainReference.InvokeOneWayMethod"/>.
        /// </summary>
        /// <param name="method">The method which an invoke call is being generated for.</param>
        /// <returns>
        /// Argument syntax for the options argument to <see cref="GrainReference.InvokeMethodAsync{T}"/> and
        /// <see cref="GrainReference.InvokeOneWayMethod"/>, or <see langword="null"/> if no options are to be specified.
        /// </returns>
        private static ArgumentSyntax GetInvokeOptions(MethodInfo method)
        {
            var options = new List<ExpressionSyntax>();
            if (GrainInterfaceData.IsReadOnly(method))
            {
                options.Add(typeof(InvokeMethodOptions).GetNameSyntax().Member(InvokeMethodOptions.ReadOnly.ToString()));
            }

            if (GrainInterfaceData.IsUnordered(method))
            {
                options.Add(typeof(InvokeMethodOptions).GetNameSyntax().Member(InvokeMethodOptions.Unordered.ToString()));
            }

            if (GrainInterfaceData.IsAlwaysInterleave(method))
            {
                options.Add(typeof(InvokeMethodOptions).GetNameSyntax().Member(InvokeMethodOptions.AlwaysInterleave.ToString()));
            }

            ExpressionSyntax allOptions;
            if (options.Count <= 1)
            {
                allOptions = options.FirstOrDefault();
            }
            else
            {
                allOptions =
                    options.Aggregate((a, b) => SF.BinaryExpression(SyntaxKind.BitwiseOrExpression, a, b));
            }

            if (allOptions == null)
            {
                return null;
            }

            return SF.Argument(SF.NameColon("options"), SF.Token(SyntaxKind.None), allOptions);
        }

        private static ExpressionSyntax GetParameterForInvocation(ParameterInfo arg)
        {
            var argIdentifier = arg.Name.ToIdentifierName();

            // Addressable arguments must be converted to references before passing.
            if (typeof(IAddressable).IsAssignableFrom(arg.ParameterType)
                && (typeof(Grain).IsAssignableFrom(arg.ParameterType) || arg.ParameterType.IsInterface))
            {
                return
                    SF.ConditionalExpression(
                        SF.BinaryExpression(SyntaxKind.IsExpression, argIdentifier, typeof(Grain).GetTypeSyntax()),
                        SF.InvocationExpression(argIdentifier.Member("AsReference", arg.ParameterType)),
                        argIdentifier);
            }

            return argIdentifier;
        }

        private static MemberDeclarationSyntax GenerateInterfaceIdProperty(Type grainType)
        {
            var property = TypeUtils.Member((IGrainMethodInvoker _) => _.InterfaceId);
            var returnValue = SF.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SF.Literal(GrainInterfaceData.GetGrainInterfaceId(grainType)));
            return
                SF.PropertyDeclaration(typeof(int).GetTypeSyntax(), property.Name)
                    .AddAccessorListAccessors(
                        SF.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .AddBodyStatements(SF.ReturnStatement(returnValue)))
                    .AddModifiers(SF.Token(SyntaxKind.ProtectedKeyword), SF.Token(SyntaxKind.OverrideKeyword));
        }

        private static MemberDeclarationSyntax GenerateIsCompatibleMethod(Type grainType)
        {
            var method = TypeUtils.Method((GrainReference _) => _.IsCompatible(default(int)));
            var methodDeclaration = method.GetDeclarationSyntax();
            var interfaceIdParameter = method.GetParameters()[0].Name.ToIdentifierName();

            var interfaceIds =
                new HashSet<int>(
                    new[] { GrainInterfaceData.GetGrainInterfaceId(grainType) }.Concat(
                        GrainInterfaceData.GetRemoteInterfaces(grainType).Keys));

            var returnValue = default(BinaryExpressionSyntax);
            foreach (var interfaceId in interfaceIds)
            {
                var check = SF.BinaryExpression(
                    SyntaxKind.EqualsExpression,
                    interfaceIdParameter,
                    SF.LiteralExpression(SyntaxKind.NumericLiteralExpression, SF.Literal(interfaceId)));

                // If this is the first check, assign it, otherwise OR this check with the previous checks.
                returnValue = returnValue == null
                                  ? check
                                  : SF.BinaryExpression(SyntaxKind.LogicalOrExpression, returnValue, check);
            }

            return
                methodDeclaration.AddBodyStatements(SF.ReturnStatement(returnValue))
                    .AddModifiers(SF.Token(SyntaxKind.OverrideKeyword));
        }

        private static MemberDeclarationSyntax GenerateInterfaceNameProperty(Type grainType)
        {
            var propertyName = TypeUtils.Member((GrainReference _) => _.InterfaceName);
            var returnValue = grainType.GetParseableName().GetLiteralExpression();
            return
                SF.PropertyDeclaration(typeof(string).GetTypeSyntax(), propertyName.Name)
                    .AddAccessorListAccessors(
                        SF.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .AddBodyStatements(SF.ReturnStatement(returnValue)))
                    .AddModifiers(SF.Token(SyntaxKind.PublicKeyword), SF.Token(SyntaxKind.OverrideKeyword));
        }

        private static MethodDeclarationSyntax GenerateGetMethodNameMethod(Type grainType)
        {
            // Get the method with the correct type.
            var method = typeof(GrainReference).GetMethod(
                "GetMethodName",
                BindingFlags.NonPublic | BindingFlags.Instance);

            var methodDeclaration =
                method.GetDeclarationSyntax()
                    .AddModifiers(SF.Token(SyntaxKind.OverrideKeyword));
            var parameters = method.GetParameters();

            var interfaceIdArgument = parameters[0].Name.ToIdentifierName();
            var methodIdArgument = parameters[1].Name.ToIdentifierName();

            var interfaceCases = CodeGeneratorCommon.GenerateGrainInterfaceAndMethodSwitch(
                grainType,
                methodIdArgument,
                methodType => new StatementSyntax[] { SF.ReturnStatement(methodType.Name.GetLiteralExpression()) });

            // Generate the default case, which will throw a NotImplementedException.
            var errorMessage = SF.BinaryExpression(
                SyntaxKind.AddExpression,
                "interfaceId=".GetLiteralExpression(),
                interfaceIdArgument);
            var throwStatement =
                SF.ThrowStatement(
                    SF.ObjectCreationExpression(typeof(NotImplementedException).GetTypeSyntax())
                        .AddArgumentListArguments(SF.Argument(errorMessage)));
            var defaultCase = SF.SwitchSection().AddLabels(SF.DefaultSwitchLabel()).AddStatements(throwStatement);
            var interfaceIdSwitch =
                SF.SwitchStatement(interfaceIdArgument).AddSections(interfaceCases.ToArray()).AddSections(defaultCase);

            return methodDeclaration.AddBodyStatements(interfaceIdSwitch);
        }
    }
}