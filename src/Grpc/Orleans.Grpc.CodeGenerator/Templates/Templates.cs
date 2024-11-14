using System.IO;
using Fluid;

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace Orleans.Grpc.CodeGenerator;
#pragma warning restore IDE0130 // Namespace does not match folder structure

internal sealed class Templates
{
    public Templates()
    {
        var parser = new FluidParser();
        ClientProxy = parser.Parse(GetTemplateSource("ClientProxy.liquid"));
    }

    public IFluidTemplate ClientProxy { get; }

    private static string GetTemplateSource(string name)
    {
        var type = typeof(Templates);
        using var stream = type.Assembly.GetManifestResourceStream($"{type.Namespace}.{type.Name}.{name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
