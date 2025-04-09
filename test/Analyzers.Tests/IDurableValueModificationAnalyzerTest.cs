using System.Threading.Tasks;
using Analyzers.Tests;
using Xunit;

using Verify = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<Orleans.Analyzers.IDurableValueModificationAnalyzer, Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Orleans.Analyzers.Tests
{
    public class IDurableValueModificationAnalyzerTest : DiagnosticAnalyzerTestBase<IDurableValueModificationAnalyzer>
    {
        private const string Preamble = @"
using Orleans.Runtime;
using System.Threading.Tasks;

public class TestPerson
{
    public int Age { get; set; }
    public string Name { get; set; }
    public System.Collections.Generic.List<int> Scores { get; set; } = new();
}

public interface IMyGrain : Orleans.IGrainWithGuidKey
{
    Task TestMethod();
}

public class MyGrain : Orleans.Grain, IMyGrain
{
    private readonly IDurableValue<TestPerson> _state;

    public MyGrain(IDurableValue<TestPerson> state)
    {
        _state = state;
    }

    public async Task TestMethod()
    {
";

        private const string Postamble = @"
        await Task.CompletedTask;
    }
}
";

        [Fact]
        public Task ShouldWarn_When_ModifyingPropertyDirectly()
        {
            var code = Preamble + @"
        // Bad: Modifying property directly
        [| _state.Value |].Age = 2;
" + Postamble;

            return Verify.VerifyAnalyzerAsync(code);
        }

        [Fact]
        public Task ShouldWarn_When_CallingMutatingMethod()
        {
            var code = Preamble + @"
        // Bad: Calling mutating method
        [| _state.Value |].Scores.Add(100);
" + Postamble;

            return Verify.VerifyAnalyzerAsync(code);
        }

        [Fact]
        public Task ShouldWarn_When_UsingIncrementOperator()
        {
            var code = Preamble + @"
        // Bad: Using increment operator
        [| _state.Value |].Age++;
" + Postamble;

            return Verify.VerifyAnalyzerAsync(code);
        }

        [Fact]
        public Task ShouldWarn_When_UsingDecrementOperator()
        {
            var code = Preamble + @"
        // Bad: Using decrement operator
        --[| _state.Value |].Age;
" + Postamble;

            return Verify.VerifyAnalyzerAsync(code);
        }

        [Fact]
        public Task ShouldWarn_When_PassingAsRefArgument()
        {
            var testMethod = @"
    private void ModifyAge(ref int age) { age = 99; }
";
            var code = Preamble + testMethod + @"
        // Bad: Passing property as ref argument
        ModifyAge(ref [| _state.Value |].Age);
" + Postamble;

            return Verify.VerifyAnalyzerAsync(code);
        }

         [Fact]
        public Task ShouldWarn_When_PassingAsOutArgument()
        {
            var testMethod = @"
    private void GetAge(out int age) { age = 99; } // Example, might not make sense logically but tests syntax
";
            var code = Preamble + testMethod + @"
        // Bad: Passing property as out argument
        GetAge(out [| _state.Value |].Age);
" + Postamble;

            return Verify.VerifyAnalyzerAsync(code);
        }

        [Fact]
        public Task ShouldNotWarn_When_AssigningNewValue()
        {
            var code = Preamble + @"
        // Good: Assigning a new value
        _state.Value = new TestPerson { Age = 2 };
" + Postamble;

            return Verify.VerifyAnalyzerAsync(code);
        }

        [Fact]
        public Task ShouldNotWarn_When_UsingWithExpression()
        {
            var code = Preamble + @"
        // Good: Using 'with' expression
        _state.Value = _state.Value with { Age = 3 };
" + Postamble;

            return Verify.VerifyAnalyzerAsync(code);
        }

        [Fact]
        public Task ShouldNotWarn_When_ReadingValue()
        {
            var code = Preamble + @"
        // Good: Reading the value
        var person = _state.Value;
        var age = _state.Value.Age;
        if (_state.Value.Age > 10) { }
" + Postamble;

            return Verify.VerifyAnalyzerAsync(code);
        }

         [Fact]
        public Task ShouldNotWarn_When_CallingNonMutatingMethod()
        {
             // Assuming ToString() is non-mutating for this test
            var code = Preamble + @"
        // Good: Calling non-mutating method
        var name = _state.Value.ToString();
" + Postamble;

            return Verify.VerifyAnalyzerAsync(code);
        }

        [Fact]
        public Task ShouldNotWarn_When_AccessingValueOnNonDurableValueType()
        {
            // Test case where a type happens to have a 'Value' property but isn't IDurableValue<T>
            var setup = @"
using System.Threading.Tasks;

public class TestPerson { public int Age { get; set; } }
public class NotDurable { public TestPerson Value { get; set; } } // Not IDurableValue<T>

public interface IMyGrain : Orleans.IGrainWithGuidKey { Task TestMethod(); }

public class MyGrain : Orleans.Grain, IMyGrain
{
    private readonly NotDurable _state = new();
    public async Task TestMethod()
    {
";
            var code = setup + @"
        _state.Value.Age = 5; // Should not warn
        await Task.CompletedTask;
    }
}
";
            return Verify.VerifyAnalyzerAsync(code);
        }
    }
}
