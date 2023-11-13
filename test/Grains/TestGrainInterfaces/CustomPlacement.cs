using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.ICustomPlacementTestGrain")]
    public interface ICustomPlacementTestGrain : IGrainWithGuidKey
    {
        [Alias("GetRuntimeInstanceId")]
        Task<string> GetRuntimeInstanceId();
    }

    [Alias("UnitTests.GrainInterfaces.IHashBasedPlacementGrain")]
    public interface IHashBasedPlacementGrain : IGrainWithGuidKey
    {
        [Alias("GetSiloAddress")]
        Task<SiloAddress> GetSiloAddress();
    }


    public enum CustomPlacementScenario
    {
        FixedSilo,
        ExcludeOne,
        RequestContextBased,
    }

    [Serializable]
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.TestCustomPlacementStrategy")]
    public class TestCustomPlacementStrategy : PlacementStrategy
    {
        private const string ScenarioKey = "test-placement-scenario";

        [Id(0)]
        public CustomPlacementScenario Scenario { get; private set; }

        public static TestCustomPlacementStrategy FixedSilo { get; } = new TestCustomPlacementStrategy(CustomPlacementScenario.FixedSilo);
        public static TestCustomPlacementStrategy ExcludeOne { get; } = new TestCustomPlacementStrategy(CustomPlacementScenario.ExcludeOne);
        public static TestCustomPlacementStrategy RequestContextBased { get; } = new TestCustomPlacementStrategy(CustomPlacementScenario.RequestContextBased);

        internal TestCustomPlacementStrategy(CustomPlacementScenario scenario)
        {
            Scenario = scenario;
        }

        public TestCustomPlacementStrategy() { }

        public override void Initialize(GrainProperties properties)
        {
            base.Initialize(properties);
            CustomPlacementScenario result;
            if (properties.Properties.TryGetValue(ScenarioKey, out var value) && Enum.TryParse(value, out result))
            {
                this.Scenario = result;
            }
        }

        public override void PopulateGrainProperties(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            base.PopulateGrainProperties(services, grainClass, grainType, properties);
            properties[ScenarioKey] = this.Scenario.ToString();
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class TestPlacementStrategyAttribute : PlacementAttribute
    {
        public CustomPlacementScenario Scenario { get; private set; }

        public TestPlacementStrategyAttribute(CustomPlacementScenario scenario) :
            base(GetCustomPlacementStrategy(scenario))
        {
            Scenario = scenario;
        }

        private static TestCustomPlacementStrategy GetCustomPlacementStrategy(CustomPlacementScenario scenario)
        {
            switch (scenario)
            {
                case CustomPlacementScenario.FixedSilo:
                    return TestCustomPlacementStrategy.FixedSilo;
                case CustomPlacementScenario.ExcludeOne:
                    return TestCustomPlacementStrategy.ExcludeOne;
                case CustomPlacementScenario.RequestContextBased:
                    return TestCustomPlacementStrategy.RequestContextBased;
                default:
                    throw new Exception("Unknown CustomPlacementScenario");
            }
        }
    }
}
