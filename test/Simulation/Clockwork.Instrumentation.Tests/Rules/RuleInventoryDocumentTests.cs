using System.Reflection;
using System.Text.RegularExpressions;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Rules;

/// <summary>
/// Verifies the committed rule inventory matches <see cref="RuleInventoryDocument.Render"/> and guards the per-family
/// surfaces it describes.
/// </summary>
public sealed class RuleInventoryDocumentTests
{
    [Fact]
    public void CommittedInventoryMatchesGeneratedContent()
    {
        string expected = RuleInventoryDocument.Render();
        string path = Repository.PathOf("src", "Simulation", "docs", "rule-inventory.md");

        if (Environment.GetEnvironmentVariable("ORLEANS_SIMULATION_UPDATE_DOCS") == "1")
        {
            File.WriteAllText(path, expected);
        }

        string refresh =
            $"Refresh '{path}' with `$env:ORLEANS_SIMULATION_UPDATE_DOCS='1'; dotnet run --project test/Simulation/Clockwork.Instrumentation.Tests/Clockwork.Instrumentation.Tests.csproj -- --filter-class {typeof(RuleInventoryDocumentTests).FullName}`.";
        Assert.True(File.Exists(path), refresh);
        string[] expectedLines = expected.Split('\n');
        string[] actualLines = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int line = Enumerable.Range(0, Math.Max(expectedLines.Length, actualLines.Length))
            .FirstOrDefault(index => index >= expectedLines.Length || index >= actualLines.Length || expectedLines[index] != actualLines[index], -1);
        Assert.True(line < 0, $"'{path}' differs from the rendered inventory at line {line + 1}. {refresh}");
    }

