using System.IO;
using Fluid;

namespace Orleans.Grpc.CodeGenerator;

internal static class Templates
{
    static Templates()
    {
        var parser = new FluidParser();
        ClientProxy = parser.Parse(GetTemplate("ClientProxy.liquid"));

        static string GetTemplate(string name)
        {
            var type = typeof(Templates);   
            var assembly = type.Assembly;
            var ns = type.Namespace;
            using var stream = assembly.GetManifestResourceStream($"{ns}.{name}");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public static IFluidTemplate ClientProxy { get; }
}
