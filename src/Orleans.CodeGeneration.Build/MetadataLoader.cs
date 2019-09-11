using System;
using System.Collections.Generic;
using System.Reflection;

namespace Orleans.CodeGeneration
{
    internal class MetadataLoader : IDisposable
    {
        private readonly MetadataLoadContext metadataContext;

        public MetadataLoader(string path, List<string> referencedAssemblies)
        {
            var paths = new List<string> { path };
            paths.AddRange(referencedAssemblies);

            var assemblyResolver = new PathAssemblyResolver(paths);
            this.metadataContext = new MetadataLoadContext(assemblyResolver);
            this.Assembly = this.metadataContext.LoadFromAssemblyPath(path);
        }

        public Assembly Assembly { get; }

        public void Dispose()
        {
            this.metadataContext?.Dispose();
        }
    }
}