    [Fact]
    public void InventoryCoversEveryShippedRule()
    {
        string rendered = RuleInventoryDocument.Render();
        foreach ((_, RewriteRule rule) in BuiltInRuleSets.DeterministicBclInventory)
        {
            Assert.Contains(rule.Id, rendered, StringComparison.Ordinal);
        }

        foreach ((_, RewriteRule rule) in BuiltInRuleSets.ControlledTasksInventory)
        {
            Assert.Contains(rule.Id, rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TaskDelayRulesClassifyEveryNet11Overload()
    {
        string[] frameworkOverloads = typeof(Task)
            .GetMethods()
            .Where(method => method.IsStatic && method.Name == nameof(Task.Delay))
            .Select(method => string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.FullName)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] classifiedOverloads = BuiltInRuleSets.ControlledTasksInventory
            .Where(entry => entry.Family == BuiltInRuleFamily.TaskTime && entry.Rule.Target.MemberName == "Delay")
            .Select(entry => string.Join(",", entry.Rule.Target.ParameterTypeFullNames))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(frameworkOverloads, classifiedOverloads);
        Assert.All(
            BuiltInRuleSets.ControlledTasksInventory.Where(
                entry => entry.Family == BuiltInRuleFamily.TaskTime && entry.Rule.Target.MemberName == "Delay"),
            entry => Assert.Equal(Orleans.Testing.Simulation.Runtime.Policy.SimulationApiPolicy.Controlled, entry.Rule.Policy));
    }

    [Fact]
    public void TaskWaitAsyncRulesClassifyEveryNet11Overload()
    {
        int frameworkOverloads =
            typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Count(method => method.Name == nameof(Task.WaitAsync)) +
            typeof(Task<>).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Count(method => method.Name == nameof(Task.WaitAsync));
        RewriteRule[] classified = BuiltInRuleSets.ControlledTasksInventory
            .Where(entry => entry.Family == BuiltInRuleFamily.TaskTime && entry.Rule.Target.MemberName == "WaitAsync")
            .Select(entry => entry.Rule)
            .ToArray();

        Assert.Equal(10, frameworkOverloads);
        Assert.Equal(frameworkOverloads, classified.Length);
        Assert.All(classified, rule =>
            Assert.Equal(Orleans.Testing.Simulation.Runtime.Policy.SimulationApiPolicy.Controlled, rule.Policy));
    }

    [Fact]
    public void TimerTypesUseControlledWholeTypeSubstitutions()
    {
        (string Target, string Replacement)[] expected =
        [
            ("System.Threading.Timer", "Orleans.Testing.Simulation.Shims.System.Threading.ControlledTimer"),
            ("System.Timers.Timer", "Orleans.Testing.Simulation.Shims.System.Timers.ControlledTimer"),
            ("System.Threading.PeriodicTimer", "Orleans.Testing.Simulation.Shims.System.Threading.ControlledPeriodicTimer"),
        ];

        foreach ((string target, string replacement) in expected)
        {
            RewriteRule rule = Assert.Single(BuiltInRuleSets.ControlledTasksInventory
                .Where(entry => entry.Family == BuiltInRuleFamily.Timers)
                .Select(entry => entry.Rule),
                rule => rule.Target.DeclaringTypeFullName == target);
            Assert.Equal(RewriteOperationKind.SubstituteType, rule.Operation);
            Assert.Equal(replacement, rule.Replacement.DeclaringTypeFullName);
        }
    }

    [Fact]
    public void TaskFactoryRulesClassifyEveryNet11StartNewOverload()
    {
        int frameworkOverloadCount =
            typeof(TaskFactory).GetMethods().Count(method => method.Name == nameof(TaskFactory.StartNew)) +
            typeof(TaskFactory<>).GetMethods().Count(method => method.Name == nameof(TaskFactory.StartNew));
        RewriteRule[] classified = BuiltInRuleSets.ControlledTasksInventory
            .Where(entry => entry.Family == BuiltInRuleFamily.TaskFactory)
            .Select(entry => entry.Rule)
            .ToArray();

        Assert.Equal(24, frameworkOverloadCount);
        Assert.Equal(frameworkOverloadCount, classified.Length);
        Assert.All(classified, rule =>
            Assert.Equal(Orleans.Testing.Simulation.Runtime.Policy.SimulationApiPolicy.Controlled, rule.Policy));
    }

    [Fact]
    public void ContinueWithRulesClassifyEveryNet11Overload()
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        int frameworkOverloads =
            typeof(Task).GetMethods(Declared).Count(method => method.Name == nameof(Task.ContinueWith)) +
            typeof(Task<>).GetMethods(Declared).Count(method => method.Name == nameof(Task.ContinueWith));
        RewriteRule[] classified = BuiltInRuleSets.ControlledTasksInventory
            .Where(entry => entry.Family == BuiltInRuleFamily.TaskContinuations)
            .Select(entry => entry.Rule)
            .ToArray();

        Assert.Equal(40, frameworkOverloads);
        Assert.Equal(frameworkOverloads, classified.Length);
        Assert.Equal(classified.Length, classified.Select(rule => rule.Target.ToCanonicalString()).Distinct().Count());
        Assert.All(classified, rule =>
        {
            Assert.Equal("ContinueWith", rule.Target.MemberName);
            Assert.Equal(Orleans.Testing.Simulation.Runtime.Policy.SimulationApiPolicy.Controlled, rule.Policy);
        });
    }

    [Fact]
    public void CompletionSourceRulesClassifyEveryNet11CompletionMember()
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        string[] Completions(Type type) =>
        [
            .. type.GetMethods(Declared)
                .Where(method => method.Name.StartsWith("Set", StringComparison.Ordinal)
                    || method.Name.StartsWith("TrySet", StringComparison.Ordinal))
                .Select(method => type.Name + "::" + method.Name + "(" + method.GetParameters().Length + ")")
                .Order(StringComparer.Ordinal),
        ];

        string[] framework =
        [
            .. Completions(typeof(TaskCompletionSource)),
            .. Completions(typeof(TaskCompletionSource<>)),
            .. Completions(typeof(System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<>)),
        ];
        string[] classified =
        [
            .. BuiltInRuleSets.ControlledTasksInventory
                .Where(entry => entry.Family == BuiltInRuleFamily.TaskCompletionSource && entry.Rule.Operation == RewriteOperationKind.RedirectCall)
                .Select(entry => entry.Rule.Target)
                .Select(target => target.DeclaringTypeFullName.Split('.')[^1] + "::" + target.MemberName + "(" + target.ParameterTypeFullNames.Length + ")")
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(26, framework.Length);
        Assert.Equal(framework.Order(StringComparer.Ordinal), classified);
        Assert.DoesNotContain(
            BuiltInRuleSets.ControlledTasksInventory,
            entry => entry.Rule.Operation == RewriteOperationKind.SubstituteType
                && entry.Rule.Target.DeclaringTypeFullName.StartsWith("System.Threading.Tasks.TaskCompletionSource", StringComparison.Ordinal));
    }

    [Fact]
    public void ValueTaskConversionRulesClassifyAsTaskAndPreserve()
    {
        string[] classified =
        [
            .. BuiltInRuleSets.ControlledTasksInventory
                .Where(entry => entry.Family == BuiltInRuleFamily.ValueTaskConversions)
                .Select(entry => entry.Rule.Target.DeclaringTypeFullName + "::" + entry.Rule.Target.MemberName)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(
            [
                "System.Threading.Tasks.ValueTask::AsTask",
                "System.Threading.Tasks.ValueTask::Preserve",
                "System.Threading.Tasks.ValueTask`1::AsTask",
                "System.Threading.Tasks.ValueTask`1::Preserve",
            ],
            classified);
    }

    [Fact]
    public void WhenEachRulesClassifyEveryNet11Overload()
    {
        string[] framework =
        [
            .. typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == "WhenEach")
                .Select(method => (method.IsGenericMethod ? "generic " : string.Empty) + method.GetParameters()[0].ParameterType.Name)
                .Order(StringComparer.Ordinal),
        ];
        RewriteRule[] classified =
        [
            .. BuiltInRuleSets.ControlledTasksInventory
                .Where(entry => entry.Family == BuiltInRuleFamily.TaskCombinators && entry.Rule.Target.MemberName == "WhenEach")
                .Select(entry => entry.Rule),
        ];

        Assert.Equal(6, framework.Length);
        Assert.Equal(framework.Length, classified.Length);
        Assert.Equal(classified.Length, classified.Select(rule => rule.Target.ToCanonicalString()).Distinct().Count());
        Assert.All(classified, rule => Assert.Equal(Orleans.Testing.Simulation.Runtime.Policy.SimulationApiPolicy.Controlled, rule.Policy));
    }

    [Fact]
    public void MultiTaskContinuationRulesClassifyEveryNet11Overload()
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        string[] Shapes(Type factory) =>
        [
            .. factory.GetMethods(Declared)
                .Where(method => method.Name is "ContinueWhenAll" or "ContinueWhenAny")
                .Select(method => factory.Name + "::" + method.Name + "`" + (method.IsGenericMethod ? method.GetGenericArguments().Length : 0) +
                    "(" + string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.Name)) + ")"),
        ];
        string[] framework = [.. Shapes(typeof(TaskFactory)), .. Shapes(typeof(TaskFactory<>))];
        RewriteRule[] classified =
        [
            .. BuiltInRuleSets.ControlledTasksInventory
                .Where(entry => entry.Family == BuiltInRuleFamily.TaskFactoryContinuations)
                .Select(entry => entry.Rule),
        ];

        Assert.Equal(48, framework.Length);
        Assert.Equal(framework.Length, framework.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(framework.Length, classified.Length);
        Assert.Equal(classified.Length, classified.Select(rule => rule.Target.ToCanonicalString()).Distinct().Count());
        Assert.All(classified, rule =>
        {
            Assert.True(rule.Target.MemberName is "ContinueWhenAll" or "ContinueWhenAny", rule.Id);
            Assert.Equal(Orleans.Testing.Simulation.Runtime.Policy.SimulationApiPolicy.Controlled, rule.Policy);
        });
    }

    [Fact]
    public void FromAsyncRulesRejectEveryOverloadOfBothFactories()
    {
        RewriteRule[] classified =
        [
            .. BuiltInRuleSets.ControlledTasksInventory
                .Where(entry => entry.Family == BuiltInRuleFamily.TaskFactoryFromAsync)
                .Select(entry => entry.Rule),
        ];

        Assert.Equal(
            ["System.Threading.Tasks.TaskFactory", "System.Threading.Tasks.TaskFactory`1"],
            classified.Select(rule => rule.Target.DeclaringTypeFullName).Order(StringComparer.Ordinal));
        Assert.All(classified, rule =>
        {
            Assert.Equal("FromAsync", rule.Target.MemberName);
            Assert.False(rule.Target.HasParameterConstraint);
            Assert.Equal(RewriteOperationKind.InjectRejection, rule.Operation);
            Assert.Equal(Orleans.Testing.Simulation.Runtime.Policy.SimulationApiPolicy.Rejected, rule.Policy);
        });
        Assert.Contains(typeof(TaskFactory).GetMethods(), method => method.Name == "FromAsync");
        Assert.Contains(typeof(TaskFactory<>).GetMethods(), method => method.Name == "FromAsync");
    }

    [Fact]
    public void DocumentedHolesDoNotNameFullyClassifiedFamilies()
    {
        string rendered = RuleInventoryDocument.Render();
        string holes = rendered[rendered.IndexOf("# Documented holes", StringComparison.Ordinal)..];

        Assert.DoesNotMatch(new Regex(@"\bTask\.Delay\b", RegexOptions.CultureInvariant), holes);
        Assert.DoesNotMatch(new Regex(@"\bTaskFactory\b", RegexOptions.CultureInvariant), holes);
        Assert.DoesNotMatch(new Regex(@"\bWhenEach\b", RegexOptions.CultureInvariant), holes);
        Assert.DoesNotMatch(new Regex(@"\bContinueWhen(All|Any)\b", RegexOptions.CultureInvariant), holes);
        Assert.DoesNotMatch(new Regex(@"\bFromAsync\b", RegexOptions.CultureInvariant), holes);
        Assert.DoesNotMatch(new Regex(@"\bMonitor\b", RegexOptions.CultureInvariant), holes);
        Assert.DoesNotMatch(new Regex(@"(?<!ReaderWriter)\bLock\b", RegexOptions.CultureInvariant), holes);
        Assert.DoesNotMatch(new Regex(@"\bSemaphoreSlim\b", RegexOptions.CultureInvariant), holes);
        Assert.DoesNotMatch(new Regex(@"\bReaderWriterLockSlim\b", RegexOptions.CultureInvariant), holes);
        Assert.DoesNotMatch(new Regex(@"\bManualResetEventSlim\b", RegexOptions.CultureInvariant), holes);
        Assert.DoesNotMatch(new Regex(@"\bSpinLock\b", RegexOptions.CultureInvariant), holes);
        Assert.DoesNotMatch(new Regex(@"\bBarrier\b", RegexOptions.CultureInvariant), holes);
        Assert.DoesNotMatch(new Regex(@"\bCountdownEvent\b", RegexOptions.CultureInvariant), holes);
    }

    [Fact]
    public void MonitorRulesClassifyEveryNet11Method()
    {
        string[] framework = typeof(Monitor)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(MethodShape)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] classified = BuiltInRuleSets.ControlledTasksInventory
            .Where(entry => entry.Family == BuiltInRuleFamily.Monitor)
            .Select(entry => entry.Rule.Target.MemberName + "(" +
                string.Join(",", entry.Rule.Target.ParameterTypeFullNames) + ")")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(framework, classified);
    }

    [Theory]
    [InlineData(typeof(Interlocked), BuiltInRuleFamily.Interlocked)]
    [InlineData(typeof(Volatile), BuiltInRuleFamily.Volatile)]
    public void AtomicRulesClassifyEveryNet11Method(Type type, BuiltInRuleFamily family)
    {
        string[] framework = type
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(method => method.Name + "(" + string.Join(",", method.GetParameters().Select(parameter => CallSiteName(parameter.ParameterType))) + ")")
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] classified = BuiltInRuleSets.ControlledTasksInventory
            .Where(entry => entry.Family == family)
            .Select(entry => entry.Rule.Target.MemberName + "(" +
                string.Join(",", entry.Rule.Target.ParameterTypeFullNames) + ")")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(framework, classified);
    }

    [Fact]
    public void SemaphoreSlimRulesClassifyEveryNet11DeclaredMember()
    {
        int frameworkMemberCount =
            typeof(SemaphoreSlim).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length +
            typeof(SemaphoreSlim).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Length;
        RewriteRule[] classified = BuiltInRuleSets.ControlledTasksInventory
            .Where(entry => entry.Family == BuiltInRuleFamily.Semaphore)
            .Select(entry => entry.Rule)
            .ToArray();

        Assert.Equal(19, frameworkMemberCount);
        Assert.Equal(frameworkMemberCount, classified.Length);
        Assert.Equal(classified.Length, classified.Select(rule => rule.Target.ToCanonicalString()).Distinct().Count());
    }

    [Fact]
    public void ModernSynchronizationReceiverFirstRulesClassifyEveryNet11DeclaredMember()
    {
        AssertFamilyMatchesPublicMembers(typeof(ReaderWriterLockSlim), BuiltInRuleFamily.ReaderWriterLockSlim, includeConstructors: true);
        AssertFamilyMatchesPublicMembers(typeof(ManualResetEventSlim), BuiltInRuleFamily.ManualResetEventSlim, includeConstructors: true);
        AssertFamilyMatchesPublicMembers(typeof(Mutex), BuiltInRuleFamily.Mutex, includeConstructors: true);
        AssertFamilyMatchesPublicMembers(typeof(Semaphore), BuiltInRuleFamily.KernelSemaphore, includeConstructors: true);
        AssertFamilyMatchesPublicMembers(typeof(ExecutionContext), BuiltInRuleFamily.ExecutionContext, includeConstructors: false);
        AssertFamilyMatchesPublicMembers(typeof(SynchronizationContext), BuiltInRuleFamily.SynchronizationContext, includeConstructors: false);
    }

    [Fact]
    public void ModernSynchronizationWholeTypeSubstitutionsUseControlledRuntimeTypes()
    {
        (string Target, string Replacement)[] expected =
        [
            ("System.Threading.SpinLock", "Orleans.Testing.Simulation.Shims.System.Threading.ControlledSpinLock"),
            ("System.Threading.Barrier", "Orleans.Testing.Simulation.Shims.System.Threading.ControlledBarrier"),
            ("System.Threading.CountdownEvent", "Orleans.Testing.Simulation.Shims.System.Threading.ControlledCountdownEvent"),
        ];

        foreach ((string target, string replacement) in expected)
        {
            RewriteRule rule = Assert.Single(BuiltInRuleSets.ControlledTasksInventory
                .Where(entry => entry.Rule.Target.DeclaringTypeFullName == target)
                .Select(entry => entry.Rule));
            Assert.Equal(RewriteOperationKind.SubstituteType, rule.Operation);
            Assert.Equal(replacement, rule.Replacement.DeclaringTypeFullName);
        }
    }

    private static void AssertFamilyMatchesPublicMembers(Type type, BuiltInRuleFamily family, bool includeConstructors)
    {
        IEnumerable<MethodBase> framework = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (includeConstructors)
        {
            framework = framework.Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        }

        string[] expected = framework.Select(MethodShape).Order(StringComparer.Ordinal).ToArray();
        string[] actual = BuiltInRuleSets.ControlledTasksInventory
            .Where(entry => entry.Family == family)
            .Select(entry => entry.Rule.Target.MemberName + "(" +
                string.Join(",", entry.Rule.Target.ParameterTypeFullNames) + ")")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);
    }

    private static string MethodShape(MethodBase method) =>
        method.Name + "(" + string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.FullName)) + ")";

    // The name a call site gives a parameter type, which is what rule targets match: a method's generic parameter is
    // positional (!!0).
    private static string CallSiteName(Type type) =>
        type.IsByRef ? CallSiteName(type.GetElementType()!) + "&"
        : type.IsGenericMethodParameter ? "!!" + type.GenericParameterPosition
        : type.FullName!;
}
