using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Runtime.Policy;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

/// <summary>How the rewriter treats a call to a reference-pack member under every built-in rule.</summary>
internal enum RuleOutcome
{
    /// <summary>No rule rewrites the call.</summary>
    None,

    /// <summary>A rule redirects or wraps the call.</summary>
    Controlled,

    /// <summary>A rule rejects the call.</summary>
    Rejected,

    /// <summary>
    /// A rule rewrites a <c>newobj</c> of the constructor, but not a <c>call</c> to it: a derived type's constructor
    /// chaining to it, or a value type constructed in place.
    /// </summary>
    NewObjOnly,

    /// <summary>The declaring type is substituted, and its substitute declares the member.</summary>
    Substituted,

    /// <summary>The member-aware pass rebuilds the awaiter the call returns.</summary>
    AwaiterSource,
}

/// <summary>The outcome of a call to a member, with the rule that decides it.</summary>
/// <param name="Outcome">How the rewriter treats the call.</param>
/// <param name="RuleId">The id of the deciding rule, if any.</param>
internal readonly record struct RuleClassification(RuleOutcome Outcome, string? RuleId)
{
    /// <summary>Gets the classification of a call no rule rewrites.</summary>
    public static RuleClassification None { get; } = new(RuleOutcome.None, null);

    public override string ToString() => RuleId is null ? Outcome.ToString() : $"{Outcome} by {RuleId}";
}

/// <summary>
/// Classifies reference-pack members with the rewriter's own rule matcher and substitution mapper over every built-in
/// rule, so the classification agrees with what a rewrite does. Like <see cref="ReferencePack"/>, it is shared by the
/// tests of <see cref="ReferencePackGroup"/> and lives for the test run.
/// </summary>
internal sealed class BuiltInRuleClassifier
{
    private static readonly Lazy<BuiltInRuleClassifier> Instance = new(() => new BuiltInRuleClassifier(ReferencePack.Shared));

    // The receivers whose GetAwaiter and ConfigureAwait calls the member-aware pass rebuilds, as it gates them.
    private static readonly HashSet<string> AwaiterSourceReceivers = new(StringComparer.Ordinal)
    {
        "System.Threading.Tasks.Task",
        "System.Threading.Tasks.Task`1",
        "System.Threading.Tasks.ValueTask",
        "System.Threading.Tasks.ValueTask`1",
    };

    private readonly RewriteRuleMatcher _matcher;
    private readonly MemberSubstitutionMapper _mapper;

    private BuiltInRuleClassifier(ReferencePack pack)
    {
        Rules = [.. BuiltInRuleSets.DeterministicBclInventory.Concat(BuiltInRuleSets.ControlledTasksInventory).Select(entry => entry.Rule)];
        _matcher = new RewriteRuleMatcher(RuleSetRuntimeSupport.Evaluate(new RewriteRuleSet("clockwork.classification", "1.0.0", Rules), BuiltInRuleSets.SupportedRuntime));
        SubstitutedTypes = _matcher.TypeSubstitutionRules.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);

        // The mapper imports each substitute into a probe module, which resolves the shim assembly from the test binaries.
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(pack.Directory);
        resolver.AddSearchDirectory(AppContext.BaseDirectory);
        ModuleDefinition probe = ModuleDefinition.CreateModule(
            "Orleans.Testing.Simulation.Classification",
            new ModuleParameters { Kind = ModuleKind.Dll, AssemblyResolver = resolver });
        var replacements = new ReplacementResolver(
            [Path.Combine(AppContext.BaseDirectory, BuiltInRuleSets.ShimAssemblyName + ".dll")],
            [pack.Directory]);
        var session = new RewriteSession(probe, _matcher, replacements, new HashSet<string>(StringComparer.Ordinal), []);
        _mapper = MemberSubstitutionMapper.Build(session, (rule, error) => Assert.Fail($"Substitution rule '{rule.Id}' does not resolve: {error}"));
    }

    /// <summary>Gets the classifier over every built-in rule.</summary>
    public static BuiltInRuleClassifier Shared => Instance.Value;

    /// <summary>Gets every built-in rule, in rule-set order.</summary>
    public IReadOnlyList<RewriteRule> Rules { get; }

    /// <summary>Gets the Cecil full names of the substituted types.</summary>
    public IReadOnlySet<string> SubstitutedTypes { get; }

    /// <summary>
    /// Returns whether the member-aware pass rebuilds a call to <paramref name="method"/>, a task or value task's
    /// <c>GetAwaiter</c> or <c>ConfigureAwait</c>, as a controlled awaiter or awaitable built from the receiver.
    /// </summary>
    public bool IsAwaiterSource(MethodDefinition method) =>
        method.Name is "GetAwaiter" or "ConfigureAwait"
        && AwaiterSourceReceivers.Contains(method.DeclaringType.FullName)
        && _mapper.BuildAwaiterSourceConstructor(ReferencePack.CallSite(method)) is not null;

    /// <summary>Classifies a call to <paramref name="member"/>.</summary>
    public RuleClassification Classify(IMemberDefinition member)
    {
        TypeDefinition declaringType = member.DeclaringType;
        if (SubstitutedTypes.Contains(declaringType.FullName))
        {
            bool mapped = member switch
            {
                MethodDefinition method => _mapper.MapMethod(method).Kind == MemberMappingKind.Mapped,
                FieldDefinition field => _mapper.OpenSubstituteFor(declaringType.FullName)!.Resolve().Fields
                    .Any(candidate => candidate.Name == field.Name && ReferencePack.IsVisible(candidate)),
                _ => throw new ArgumentException($"'{member.FullName}' is neither a method nor a field.", nameof(member)),
            };
            return mapped ? new RuleClassification(RuleOutcome.Substituted, _mapper.RuleIdFor(declaringType.FullName)) : RuleClassification.None;
        }

        if (member is not MethodDefinition invoked)
        {
            return RuleClassification.None;
        }

        RuleClassification matched = Match(invoked, isNewObj: invoked.IsConstructor);

        // A derived type's constructor chains to its base constructor, and a value type is constructed in place, with a
        // call, which only a rule matching calls rewrites. A sealed reference type has neither.
        if (invoked.IsConstructor
            && matched.Outcome != RuleOutcome.None
            && (!declaringType.IsSealed || declaringType.IsValueType)
            && Match(invoked, isNewObj: false).Outcome == RuleOutcome.None)
        {
            return new RuleClassification(RuleOutcome.NewObjOnly, matched.RuleId);
        }

        if (matched.Outcome != RuleOutcome.None)
        {
            return matched;
        }

        return IsAwaiterSource(invoked) ? new RuleClassification(RuleOutcome.AwaiterSource, null) : RuleClassification.None;
    }

    private RuleClassification Match(MethodDefinition method, bool isNewObj)
    {
        if (!_matcher.TryMatchInvocation(ReferencePack.CallSite(method), isNewObj, out RewriteRule rule))
        {
            return RuleClassification.None;
        }

        return new RuleClassification(rule.Policy == SimulationApiPolicy.Rejected ? RuleOutcome.Rejected : RuleOutcome.Controlled, rule.Id);
    }
}
