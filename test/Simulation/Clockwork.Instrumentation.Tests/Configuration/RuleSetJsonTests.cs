using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Orchestration;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Runtime.Policy;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Configuration;

/// <summary>
/// Verifies the strict rule-set JSON loader: valid documents parse to the expected rule model,
/// round-tripping is signature-stable, and every class of malformed document fails with a
/// <see cref="ConfigurationException"/> rather than silently producing a degenerate rule set.
/// </summary>
public sealed class RuleSetJsonTests
{
    private const string ValidDocument = """
        {
          "schemaVersion": 2,
          "id": "clockwork.example",
          "version": "1.0",
          "rules": [
            {
              "id": "redirect-utcnow",
              "operation": "RedirectCall",
              "target": { "type": "System.DateTime", "member": "get_UtcNow", "parameters": [] },
              "replacement": { "assembly": "Shims", "type": "Shims.Clock", "member": "UtcNow", "parameters": [] },
              "policy": "Controlled",
              "supportedRuntimes": { "min": "10.0", "max": null },
              "description": "redirect the clock"
            },
            {
              "id": "substitute-marker",
              "operation": "SubstituteType",
              "target": { "type": "App.LegacyMarker" },
              "replacement": { "assembly": "Shims", "type": "Shims.ModernMarker" }
            }
          ]
        }
        """;

    [Fact]
    public void ParsesValidDocument()
    {
        RewriteRuleSet ruleSet = RuleSetJson.Parse(ValidDocument);

        Assert.Equal("clockwork.example", ruleSet.Id);
        Assert.Equal("1.0", ruleSet.Version);
        Assert.Equal(2, ruleSet.Rules.Length);

        RewriteRule redirect = ruleSet.Rules[0];
        Assert.Equal("redirect-utcnow", redirect.Id);
        Assert.Equal(RewriteOperationKind.RedirectCall, redirect.Operation);
        Assert.Equal("System.DateTime", redirect.Target.DeclaringTypeFullName);
        Assert.Equal("get_UtcNow", redirect.Target.MemberName);
        Assert.False(redirect.Target.ParameterTypeFullNames.IsDefault);
        Assert.Empty(redirect.Target.ParameterTypeFullNames);
        Assert.Equal("Shims", redirect.Replacement.AssemblyName);
        Assert.Equal(new Version(10, 0), redirect.SupportedRuntimes.Minimum);

        RewriteRule substitute = ruleSet.Rules[1];
        Assert.Equal(RewriteOperationKind.SubstituteType, substitute.Operation);
        Assert.True(substitute.Target.IsTypeOnly);
        Assert.True(substitute.Replacement.IsTypeOnly);
        Assert.Equal(SimulationApiPolicy.Controlled, substitute.Policy);
    }

    [Fact]
    public void OmittedParametersMatchAnyOverload()
    {
        const string doc = """
            {
              "id": "s", "version": "1",
              "rules": [{
                "id": "r", "operation": "RedirectCall",
                "target": { "type": "T", "member": "M" },
                "replacement": { "assembly": "A", "type": "R", "member": "M" }
              }]
            }
            """;

        RewriteRule rule = RuleSetJson.Parse(doc).Rules[0];
        Assert.True(rule.Target.ParameterTypeFullNames.IsDefault);
        Assert.False(rule.Target.HasParameterConstraint);
    }

