using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Utilities;

namespace Orleans.CodeGenerator.Model
{
    internal interface IMemberDescription
    {
        uint FieldId { get; }
        ISymbol Member { get; }
        ITypeSymbol Type { get; }
        string Name { get; }
    }

    internal class FieldDescription : IMemberDescription
    {
        public FieldDescription(uint fieldId, IFieldSymbol field)
        {
            this.FieldId = fieldId;
            this.Field = field;
        }

        public IFieldSymbol Field { get; }
        public uint FieldId { get; }
        public ISymbol Member => this.Field;
        public ITypeSymbol Type => this.Field.Type;
        public string Name => this.Field.Name;
    }

    internal class PropertyDescription : IMemberDescription
    {
        public PropertyDescription(uint fieldId, IPropertySymbol property)
        {
            this.FieldId = fieldId;
            this.Property = property;
        }

        public uint FieldId { get; }
        public ISymbol Member => this.Property;
        public ITypeSymbol Type => this.Property.Type;
        public IPropertySymbol Property { get; }
        public string Name => this.Property.Name;
    }
    internal interface ISerializableTypeDescription
    {
        TypeSyntax TypeSyntax { get; }
        TypeSyntax UnboundTypeSyntax { get; }
        bool HasComplexBaseType { get; }
        INamedTypeSymbol BaseType { get; }
        string Name { get; }
        bool IsValueType { get; }
        bool IsGenericType { get; }
        ImmutableArray<ITypeParameterSymbol> TypeParameters { get; }
        List<IMemberDescription> Members { get; }
    }
    internal class SerializableTypeDescription : ISerializableTypeDescription
    {
        public SerializableTypeDescription(INamedTypeSymbol type, IEnumerable<IMemberDescription> members)
        {
            this.Type = type;
            this.Members = members.ToList();
        }

        private INamedTypeSymbol Type { get; }

        public TypeSyntax TypeSyntax => this.Type.ToTypeSyntax();
        public TypeSyntax UnboundTypeSyntax => this.Type.ToTypeSyntax();

        public bool HasComplexBaseType => !this.IsValueType &&
                                          this.Type.BaseType != null &&
                                          this.Type.BaseType.SpecialType != SpecialType.System_Object;

        public INamedTypeSymbol BaseType => this.Type.BaseType;

        public string Name => this.Type.Name;

        public bool IsValueType => this.Type.IsValueType;

        public bool IsGenericType => this.Type.IsGenericType;

        public ImmutableArray<ITypeParameterSymbol> TypeParameters => this.Type.TypeParameters;

        public List<IMemberDescription> Members { get; }
    }
    internal interface IGeneratedInvokerDescription : ISerializableTypeDescription
    {
        IInvokableInterfaceDescription InterfaceDescription { get; }
    }

    internal interface IInvokableInterfaceDescription
    {
        INamedTypeSymbol InterfaceType { get; }
        List<GrainMethodDescription> Methods { get; }
        bool IsExtension { get; }
    }

    internal interface IGeneratedProxyDescription
    {
        TypeSyntax TypeSyntax { get; }
        GrainInterfaceDescription InterfaceDescription { get; }
    }

    internal class InvokableInterfaceDescription : IInvokableInterfaceDescription
    {
        public InvokableInterfaceDescription(
            WellKnownTypes WellKnownTypes,
            INamedTypeSymbol interfaceType,
            IEnumerable<GrainMethodDescription> methods,
            INamedTypeSymbol proxyBaseType,
            bool isExtension)
        {
            this.ValidateBaseClass(WellKnownTypes, proxyBaseType);
            this.InterfaceType = interfaceType;
            this.ProxyBaseType = proxyBaseType;
            this.IsExtension = isExtension;
            this.Methods = methods.ToList();
        }

        void ValidateBaseClass(WellKnownTypes l, INamedTypeSymbol baseClass)
        {
            var found = false;
            foreach (var member in baseClass.GetMembers("Invoke"))
            {
                if (!(member is IMethodSymbol method)) continue;
                if (method.TypeParameters.Length != 1) continue;
                if (method.Parameters.Length != 1) continue;
                if (!method.Parameters[0].Type.Equals(method.TypeParameters[0])) continue;
                if (!method.TypeParameters[0].ConstraintTypes.Contains(l.IInvokable)) continue;
                if (!method.ReturnType.Equals(l.ValueTask)) continue;
                found = true;
            }

            if (!found)
            {
                throw new InvalidOperationException(
                    $"Proxy base class {baseClass} does not contain a definition for ValueTask Invoke<T>(T) where T : IInvokable");
            }
        }

        public INamedTypeSymbol InterfaceType { get; }
        public List<GrainMethodDescription> Methods { get; }
        public INamedTypeSymbol ProxyBaseType { get; }
        public bool IsExtension { get; }
    }
}