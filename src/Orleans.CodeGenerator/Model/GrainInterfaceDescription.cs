using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Generators;
using Orleans.CodeGenerator.Utilities;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator.Model
{
    internal class GrainInterfaceDescription : ITypeDescription, IInvokableInterfaceDescription
    {
        public GrainInterfaceDescription(INamedTypeSymbol type, int interfaceId, ushort interfaceVersion, IEnumerable<GrainMethodDescription> members, WellKnownTypes wellKnownTypes)
        {
            this.Type = type;
            this.InterfaceId = interfaceId;
            this.InterfaceVersion = interfaceVersion;
            this.Methods = members.ToList();
            var attribute = type.GetAttributeOrDefault(
                wellKnownTypes.GenerateMethodSerializersAttribute,
                inherited: true);
            //var baseClass = (INamedTypeSymbol)attribute.ConstructorArguments[0].Value;
            //var isExtension = (bool)attribute.ConstructorArguments[1].Value;
            this.HasGenerateMethodSerializersAttribute = attribute != null;
            this.IsExtension = type.HasInterface(wellKnownTypes.IGrainExtension);
        }

        public bool HasGenerateMethodSerializersAttribute { get; }

        public ushort InterfaceVersion { get; }

        public int InterfaceId { get; }

        public INamedTypeSymbol Type { get; }

        public INamedTypeSymbol InterfaceType => this.Type;
        public List<GrainMethodDescription> Methods { get; }
        public bool IsExtension { get; }

        public string InvokerTypeName => GrainMethodInvokerGenerator.GetGeneratedClassName(this.Type);
        public string ReferenceTypeName => GrainReferenceGenerator.GetGeneratedClassName(this.Type);

        public Dictionary<GrainMethodDescription, IGeneratedInvokerDescription> Invokers { get; } = new Dictionary<GrainMethodDescription, IGeneratedInvokerDescription>();
        public TypeSyntax InvokerType => ParseTypeName(this.Type.GetParsableReplacementName(this.InvokerTypeName));
        public TypeSyntax ReferenceType => ParseTypeName(this.Type.GetParsableReplacementName(this.ReferenceTypeName));
    }
}