    [Fact]
    public void RoundTripIsSignatureStable()
    {
        RewriteRuleSet original = RuleSetJson.Parse(ValidDocument);
        string written = RuleSetJson.Write(original);
        RewriteRuleSet reparsed = RuleSetJson.Parse(written);

        Assert.Equal(original.ComputeSignature(), reparsed.ComputeSignature());
        Assert.Equal(written, RuleSetJson.Write(reparsed));
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("""{ "id": "s", "version": "1" }""", "'rules' array")]
    [InlineData("""{ "version": "1", "rules": [] }""", "required property 'id'")]
    [InlineData("""{ "id": "s", "version": "1", "rules": [ { "id": "r", "operation": "Nope", "target": {"type":"T","member":"M"}, "replacement": {"assembly":"A","type":"R","member":"M"} } ] }""", "not one of")]
    [InlineData("""{ "id": "s", "version": "1", "rules": [ { "id": "r", "operation": "RedirectCall", "target": {"type":"T","member":"M"}, "replacement": {"assembly":"A","type":"R","member":"M"}, "supportedRuntimes": { "max": "11.0.2" } } ] }""", "not a runtime feature version")]
    public void RejectsMalformedDocuments(string json, string expectedFragment)
    {
        ConfigurationException ex = Assert.Throws<ConfigurationException>(() => RuleSetJson.Parse(json));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BaseCallReplacementRoundTrips()
    {
        const string doc = """
            {
              "id": "s", "version": "1",
              "rules": [
                { "id": "r", "operation": "RedirectCall", "target": {"type":"T","member":"M"},
                  "replacement": {"assembly":"A","type":"R","member":"M"},
                  "baseCallReplacement": {"assembly":"A","type":"R","member":"BaseM","parameters":["T"]} }
              ]
            }
            """;

        RewriteRuleSet original = RuleSetJson.Parse(doc);
        RewriteRuleSet reparsed = RuleSetJson.Parse(RuleSetJson.Write(original));

        RewriteReplacement baseCall = Assert.Single(reparsed.Rules).BaseCallReplacement!.Value;
        Assert.Equal("BaseM", baseCall.MemberName);
        Assert.Equal(["T"], baseCall.ParameterTypeFullNames);
        Assert.Equal(original.ComputeSignature(), reparsed.ComputeSignature());
    }

    [Fact]
    public void RejectsABaseCallReplacementOnARuleOtherThanARedirectCall()
    {
        const string doc = """
            {
              "id": "s", "version": "1",
              "rules": [
                { "id": "r", "operation": "WrapAfterCall", "target": {"type":"T","member":"M"},
                  "replacement": {"assembly":"A","type":"R","member":"M"},
                  "baseCallReplacement": {"assembly":"A","type":"R","member":"BaseM"} }
              ]
            }
            """;

        ConfigurationException ex = Assert.Throws<ConfigurationException>(() => RuleSetJson.Parse(doc));
        Assert.Contains("baseCallReplacement", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new RewriteRuleSet("s", "1", [
            RewriteRule.WrapAfterCall("r", MemberSignature.Method("T", "M"), new RewriteReplacement("A", "R", "M")) with
            {
                BaseCallReplacement = new RewriteReplacement("A", "R", "BaseM"),
            },
        ]));
    }

    [Fact]
    public void RejectsDuplicateRuleIds()
    {
        const string doc = """
            {
              "id": "s", "version": "1",
              "rules": [
                { "id": "dup", "operation": "RedirectCall", "target": {"type":"T","member":"M"}, "replacement": {"assembly":"A","type":"R","member":"M"} },
                { "id": "dup", "operation": "RedirectCall", "target": {"type":"T","member":"N"}, "replacement": {"assembly":"A","type":"R","member":"N"} }
              ]
            }
            """;

        ConfigurationException ex = Assert.Throws<ConfigurationException>(() => RuleSetJson.Parse(doc));
        Assert.Contains("dup", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsTypeOperationWithMember()
    {
        const string doc = """
            {
              "id": "s", "version": "1",
              "rules": [{ "id": "r", "operation": "SubstituteType", "target": {"type":"T","member":"M"}, "replacement": {"assembly":"A","type":"R"} }]
            }
            """;

        Assert.Throws<ConfigurationException>(() => RuleSetJson.Parse(doc));
    }

    [Fact]
    public void RejectsMemberOperationWithoutMember()
    {
        const string doc = """
            {
              "id": "s", "version": "1",
              "rules": [{ "id": "r", "operation": "RedirectCall", "target": {"type":"T"}, "replacement": {"assembly":"A","type":"R","member":"M"} }]
            }
            """;

        Assert.Throws<ConfigurationException>(() => RuleSetJson.Parse(doc));
    }

    [Fact]
    public void RejectsTargetWithAssembly()
    {
        const string doc = """
            {
              "id": "s", "version": "1",
              "rules": [{ "id": "r", "operation": "RedirectCall", "target": {"assembly":"X","type":"T","member":"M"}, "replacement": {"assembly":"A","type":"R","member":"M"} }]
            }
            """;

        ConfigurationException ex = Assert.Throws<ConfigurationException>(() => RuleSetJson.Parse(doc));
        Assert.Contains("must not specify an 'assembly'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fallback", "\"fallback\":\"Fail\"", "unknown property 'fallback'")]
    [InlineData("pass-through policy", "\"policy\":\"PassThrough\"", "'policy' value 'PassThrough' is not one of")]
    public void RejectsRemovedRuleFieldsAndValues(string name, string property, string expectedFragment)
    {
        _ = name;
        string doc = $$"""
            {
              "id": "s", "version": "1",
              "rules": [{
                "id": "r", "operation": "RedirectCall",
                "target": {"type":"T","member":"M"},
                "replacement": {"assembly":"A","type":"R","member":"M"},
                {{property}}
              }]
            }
            """;

        ConfigurationException ex = Assert.Throws<ConfigurationException>(() => RuleSetJson.Parse(doc));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriterOmitsRemovedFallbackField()
    {
        string written = RuleSetJson.Write(RuleSetJson.Parse(ValidDocument));

        Assert.DoesNotContain("fallback", written, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"schemaVersion\": 2", written, StringComparison.Ordinal);
    }

    [Fact]
    public void MaximumLengthRuleIdentifiersAreAccepted()
    {
        string identifier = new('i', ClosureManifestLimits.MaxStringLength);
        string document = $$"""
            {
              "id": {{System.Text.Json.JsonSerializer.Serialize(identifier)}},
              "version": "1",
              "rules": [{
                "id": {{System.Text.Json.JsonSerializer.Serialize(identifier)}},
                "operation": "RedirectCall",
                "target": {"type":"T","member":"M"},
                "replacement": {"assembly":"A","type":"R","member":"M"}
              }]
            }
            """;

        RewriteRuleSet ruleSet = RuleSetJson.Parse(document);

        Assert.Equal(identifier, ruleSet.Id);
        Assert.Equal(identifier, Assert.Single(ruleSet.Rules).Id);
    }

    [Theory]
    [InlineData("ruleSet")]
    [InlineData("rule")]
    public void OverLimitRuleIdentifierIsRejectedAtAcceptance(string identifierKind)
    {
        string valid = "valid";
        string overLimit = new('i', ClosureManifestLimits.MaxStringLength + 1);
        string ruleSetId = identifierKind == "ruleSet" ? overLimit : valid;
        string ruleId = identifierKind == "rule" ? overLimit : valid;
        string document = $$"""
            {
              "id": {{System.Text.Json.JsonSerializer.Serialize(ruleSetId)}},
              "version": "1",
              "rules": [{
                "id": {{System.Text.Json.JsonSerializer.Serialize(ruleId)}},
                "operation": "RedirectCall",
                "target": {"type":"T","member":"M"},
                "replacement": {"assembly":"A","type":"R","member":"M"}
              }]
            }
            """;

        ConfigurationException exception =
            Assert.Throws<ConfigurationException>(() => RuleSetJson.Parse(document));

        Assert.Contains("length", exception.Message, StringComparison.Ordinal);
        Assert.Contains("exceeds", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgrammaticRuleSetRejectsOverLimitIdentifier()
    {
        string overLimit = new('i', ClosureManifestLimits.MaxStringLength + 1);

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new RewriteRuleSet(overLimit, "1", []));

        Assert.Contains("Identifier length", exception.Message, StringComparison.Ordinal);
    }
}
