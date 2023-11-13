namespace Orleans.UnitTest.GrainInterfaces
{
    [Serializable]
    [GenerateSerializer]
    [Alias("Orleans.UnitTest.GrainInterfaces.MyTypeWithAnInternalTypeField")]
    public class MyTypeWithAnInternalTypeField
    {
        [Id(0)]
        private readonly MyInternalDependency _dependency;

        public MyTypeWithAnInternalTypeField()
        {
            _dependency = new MyInternalDependency();
        }

        [GenerateSerializer]
        [Alias("Orleans.UnitTest.GrainInterfaces.MyTypeWithAnInternalTypeField.MyInternalDependency")]
        internal class MyInternalDependency
        {
        }
    }

    // Verify that we do generate a custom serializer for MyTypeWithAnInternalTypeField because it is visible within the assembly.
    [Alias("Orleans.UnitTest.GrainInterfaces.IInternalReturnType")]
    public interface IInternalReturnType : IGrainWithIntegerKey
    {
        [Alias("Foo")]
        Task<MyTypeWithAnInternalTypeField> Foo();
    }
}
