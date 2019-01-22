using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator.Generators
{
    /// <summary>
    /// Generates RPC stub objects called invokers.
    /// </summary>
    internal static class InvokableGenerator
    {
        public static (ClassDeclarationSyntax, IGeneratedInvokerDescription) Generate(
            Compilation compilation,
            WellKnownTypes WellKnownTypes,
            IInvokableInterfaceDescription interfaceDescription,
            MethodDescription methodDescription)
        {
            var method = methodDescription.Method;
            var generatedClassName = GetSimpleClassName(method);

            var fieldDescriptions = GetFieldDescriptions(methodDescription.Method, interfaceDescription);
            var fields = GetFieldDeclarations(fieldDescriptions, WellKnownTypes);
            var ctor = GenerateConstructor(generatedClassName, fieldDescriptions);

            var targetField = fieldDescriptions.OfType<TargetFieldDescription>().Single();
            var resultField = fieldDescriptions.OfType<ResultFieldDescription>().FirstOrDefault();

            var classDeclaration = SyntaxFactory.ClassDeclaration(generatedClassName)
                .AddBaseListTypes(SyntaxFactory.SimpleBaseType(WellKnownTypes.Invokable.ToTypeSyntax()))
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.InternalKeyword), SyntaxFactory.Token(SyntaxKind.SealedKeyword))
                .AddAttributeLists(
                    SyntaxFactory.AttributeList(SingletonSeparatedList(CodeGenerator.GetGeneratedCodeAttributeSyntax())))
                .AddMembers(fields)
                .AddMembers(ctor)
                .AddMembers(
                    GenerateGetArgumentCount(WellKnownTypes, methodDescription),
                    GenerateSetTargetMethod(WellKnownTypes, interfaceDescription, targetField),
                    GenerateGetTargetMethod(WellKnownTypes, targetField),
                    GenerateResetMethod(WellKnownTypes, fieldDescriptions),
                    GenerateGetArgumentMethod(WellKnownTypes, methodDescription, fieldDescriptions),
                    GenerateSetArgumentMethod(WellKnownTypes, methodDescription, fieldDescriptions),
                    GenerateInvokeMethod(WellKnownTypes, methodDescription, fieldDescriptions, targetField, resultField),
                    GenerateSetResultProperty(WellKnownTypes, resultField),
                    GenerateGetResultProperty(WellKnownTypes, resultField));

            var typeParameters = interfaceDescription.InterfaceType.TypeParameters.Select(tp => (tp, tp.Name))
                .Concat(method.TypeParameters.Select(tp => (tp, tp.Name)))
                .ToList();
            if (typeParameters.Count > 0)
            {
                classDeclaration = AddGenericTypeConstraints(classDeclaration, typeParameters);
            }

            return (classDeclaration,
                new GeneratedInvokerDescription(
                    interfaceDescription,
                    methodDescription,
                    generatedClassName,
                    fieldDescriptions.OfType<IMemberDescription>().ToList()));
        }

        private static MemberDeclarationSyntax GenerateSetTargetMethod(
            WellKnownTypes WellKnownTypes,
            IInvokableInterfaceDescription interfaceDescription,
            TargetFieldDescription targetField)
        {
            var type = SyntaxFactory.IdentifierName("TTargetHolder");
            var typeToken = SyntaxFactory.Identifier("TTargetHolder");
            var holderParameter = SyntaxFactory.Identifier("holder");
            var holder = SyntaxFactory.IdentifierName("holder");

            var getTarget = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        holder,
                        SyntaxFactory.GenericName(interfaceDescription.IsExtension ? "GetExtension" : "GetTarget")
                            .WithTypeArgumentList(
                                SyntaxFactory.TypeArgumentList(
                                    SingletonSeparatedList(interfaceDescription.InterfaceType.ToTypeSyntax())))))
                .WithArgumentList(SyntaxFactory.ArgumentList());

            var body =
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.ThisExpression().Member(targetField.FieldName),
                    getTarget);
            return SyntaxFactory.MethodDeclaration(WellKnownTypes.Void.ToTypeSyntax(), "SetTarget")
                .WithTypeParameterList(SyntaxFactory.TypeParameterList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.TypeParameter(typeToken))))
                .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Parameter(holderParameter).WithType(type))))
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(body))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword)));
        }

        private static MemberDeclarationSyntax GenerateGetTargetMethod(
            WellKnownTypes WellKnownTypes,
            TargetFieldDescription targetField)
        {
            var type = SyntaxFactory.IdentifierName("TTarget");
            var typeToken = SyntaxFactory.Identifier("TTarget");

            var body = SyntaxFactory.CastExpression(type, SyntaxFactory.ThisExpression().Member(targetField.FieldName));
            return SyntaxFactory.MethodDeclaration(type, "GetTarget")
                .WithTypeParameterList(SyntaxFactory.TypeParameterList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.TypeParameter(typeToken))))
                .WithParameterList(SyntaxFactory.ParameterList())
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(body))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword)));
        }

        private static MemberDeclarationSyntax GenerateGetArgumentMethod(
            WellKnownTypes WellKnownTypes,
            MethodDescription methodDescription,
            List<FieldDescription> fields)
        {
            var index = SyntaxFactory.IdentifierName("index");
            var type = SyntaxFactory.IdentifierName("TArgument");
            var typeToken = SyntaxFactory.Identifier("TArgument");

            var cases = new List<SwitchSectionSyntax>();
            foreach (var field in fields)
            {
                if (!(field is MethodParameterFieldDescription parameter)) continue;

                // C#: case {index}: return (TArgument)(object){field}
                var label = SyntaxFactory.CaseSwitchLabel(
                    SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(parameter.ParameterOrdinal)));
                cases.Add(
                    SyntaxFactory.SwitchSection(
                        SyntaxFactory.SingletonList<SwitchLabelSyntax>(label),
                        new SyntaxList<StatementSyntax>(
                            SyntaxFactory.ReturnStatement(
                                SyntaxFactory.CastExpression(
                                    type,
                                    SyntaxFactory.CastExpression(
                                        WellKnownTypes.Object.ToTypeSyntax(),
                                        SyntaxFactory.ThisExpression().Member(parameter.FieldName)))))));
            }

            // C#: default: return HagarGeneratedCodeHelper.InvokableThrowArgumentOutOfRange<TArgument>(index, {maxArgs})
            var throwHelperMethod = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("HagarGeneratedCodeHelper"),
                SyntaxFactory.GenericName("InvokableThrowArgumentOutOfRange")
                    .WithTypeArgumentList(
                        SyntaxFactory.TypeArgumentList(
                            SyntaxFactory.SingletonSeparatedList<TypeSyntax>(type))));
            cases.Add(
                SyntaxFactory.SwitchSection(
                    SyntaxFactory.SingletonList<SwitchLabelSyntax>(SyntaxFactory.DefaultSwitchLabel()),
                    new SyntaxList<StatementSyntax>(
                        SyntaxFactory.ReturnStatement(
                            SyntaxFactory.InvocationExpression(
                                throwHelperMethod,
                                SyntaxFactory.ArgumentList(
                                    SyntaxFactory.SeparatedList(
                                        new[]
                                        {
                                            SyntaxFactory.Argument(index),
                                            SyntaxFactory.Argument(
                                                SyntaxFactory.LiteralExpression(
                                                    SyntaxKind.NumericLiteralExpression,
                                                    SyntaxFactory.Literal(
                                                        Math.Max(0, methodDescription.Method.Parameters.Length - 1))))
                                        })))))));
            var body = SyntaxFactory.SwitchStatement(index, new SyntaxList<SwitchSectionSyntax>(cases));
            return SyntaxFactory.MethodDeclaration(type, "GetArgument")
                .WithTypeParameterList(SyntaxFactory.TypeParameterList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.TypeParameter(typeToken))))
                .WithParameterList(
                    SyntaxFactory.ParameterList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Parameter(SyntaxFactory.Identifier("index")).WithType(WellKnownTypes.Int32.ToTypeSyntax()))))
                .WithBody(SyntaxFactory.Block(body))
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword)));
        }

        private static MemberDeclarationSyntax GenerateSetArgumentMethod(
            WellKnownTypes WellKnownTypes,
            MethodDescription methodDescription,
            List<FieldDescription> fields)
        {
            var index = SyntaxFactory.IdentifierName("index");
            var value = SyntaxFactory.IdentifierName("value");
            var type = SyntaxFactory.IdentifierName("TArgument");
            var typeToken = SyntaxFactory.Identifier("TArgument");

            var cases = new List<SwitchSectionSyntax>();
            foreach (var field in fields)
            {
                if (!(field is MethodParameterFieldDescription parameter)) continue;

                // C#: case {index}: {field} = (TField)(object)value; return;
                var label = SyntaxFactory.CaseSwitchLabel(
                    SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(parameter.ParameterOrdinal)));
                cases.Add(
                    SyntaxFactory.SwitchSection(
                        SyntaxFactory.SingletonList<SwitchLabelSyntax>(label),
                        new SyntaxList<StatementSyntax>(
                            new StatementSyntax[]
                            {
                                SyntaxFactory.ExpressionStatement(
                                    SyntaxFactory.AssignmentExpression(
                                        SyntaxKind.SimpleAssignmentExpression,
                                        SyntaxFactory.ThisExpression().Member(parameter.FieldName),
                                        SyntaxFactory.CastExpression(
                                            parameter.FieldType.ToTypeSyntax(),
                                            SyntaxFactory.CastExpression(
                                                WellKnownTypes.Object.ToTypeSyntax(),
                                                value
                                            )))),
                                SyntaxFactory.ReturnStatement()
                            })));
            }

            // C#: default: return HagarGeneratedCodeHelper.InvokableThrowArgumentOutOfRange<TArgument>(index, {maxArgs})
            var maxArgs = Math.Max(0, methodDescription.Method.Parameters.Length - 1);
            var throwHelperMethod = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("HagarGeneratedCodeHelper"),
                SyntaxFactory.GenericName("InvokableThrowArgumentOutOfRange")
                    .WithTypeArgumentList(
                        SyntaxFactory.TypeArgumentList(
                            SyntaxFactory.SingletonSeparatedList<TypeSyntax>(type))));
            cases.Add(
                SyntaxFactory.SwitchSection(
                    SyntaxFactory.SingletonList<SwitchLabelSyntax>(SyntaxFactory.DefaultSwitchLabel()),
                    new SyntaxList<StatementSyntax>(
                        new StatementSyntax[]
                        {
                            SyntaxFactory.ExpressionStatement(
                                SyntaxFactory.InvocationExpression(
                                    throwHelperMethod,
                                    SyntaxFactory.ArgumentList(
                                        SyntaxFactory.SeparatedList(
                                            new[]
                                            {
                                                SyntaxFactory.Argument(index),
                                                SyntaxFactory.Argument(
                                                    SyntaxFactory.LiteralExpression(
                                                        SyntaxKind.NumericLiteralExpression,
                                                        SyntaxFactory.Literal(maxArgs)))
                                            })))),
                            SyntaxFactory.ReturnStatement()
                        })));
            var body = SyntaxFactory.SwitchStatement(index, new SyntaxList<SwitchSectionSyntax>(cases));
            return SyntaxFactory.MethodDeclaration(WellKnownTypes.Void.ToTypeSyntax(), "SetArgument")
                .WithTypeParameterList(SyntaxFactory.TypeParameterList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.TypeParameter(typeToken))))
                .WithParameterList(
                    SyntaxFactory.ParameterList(
                        SyntaxFactory.SeparatedList(
                            new[]
                            {
                                SyntaxFactory.Parameter(SyntaxFactory.Identifier("index")).WithType(WellKnownTypes.Int32.ToTypeSyntax()),
                                SyntaxFactory.Parameter(SyntaxFactory.Identifier("value"))
                                    .WithType(type)
                                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InKeyword)))
                            }
                        )))
                .WithBody(SyntaxFactory.Block(body))
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword)));
        }

        private static MemberDeclarationSyntax GenerateInvokeMethod(
            WellKnownTypes WellKnownTypes,
            MethodDescription method,
            List<FieldDescription> fields,
            TargetFieldDescription target,
            ResultFieldDescription result)
        {
            var body = new List<StatementSyntax>();

            var resultTask = SyntaxFactory.IdentifierName("resultTask");

            // C# var resultTask = this.target.{Method}({params});
            var args = SyntaxFactory.SeparatedList(
                fields.OfType<MethodParameterFieldDescription>()
                    .OrderBy(p => p.ParameterOrdinal)
                    .Select(p => SyntaxFactory.Argument(SyntaxFactory.ThisExpression().Member(p.FieldName))));
            ExpressionSyntax methodCall;
            if (method.Method.TypeParameters.Length > 0)
            {
                methodCall = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ThisExpression().Member(target.FieldName),
                    SyntaxFactory.GenericName(
                        SyntaxFactory.Identifier(method.Method.Name),
                        SyntaxFactory.TypeArgumentList(
                            SeparatedList<TypeSyntax>(
                                method.Method.TypeParameters.Select(p => IdentifierName(p.Name))))));
            }
            else
            {
                methodCall = SyntaxFactory.ThisExpression().Member(target.FieldName).Member(method.Method.Name);
            }
            
            body.Add(
                SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(
                        SyntaxFactory.ParseTypeName("var"),
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.VariableDeclarator(resultTask.Identifier)
                                .WithInitializer(
                                    SyntaxFactory.EqualsValueClause(
                                        SyntaxFactory.InvocationExpression(
                                            methodCall,
                                            SyntaxFactory.ArgumentList(args))))))));

            // C#: if (resultTask.IsCompleted) // Even if it failed.
            // C#: {
            // C#:     this.result = resultTask.GetAwaiter().GetResult();
            // C#:     return default; // default(ValueTask) is a successfully completed ValueTask.
            // C#: }
            var synchronousCompletionBody = new List<StatementSyntax>();
            if (result != null)
            {
                synchronousCompletionBody.Add(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.ThisExpression().Member(result.FieldName),
                            SyntaxFactory.InvocationExpression(
                                SyntaxFactory.InvocationExpression(resultTask.Member("GetAwaiter")).Member("GetResult")))));
            }

            synchronousCompletionBody.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.DefaultExpression(WellKnownTypes.ValueTask.ToTypeSyntax())));
            body.Add(SyntaxFactory.IfStatement(resultTask.Member("IsCompleted"), SyntaxFactory.Block(synchronousCompletionBody)));

            // C#: async ValueTask InvokeAsync(ValueTask<int> asyncValue)
            // C#: {
            // C#:     this.result = await asyncValue.ConfigureAwait(false);
            // C#: }
            var invokeAsyncParam = SyntaxFactory.IdentifierName("asyncTask");
            var invokeAsyncBody = new List<StatementSyntax>();
            var awaitExpression = SyntaxFactory.AwaitExpression(
                SyntaxFactory.InvocationExpression(
                    invokeAsyncParam.Member("ConfigureAwait"),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression))))));
            if (result != null)
            {
                invokeAsyncBody.Add(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.ThisExpression().Member(result.FieldName),
                            awaitExpression)));
            }
            else
            {
                invokeAsyncBody.Add(SyntaxFactory.ExpressionStatement(SyntaxFactory.AwaitExpression(invokeAsyncParam)));
            }

            var invokeAsync = SyntaxFactory.LocalFunctionStatement(WellKnownTypes.ValueTask.ToTypeSyntax(), "InvokeAsync")
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.AsyncKeyword)))
                .WithParameterList(
                    SyntaxFactory.ParameterList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Parameter(invokeAsyncParam.Identifier).WithType(method.Method.ReturnType.ToTypeSyntax()))))
                .WithBody(SyntaxFactory.Block(invokeAsyncBody));

            // C#: return InvokeAsync(resultTask);
            body.Add(
                SyntaxFactory.ReturnStatement(
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.IdentifierName("InvokeAsync"),
                        SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(resultTask))))));
            body.Add(invokeAsync);

            return SyntaxFactory.MethodDeclaration(WellKnownTypes.ValueTask.ToTypeSyntax(), "Invoke")
                .WithParameterList(SyntaxFactory.ParameterList())
                .WithBody(SyntaxFactory.Block(body))
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword)));
        }

        private static MemberDeclarationSyntax GenerateResetMethod(
            WellKnownTypes WellKnownTypes,
            List<FieldDescription> fields)
        {
            var body = new List<StatementSyntax>();

            foreach (var field in fields)
            {
                if (!field.IsInjected)
                {
                    body.Add(
                        SyntaxFactory.ExpressionStatement(
                            SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                SyntaxFactory.ThisExpression().Member(field.FieldName),
                                SyntaxFactory.DefaultExpression(field.FieldType.ToTypeSyntax()))));
                }
            }

            return SyntaxFactory.MethodDeclaration(WellKnownTypes.Void.ToTypeSyntax(), "Reset")
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
                .WithBody(SyntaxFactory.Block(body));
        }

        private static MemberDeclarationSyntax GenerateGetArgumentCount(
            WellKnownTypes WellKnownTypes,
            MethodDescription methodDescription) =>
            SyntaxFactory.PropertyDeclaration(WellKnownTypes.Int32.ToTypeSyntax(), "ArgumentCount")
                .WithExpressionBody(
                    SyntaxFactory.ArrowExpressionClause(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            Literal(methodDescription.Method.Parameters.Length))))
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        private static MemberDeclarationSyntax GenerateResultProperty(
            WellKnownTypes WellKnownTypes,
            ResultFieldDescription resultField)
        {
            var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithExpressionBody(
                    SyntaxFactory.ArrowExpressionClause(
                        SyntaxFactory.CastExpression(
                            WellKnownTypes.Object.ToTypeSyntax(),
                            SyntaxFactory.ThisExpression().Member(resultField.FieldName))))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

            var setter = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                .WithExpressionBody(
                    SyntaxFactory.ArrowExpressionClause(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.ThisExpression().Member(resultField.FieldName),
                            SyntaxFactory.CastExpression(resultField.FieldType.ToTypeSyntax(), SyntaxFactory.IdentifierName("value")))))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

            return SyntaxFactory.PropertyDeclaration(WellKnownTypes.Object.ToTypeSyntax(), "Result")
                .WithAccessorList(
                    SyntaxFactory.AccessorList()
                        .AddAccessors(
                            getter,
                            setter))
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword)));
        }

        private static MemberDeclarationSyntax GenerateSetResultProperty(
            WellKnownTypes WellKnownTypes,
            ResultFieldDescription resultField)
        {

            var type = SyntaxFactory.IdentifierName("TResult");
            var typeToken = SyntaxFactory.Identifier("TResult");

            ExpressionSyntax body;
            if (resultField != null)
            {
                body = SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.ThisExpression().Member(resultField.FieldName),
                    SyntaxFactory.CastExpression(
                        resultField.FieldType.ToTypeSyntax(),
                        SyntaxFactory.CastExpression(WellKnownTypes.Object.ToTypeSyntax(), SyntaxFactory.IdentifierName("value"))));
            }
            else
            {
                body = SyntaxFactory.ThrowExpression(
                    SyntaxFactory.ObjectCreationExpression(WellKnownTypes.InvalidOperationException.ToTypeSyntax())
                        .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument("Method does not have a return value.".GetLiteralExpression())))));
            }

            return SyntaxFactory.MethodDeclaration(WellKnownTypes.Void.ToTypeSyntax(), "SetResult")
                .WithTypeParameterList(SyntaxFactory.TypeParameterList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.TypeParameter(typeToken))))
                .WithParameterList(
                    SyntaxFactory.ParameterList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Parameter(SyntaxFactory.Identifier("value"))
                                .WithType(type)
                                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InKeyword))))))
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(body))
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }

        private static MemberDeclarationSyntax GenerateGetResultProperty(
            WellKnownTypes WellKnownTypes,
            ResultFieldDescription resultField)
        {

            var type = SyntaxFactory.IdentifierName("TResult");
            var typeToken = SyntaxFactory.Identifier("TResult");

            ExpressionSyntax body;
            if (resultField != null)
            {
                body = SyntaxFactory.CastExpression(
                    type,
                    SyntaxFactory.CastExpression(WellKnownTypes.Object.ToTypeSyntax(), SyntaxFactory.ThisExpression().Member(resultField.FieldName)));
            }
            else
            {
                body = SyntaxFactory.ThrowExpression(
                    SyntaxFactory.ObjectCreationExpression(WellKnownTypes.InvalidOperationException.ToTypeSyntax())
                        .WithArgumentList(
                            SyntaxFactory.ArgumentList(
                                SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.Argument("Method does not have a return value.".GetLiteralExpression())))));
            }

            return SyntaxFactory.MethodDeclaration(type, "GetResult")
                .WithTypeParameterList(SyntaxFactory.TypeParameterList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.TypeParameter(typeToken))))
                .WithParameterList(SyntaxFactory.ParameterList())
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(body))
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }

        private class GeneratedInvokerDescription : IGeneratedInvokerDescription
        {
            private readonly MethodDescription methodDescription;

            public GeneratedInvokerDescription(
                IInvokableInterfaceDescription interfaceDescription,
                MethodDescription methodDescription,
                string generatedClassName,
                List<IMemberDescription> members)
            {
                this.InterfaceDescription = interfaceDescription;
                this.methodDescription = methodDescription;
                this.Name = generatedClassName;
                this.Members = members;

                this.TypeParameters = interfaceDescription.InterfaceType.TypeParameters
                    .Concat(this.methodDescription.Method.TypeParameters)
                    .ToImmutableArray();
            }

            public TypeSyntax TypeSyntax
            {
                get
                {
                    var name = GetSimpleClassName(this.methodDescription.Method);

                    if (this.TypeParameters.Length > 0)
                    {
                        return SyntaxFactory.GenericName(
                            SyntaxFactory.Identifier(name),
                            SyntaxFactory.TypeArgumentList(
                                SyntaxFactory.SeparatedList<TypeSyntax>(this.TypeParameters.Select(p => SyntaxFactory.IdentifierName(p.Name)))));
                    }

                    return SyntaxFactory.IdentifierName(name);
                }
            }

            public TypeSyntax UnboundTypeSyntax => this.methodDescription.GetInvokableTypeName();
            public bool HasComplexBaseType => false;
            public INamedTypeSymbol BaseType => throw new NotImplementedException();
            public string Name { get; }
            public bool IsValueType => false;
            public bool IsGenericType => this.methodDescription.Method.IsGenericMethod;
            public ImmutableArray<ITypeParameterSymbol> TypeParameters { get; }
            public List<IMemberDescription> Members { get; }
            public IInvokableInterfaceDescription InterfaceDescription { get; }
        }

        public static string GetSimpleClassName(IMethodSymbol method)
        {
            var typeArgs = method.TypeParameters.Length > 0 ? "_" + method.TypeParameters.Length : string.Empty;
            var args = method.Parameters.Length > 0
                ? "_" + string.Join("_", method.Parameters.Select(p => p.Type.Name))
                : string.Empty;
            return
                $"{CodeGenerator.CodeGeneratorName}_Invokable_{method.ContainingType.Name}_{method.Name}{typeArgs}{args}";
        }

        private static ClassDeclarationSyntax AddGenericTypeConstraints(
            ClassDeclarationSyntax classDeclaration,
            List<(ITypeParameterSymbol, string)> typeParameters)
        {
            classDeclaration = classDeclaration.WithTypeParameterList(
                SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(typeParameters.Select(tp => SyntaxFactory.TypeParameter(tp.Item2)))));
            var constraints = new List<TypeParameterConstraintSyntax>();
            foreach (var (tp, _) in typeParameters)
            {
                constraints.Clear();
                if (tp.HasReferenceTypeConstraint)
                {
                    constraints.Add(SyntaxFactory.ClassOrStructConstraint(SyntaxKind.ClassConstraint));
                }

                if (tp.HasValueTypeConstraint)
                {
                    constraints.Add(SyntaxFactory.ClassOrStructConstraint(SyntaxKind.StructConstraint));
                }

                foreach (var c in tp.ConstraintTypes)
                {
                    constraints.Add(SyntaxFactory.TypeConstraint(c.ToTypeSyntax()));
                }

                if (tp.HasConstructorConstraint)
                {
                    constraints.Add(SyntaxFactory.ConstructorConstraint());
                }

                if (constraints.Count > 0)
                {
                    classDeclaration = classDeclaration.AddConstraintClauses(
                        SyntaxFactory.TypeParameterConstraintClause(tp.Name).AddConstraints(constraints.ToArray()));
                }
            }

            return classDeclaration;
        }

        private static MemberDeclarationSyntax[] GetFieldDeclarations(
            List<FieldDescription> fieldDescriptions,
            WellKnownTypes WellKnownTypes)
        {
            return fieldDescriptions.Select(GetFieldDeclaration).ToArray();

            MemberDeclarationSyntax GetFieldDeclaration(FieldDescription description)
            {
                var field = SyntaxFactory.FieldDeclaration(
                    SyntaxFactory.VariableDeclaration(
                        description.FieldType.ToTypeSyntax(),
                        SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(description.FieldName))));

                switch (description)
                {
                    case ResultFieldDescription _:
                    case MethodParameterFieldDescription _:
                        field = field.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
                        break;
                }

                if (!description.IsSerializable)
                {
                    field = field.AddAttributeLists(
                            SyntaxFactory.AttributeList()
                                .AddAttributes(SyntaxFactory.Attribute(WellKnownTypes.NonSerializedAttribute.ToNameSyntax())));
                }
                else if (description is MethodParameterFieldDescription parameter)
                {
                    field = field.AddAttributeLists(
                        SyntaxFactory.AttributeList()
                            .AddAttributes(
                                SyntaxFactory.Attribute(
                                    WellKnownTypes.IdAttribute.ToNameSyntax(),
                                    SyntaxFactory.AttributeArgumentList()
                                        .AddArguments(
                                            SyntaxFactory.AttributeArgument(
                                                SyntaxFactory.LiteralExpression(
                                                    SyntaxKind.NumericLiteralExpression,
                                                    SyntaxFactory.Literal(parameter.FieldId)))))));
                }

                return field;
            }
        }

        private static ConstructorDeclarationSyntax GenerateConstructor(
            string simpleClassName,
            List<FieldDescription> fieldDescriptions)
        {
            var injected = fieldDescriptions.Where(f => f.IsInjected).ToList();
            var parameters = injected.Select(
                f => SyntaxFactory.Parameter(f.FieldName.ToIdentifier()).WithType(f.FieldType.ToTypeSyntax()));
            var body = injected.Select(
                f => (StatementSyntax)SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.ThisExpression().Member(f.FieldName.ToIdentifierName()),
                        Unwrapped(f.FieldName.ToIdentifierName()))));
            return SyntaxFactory.ConstructorDeclaration(simpleClassName)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(parameters.ToArray())
                .AddBodyStatements(body.ToArray());

            ExpressionSyntax Unwrapped(ExpressionSyntax expr)
            {
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("HagarGeneratedCodeHelper"),
                        SyntaxFactory.IdentifierName("UnwrapService")),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] {SyntaxFactory.Argument(SyntaxFactory.ThisExpression()), SyntaxFactory.Argument(expr)})));
            }
        }

        private static List<FieldDescription> GetFieldDescriptions(
            IMethodSymbol method,
            IInvokableInterfaceDescription interfaceDescription)
        {
            var fields = new List<FieldDescription>();

            uint fieldId = 0;
            foreach (var parameter in method.Parameters)
            {
                fields.Add(new MethodParameterFieldDescription(parameter, $"arg{fieldId}", fieldId));
                fieldId++;
            }

            if (method.ReturnType is INamedTypeSymbol returnType && returnType.TypeArguments.Length == 1)
            {
                fields.Add(new ResultFieldDescription(returnType.TypeArguments[0]));
            }

            fields.Add(new TargetFieldDescription(interfaceDescription.InterfaceType));

            return fields;
        }

        internal abstract class FieldDescription
        {
            protected FieldDescription(ITypeSymbol fieldType, string fieldName)
            {
                this.FieldType = fieldType;
                this.FieldName = fieldName;
            }

            public ITypeSymbol FieldType { get; }
            public string FieldName { get; }
            public abstract bool IsInjected { get; }
            public abstract bool IsSerializable { get; }
        }

        internal class InjectedFieldDescription : FieldDescription
        {
            public InjectedFieldDescription(ITypeSymbol fieldType, string fieldName) : base(fieldType, fieldName)
            {
            }

            public override bool IsInjected => true;
            public override bool IsSerializable => false;
        }

        internal class CodecFieldDescription : FieldDescription, ICodecDescription
        {
            public CodecFieldDescription(ITypeSymbol fieldType, string fieldName, ITypeSymbol underlyingType) : base(
                fieldType,
                fieldName)
            {
                this.UnderlyingType = underlyingType;
            }

            public ITypeSymbol UnderlyingType { get; }
            public override bool IsInjected => true;
            public override bool IsSerializable => false;
        }

        internal class TypeFieldDescription : FieldDescription
        {
            public TypeFieldDescription(ITypeSymbol fieldType, string fieldName, ITypeSymbol underlyingType) : base(
                fieldType,
                fieldName)
            {
                this.UnderlyingType = underlyingType;
            }

            public ITypeSymbol UnderlyingType { get; }
            public override bool IsInjected => false;
            public override bool IsSerializable => false;
        }

        internal class ResultFieldDescription : FieldDescription
        {
            public ResultFieldDescription(ITypeSymbol fieldType) : base(fieldType, "result")
            {
            }

            public override bool IsInjected => false;
            public override bool IsSerializable => false;
        }

        internal class TargetFieldDescription : FieldDescription
        {
            public TargetFieldDescription(ITypeSymbol fieldType) : base(fieldType, "target")
            {
            }

            public override bool IsInjected => false;
            public override bool IsSerializable => false;
        }

        internal class MethodParameterFieldDescription : FieldDescription, IMemberDescription
        {
            public MethodParameterFieldDescription(IParameterSymbol parameter, string fieldName, uint fieldId)
                : base(parameter.Type, fieldName)
            {
                this.FieldId = fieldId;
                this.Parameter = parameter;
            }

            public int ParameterOrdinal => this.Parameter.Ordinal;

            public override bool IsInjected => false;
            public uint FieldId { get; }
            public ISymbol Member => this.Parameter;
            public ITypeSymbol Type => this.FieldType;
            public IParameterSymbol Parameter { get; }
            public string Name => this.FieldName;
            public override bool IsSerializable => true;
        }
    }
}