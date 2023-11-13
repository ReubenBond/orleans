namespace Orleans.TestingHost.Tests.Grains
{
    [Alias("Orleans.TestingHost.Tests.Grains.ISimpleGrain")]
    public interface ISimpleGrain : IGrainWithIntegerKey
    {
        [Alias("SetA")]
        Task SetA(int a);
        [Alias("SetB")]
        Task SetB(int b);
        [Alias("IncrementA")]
        Task IncrementA();
        [Alias("GetAxB")]
        Task<int> GetAxB();
        [Alias("GetAxB1")]
        Task<int> GetAxB(int a, int b);
        [Alias("GetA")]
        Task<int> GetA();
    }

    /// <summary>
    /// A simple grain that allows to set two arguments and then multiply them.
    /// </summary>
    public class SimpleGrain : Grain, ISimpleGrain
    {
        protected int A { get; set; }
        protected int B { get; set; }

        public Task SetA(int a)
        {
            A = a;
            return Task.CompletedTask;
        }

        public Task SetB(int b)
        {
            this.B = b;
            return Task.CompletedTask;
        }

        public Task IncrementA()
        {
            A = A + 1;
            return Task.CompletedTask;
        }

        public Task<int> GetAxB()
        {
            return Task.FromResult(A * B);
        }

        public Task<int> GetAxB(int a, int b)
        {
            return Task.FromResult(a * b);
        }

        public Task<int> GetA()
        {
            return Task.FromResult(A);
        }
    }
}
