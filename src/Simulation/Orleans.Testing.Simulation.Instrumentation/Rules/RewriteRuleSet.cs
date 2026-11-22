using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Orleans.Testing.Simulation.Instrumentation.Orchestration;

namespace Orleans.Testing.Simulation.Instrumentation.Rules;

/// <summary>
/// A versioned, immutable collection of <see cref="RewriteRule"/> values applied together by the
/// engine. A rule set has a stable <see cref="Id"/> and <see cref="Version"/>, and can compute a
/// deterministic content <see cref="ComputeSignature"/> used for the engine's idempotence marker:
/// re-running with the same signature is a verified no-op, while a different signature is detected
/// as an incompatible rewrite.
/// </summary>
public sealed class RewriteRuleSet
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RewriteRuleSet"/> class.
    /// </summary>
    /// <param name="id">The stable identity of the rule set.</param>
    /// <param name="version">The version of the rule set.</param>
    /// <param name="rules">The rules, applied in order (first matching rule wins at a site).</param>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="rules"/> contains duplicate ids, or a rule other than a <c>RedirectCall</c> carries a
    /// <see cref="RewriteRule.BaseCallReplacement"/>.
    /// </exception>
    public RewriteRuleSet(string id, string version, IEnumerable<RewriteRule> rules)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(rules);
        ValidateIdentifier(id, nameof(id));
        ValidateIdentifier(version, nameof(version));

        Id = id;
        Version = version;
        Rules = [.. rules];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (RewriteRule rule in Rules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            ValidateIdentifier(rule.Id, $"{nameof(rules)} rule id");
            if (!seen.Add(rule.Id))
            {
                throw new ArgumentException($"Duplicate rule id '{rule.Id}' in rule set '{id}'.", nameof(rules));
            }

            if (rule.BaseCallReplacement is not null && rule.Operation != RewriteOperationKind.RedirectCall)
            {
                throw new ArgumentException(
                    $"Rule '{rule.Id}' has a base-call replacement, which only a RedirectCall rule applies.",
                    nameof(rules));
            }
        }
    }

    /// <summary>Gets the stable identity of the rule set.</summary>
    public string Id { get; }

    /// <summary>Gets the version of the rule set.</summary>
    public string Version { get; }

    /// <summary>Gets the rules, in application order.</summary>
    public ImmutableArray<RewriteRule> Rules { get; }

    /// <summary>Creates a fluent builder for a rule set with the given id and version.</summary>
    public static RewriteRuleSetBuilder CreateBuilder(string id, string version) => new(id, version);

    /// <summary>
    /// Computes a stable SHA-256 content signature over the rule set's id, version, and ordered
    /// rules. Independent of process, culture, and run, so the same rule set always yields the same
    /// signature.
    /// </summary>
    public string ComputeSignature()
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(ToCanonicalString()));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Returns a stable, unambiguous canonical encoding of the whole rule set.</summary>
    public string ToCanonicalString()
    {
        var canonical = new CanonicalEncoding(nameof(RewriteRuleSet));
        canonical.AddString(nameof(Id), Id);
        canonical.AddString(nameof(Version), Version);
        canonical.AddStringSequence(
            nameof(Rules),
            Rules.Select(static rule => rule.ToCanonicalString()));
        return canonical.ToString();
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (value.Length > ClosureManifestLimits.MaxStringLength)
        {
            throw new ArgumentException(
                $"Identifier length {value.Length} exceeds {ClosureManifestLimits.MaxStringLength}.",
                parameterName);
        }
    }
}
