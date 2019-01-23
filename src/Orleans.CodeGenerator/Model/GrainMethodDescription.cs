using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Model
{
    internal class GrainMethodDescription
    {
        public GrainMethodDescription(int methodId, IMethodSymbol method)
        {
            this.MethodId = methodId;
            this.Method = method;
        }

        public int MethodId { get; }
        public IMethodSymbol Method { get; }
        
        public override int GetHashCode() => this.Method != null ? this.Method.GetHashCode() : 0;

        protected bool Equals(GrainMethodDescription other) => Equals(this.Method, other.Method);

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (obj.GetType() != this.GetType())
            {
                return false;
            }

            return this.Equals((GrainMethodDescription) obj);
        }
    }
}