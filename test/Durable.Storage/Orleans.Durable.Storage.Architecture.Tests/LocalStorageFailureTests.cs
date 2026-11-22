using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.Durable.Storage.Diagnostics;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// Pins conventions.md §14: a failed local storage operation, or a defect which would leave a component's memory out of
/// step with its write-ahead log, stops the process. The storage seams alone call the platform's file-system APIs and
/// RocksDB, and alone report storage failures to <see cref="FatalErrorHandler"/>; the components which own a write-ahead
/// log alone report defects; one production site names each exit code; no catch of a storage project swallows or retries
/// a failure, or answers before it reports; and only the handler ends the process, which nothing else intercepts or
/// hooks. Each rule is shown to fire on a fixture which breaks it.
/// </summary>
public sealed class LocalStorageFailureTests
{
    private const string FixtureNamespace = "Orleans.Durable.Storage.Architecture.Tests.FailStopFixtures";

    /// <summary>
    /// The storage seams: the types which call the platform's file-system APIs or RocksDB, and report their failures.
    /// They are the WAL's file system with its files; the direct-I/O opens and the directory flushes which the file system
    /// and the io_uring storage call; the io_uring storage, its files, and its ring; the state database's RocksDB
    /// storage; the native consensus's SQLite stores, whose failures the consensus stores detect and report through their
    /// seam; and the file system's answers, which the seams share and which report nothing.
    /// </summary>
    private static readonly string[] Seams =
    [
        "Orleans.Durable.Storage.Engine.FileSystemAnswers",
        "Orleans.Durable.Storage.Engine.Wal.FileSystem.WalFileSystem",
        "Orleans.Durable.Storage.Engine.Wal.FileSystem.WalDirectIo",
        "Orleans.Durable.Storage.Engine.Wal.FileSystem.WalDirectorySync",
        "Orleans.Durable.Storage.Engine.Wal.IoUring.IoUringWalStorage",
        "Orleans.Durable.Storage.Engine.Wal.IoUring.IoUringEngine",
        "Orleans.Durable.Storage.Engine.Wal.IoUring.IoUringOperation",
        "Orleans.Durable.Storage.Engine.Wal.IoUring.IoUringInterop",
        "Orleans.Durable.Storage.RocksDb.RocksDbJournalIndex",
        "Orleans.Durable.Storage.CasPaxos.Hosting.ConsensusStorageFailures",
    ];

    /// <summary>
    /// Derived volatile caches explicitly exempted from fail-stop local storage reporting (conventions.md §14, OL-I11):
    /// each holds only verified copies of bytes the object store holds and discards failed reads or writes in place,
    /// falling back to the object store.
    /// </summary>
    private static readonly string[] DerivedCaches =
    [
        "Orleans.Durable.Storage.Engine.Ledgers.Objects.ObjectLedgerCache",
    ];

    /// <summary>
    /// The platform's types whose members read or change the file system.
    /// </summary>
    private static readonly HashSet<string> FileSystemTypes =
    [
        "System.IO.File",
        "System.IO.Directory",
        "System.IO.FileStream",
        "System.IO.RandomAccess",
        "System.IO.FileInfo",
        "System.IO.DirectoryInfo",
        "System.IO.FileSystemInfo",
        "System.IO.DriveInfo",
    ];

    /// <summary>
    /// The source directories, under <c>src</c>, of the components which own local storage, of the diagnostics' types,
    /// and of the Engine's shared helpers, whose catches the catch rule reads: the ledgers' quorum and memory provider
    /// reach storage only through replicas. The helpers are the files of the Engine's own directory, without its
    /// layers' subdirectories.
    /// </summary>
    private static readonly (string Directory, SearchOption Option)[] StorageSources =
    [
        (Components.Diagnostics, SearchOption.AllDirectories),
        (Components.Engine, SearchOption.TopDirectoryOnly),
        (Path.Combine(Components.Engine, "Ledgers", "Store"), SearchOption.AllDirectories),
        (Path.Combine(Components.Engine, "Wal"), SearchOption.AllDirectories),
        (Components.FileSystem, SearchOption.AllDirectories),
        (Components.RocksDb, SearchOption.AllDirectories),
    ];

    /// <summary>
    /// The exception types a catch of which could meet a failed local storage operation.
    /// </summary>
    private static readonly HashSet<string> BroadExceptions = ["Exception", "SystemException", "IOException", "UnauthorizedAccessException"];

    /// <summary>
    /// The types which report failed local storage operations: the seams which do, and the simulated storage, which stands
    /// in for them in tests and simulations.
    /// </summary>
    private static readonly string[] StorageReporters =
    [
        "Orleans.Durable.Storage.CasPaxos.Hosting.ConsensusStorageFailures",
        "Orleans.Durable.Storage.Engine.Wal.FileSystem.WalFileSystem",
        "Orleans.Durable.Storage.Engine.Wal.IoUring.IoUringEngine",
        "Orleans.Durable.Storage.Engine.Wal.IoUring.IoUringWalStorage",
        "Orleans.Durable.Storage.Engine.Wal.Simulation.SimulatedWalStorage",
        "Orleans.Durable.Storage.RocksDb.RocksDbJournalIndex",
    ];

    /// <summary>
    /// The types which report defects: the components whose memory follows a write-ahead log, storage, or a ledger's
    /// decided rows, and the object writers' one report of an object whose content changed after verification.
    /// </summary>
    private static readonly string[] DefectReporters =
    [
        "Orleans.Durable.Storage.Engine.Journals.LedgerAdmission",
        "Orleans.Durable.Storage.Engine.Ledgers.Store.LedgerStore",
        "Orleans.Durable.Storage.Engine.ObjectStore.ObjectContentChangedDefect",
        "Orleans.Durable.Storage.Engine.Wal.WriteAheadLog",
    ];

    /// <summary>
    /// The calls which report a caught failure to the fatal-error handler: a seam's, or a component's, directly or through
    /// its one stop helper.
    /// </summary>
    private static readonly HashSet<string> Reports = ["StopOnStorageFailure", "StopOnDefect", "Stop"];

    /// <summary>
    /// The calls which hand a caught failure on: to a task its waiter observes, or to a rethrow which a later await
    /// performs.
    /// </summary>
    private static readonly HashSet<string> Transfers = ["TrySetException", "SetException", "FromException", "Throw"];

    /// <summary>
    /// The broad catches which neither rethrow nor hand their failure on, each by file and member, with why it neither
    /// swallows nor retries a failure.
    /// </summary>
    private static readonly Dictionary<string, string> ReviewedCatches = new(StringComparer.Ordinal)
    {
        ["FatalErrorHandler.cs:Write"] =
            "Standard error's failure loses the stop's message, not the stop: the exit which follows names the site with its code.",
        ["FileSystemAnswers.cs:WaitOutContention"] =
            "Another process's handle refused the rename or deletion, which changed nothing: an answer the seam waits out, bounded, and reports past the bound.",
        ["WalFileSystem.cs:TryDeleteEmptyDirectoryCore"] =
            "A directory which holds an entry, such as an arriving owner's lock file, is the deletion's answer; contention is waited out and any other failure reported.",
        ["WalFileSystem.cs:TryDeleteLockFileCore"] =
            "On Windows a handle which still refuses the lock file's deletion past the contention bound is another owner's lock: the answer that the lock is held, which the directory's deletion reports as WalLockHeldException.",
        ["WalFileSystem.cs:AttemptOnce"] =
            "Another process's handle refused the rename or deletion, which changed nothing: an answer whose caller waits it out outside its lock, and which the seam reports past the bound.",
        ["WriteAheadLog.cs:CompleteGroupAsync"] =
            "The group's error decides its records' outcomes; the seam reported a storage failure, and the log stops with WalWriteGroup for any other.",
        ["LiveReplica.cs:MaintainCheckpointAsync"] =
            "Narrowed to the disposal or cancellation of a checkpoint whose replica a drop stopped: no storage operation failed.",
    };

    /// <summary>
    /// Only the storage seams reference the platform's file-system types or RocksDB, so every local storage operation
    /// goes through a seam which reports its failure.
    /// </summary>
    [Fact]
    public void OnlyTheStorageSeamsReachTheFileSystemOrRocksDb() =>
        Assert.Empty(Repository.ProductionAssemblies().SelectMany(static assembly => SeamViolations(MetadataReferences.Scan(assembly.Path, static _ => true))));

    [Fact]
    public void TheSeamRuleFiresOnAFixtureWhichReadsAFile() =>
        Assert.Equal([$"{FixtureNamespace}.ReadsAFile -> System.IO.File"], SeamViolations(FixtureTypes()));

    /// <summary>
    /// OL-I11: derived volatile caches are exempted from fail-stop local storage reporting and must never reference
    /// <see cref="FatalErrorHandler"/> or <see cref="FatalErrorSite"/> — a cache read or write failure discards the entry
    /// and falls back to object storage without stopping the process.
    /// </summary>
    [Fact]
    public void DerivedCachesNeverReferenceFatalErrorHandler()
    {
        var fatalTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            typeof(FatalErrorHandler).FullName!,
            typeof(FatalErrorSite).FullName!,
        };

        var violations = Repository.ProductionAssemblies()
            .SelectMany(static assembly => MetadataReferences.Scan(assembly.Path, static _ => true))
            .Where(static type => DerivedCaches.Any(cache => type.FullName == cache || type.FullName.StartsWith(cache + "+", StringComparison.Ordinal)))
            .SelectMany(type => type.Types
                .Where(reference => fatalTypes.Contains(reference.FullName))
                .Select(reference => $"{type.FullName} -> {reference.FullName}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    /// <summary>
    /// Every exit code lies in the reserved block, 79–125, and one production site names each storage site's, in a storage
    /// seam's source, so the code names the site (conventions.md §14). The defect code, <see cref="FatalErrorSite.Defect"/>,
    /// which every defect shares and whose report names the defect, is named outside the seams. The simulated storage
    /// stands in for the sites whose failures it injects, and the registry, which declares every site, names none.
    /// </summary>
    [Fact]
    public void EachExitCodeLiesInTheReservedBlockAndOneProductionSiteNamesIt()
    {
        var root = Repository.FindRoot();
        var sources = Repository.ProductionSourceFiles("*.cs")
            .Where(static path => !path.EndsWith("SimulatedWalStorage.cs", StringComparison.Ordinal) && !path.EndsWith("FatalErrorSite.cs", StringComparison.Ordinal))
            .Select(path => (Path.GetRelativePath(root, path), File.ReadAllText(path)));

        var violations = CodeViolations(sources);
        Assert.True(violations.Count == 0, "Exit codes misnamed:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void TheCodeRuleFiresOnACodeTwoSitesNameOnAnUnnamedCodeAndOnASiteOfTheWrongKind()
    {
        var violations = CodeViolations(
        [
            ("a.cs", "class A { void F() => R(FatalErrorSite.HandleFlush); void G() => R(FatalErrorSite.HandleFlush); }"),
            ("WalFileSystem.cs", "class B { void F() => R(FatalErrorSite.Defect); }"),
        ]);

        Assert.Contains("HandleFlush is named by 2 production sites: a.cs, a.cs", violations);
        Assert.Contains("CreateFile is named by 0 production sites: ", violations);
        Assert.Contains("HandleFlush, a storage site, is named outside the storage seams: a.cs", violations);
        Assert.Contains("Defect, the defect site, is named in a storage seam: WalFileSystem.cs", violations);
    }

    /// <summary>
    /// The storage seams alone report failed local storage operations, the simulated storage standing in for them, and
    /// the components whose memory follows a log alone report defects (conventions.md §14).
    /// </summary>
    [Fact]
    public void TheSeamsAloneReportStorageFailuresAndTheirComponentsDefects()
    {
        var types = Repository.ProductionAssemblies().SelectMany(static assembly => MetadataReferences.Scan(assembly.Path, static _ => true)).ToList();

        Assert.Equal(StorageReporters, Reporters(types, nameof(FatalErrorHandler.StopOnStorageFailure)));
        Assert.Equal(DefectReporters, Reporters(types, nameof(FatalErrorHandler.StopOnDefect)));
        Assert.All(StorageReporters, reporter => Assert.True(
            Seams.Contains(reporter, StringComparer.Ordinal) || reporter == "Orleans.Durable.Storage.Engine.Wal.Simulation.SimulatedWalStorage",
            $"{reporter} reports storage failures but is no storage seam."));
    }

    [Fact]
    public void TheReporterRuleNamesAFixtureWhichReportsEach()
    {
        Assert.Equal([$"{FixtureNamespace}.ReportsFatalErrors"], Reporters(FixtureTypes(), nameof(FatalErrorHandler.StopOnStorageFailure)));
        Assert.Equal([$"{FixtureNamespace}.ReportsFatalErrors"], Reporters(FixtureTypes(), nameof(FatalErrorHandler.StopOnDefect)));
    }

    /// <summary>
    /// No catch in a storage project which could meet a failed local storage operation swallows or retries it: each
    /// rethrows, reports the failure (<see cref="Reports"/>), hands it on (<see cref="Transfers"/>), narrows its filter to
    /// answers, or is reviewed. A catch which both reports and hands the failure on reports it first, so no waiter acts
    /// on a failure the process has not yet stopped for.
    /// </summary>
    [Fact]
    public void NoCatchOfAStorageProjectSwallowsOrRetriesAFailure()
    {
        var root = Repository.FindRoot();
        var sources = StorageSources
            .SelectMany(source => Directory.EnumerateFiles(Path.Combine(root, "src", source.Directory), "*.cs", source.Option))
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)));

        var violations = CatchViolations(sources, ReviewedCatches);
        Assert.True(violations.Count == 0, "Catches which swallow or retry a failure:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void TheCatchRuleFiresOnACatchWhichSwallowsAndOnOneWhichRetries()
    {
        const string Source = """
            class Fixture
            {
                void Swallows() { try { Write(); } catch (IOException) { } }
                async Task Retries() { while (true) { try { Write(); return; } catch (Exception) { await Task.Yield(); } } }
                void Rethrows() { try { Write(); } catch { Clean(); throw; } }
                void Answers() { try { Write(); } catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { } }
                void Hands(TaskCompletionSource source) { try { Write(); } catch (Exception exception) { source.TrySetException(exception); } }
                void ReportsFirst(TaskCompletionSource source) { try { Write(); } catch (Exception exception) { Stop("WalRoll", exception); source.TrySetException(exception); } }
                void AnswersFirst(TaskCompletionSource source) { try { Write(); } catch (Exception exception) { source.TrySetException(exception); FatalErrorHandler.StopOnDefect("WalRoll", exception); } }
                void Cancels() { try { Write(); } catch (OperationCanceledException) { } }
                void MeetsTheStop() { try { Write(); } catch (Exception exception) when (FatalErrorHandler.IsReported(exception)) { } }
            }
            """;

        Assert.Equal(["fixture.cs:Swallows", "fixture.cs:Retries", "fixture.cs:AnswersFirst"], CatchViolations([("fixture.cs", Source)], new Dictionary<string, string>()));
    }

    /// <summary>
    /// Every reviewed catch exists and needs its review: it neither rethrows, reports, nor hands its failure on.
    /// </summary>
    [Fact]
    public void EveryReviewedCatchExistsAndNeedsItsReview()
    {
        var root = Repository.FindRoot();
        var sites = StorageSources
            .SelectMany(source => Directory.EnumerateFiles(Path.Combine(root, "src", source.Directory), "*.cs", source.Option))
            .SelectMany(path => BroadCatches(Path.GetFileName(path), File.ReadAllText(path)))
            .Where(static site => !HandsOn(site.Clause))
            .Select(static site => site.Site)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(ReviewedCatches.Keys, site => Assert.Contains(site, sites));
    }

    /// <summary>
    /// Only <see cref="FatalErrorHandler"/> ends the process; no production type fails it fast, runs code at its exit,
    /// or sets the interceptor which tests use in place of the stop.
    /// </summary>
    [Fact]
    public void OnlyTheFatalErrorHandlerEndsTheProcessAndNothingInterceptsIt() =>
        Assert.Empty(Repository.ProductionAssemblies().SelectMany(static assembly => ExitViolations(MetadataReferences.Scan(assembly.Path, static _ => true))));

    [Fact]
    public void TheExitRuleFiresOnAFixtureWhichEndsHooksAndInterceptsTheProcess() =>
        Assert.Equal(
            [
                $"{FixtureNamespace}.StopsTheProcess -> System.Environment.Exit",
                $"{FixtureNamespace}.StopsTheProcess -> System.Environment.FailFast",
                $"{FixtureNamespace}.StopsTheProcess -> System.AppDomain.add_ProcessExit",
                $"{FixtureNamespace}.StopsTheProcess -> Orleans.Durable.Storage.Diagnostics.FatalErrorHandler.set_Interceptor",
            ],
            ExitViolations(FixtureTypes()));

    private static ScannedType[] FixtureTypes() =>
        [.. MetadataReferences.Scan(typeof(LocalStorageFailureTests).Assembly.Location, static ns => string.Equals(ns, FixtureNamespace, StringComparison.Ordinal))];

    private static List<string> SeamViolations(IEnumerable<ScannedType> types) =>
    [
        .. types
            .Where(static type => !Seams.Any(seam => type.FullName == seam || type.FullName.StartsWith(seam + "+", StringComparison.Ordinal))
                && !DerivedCaches.Any(cache => type.FullName == cache || type.FullName.StartsWith(cache + "+", StringComparison.Ordinal)))
            .SelectMany(static type => type.Types
                .Where(static reference => FileSystemTypes.Contains(reference.FullName)
                    || reference.Namespace == "RocksDbSharp"
                    || reference.Namespace.StartsWith("RocksDbSharp.", StringComparison.Ordinal))
                .Select(reference => $"{type.FullName} -> {reference.FullName}"))
            .Distinct(StringComparer.Ordinal),
    ];

    private static List<string> ExitViolations(IEnumerable<ScannedType> types)
    {
        ReferencedMember[] forbidden =
        [
            new("System.Environment", "FailFast"),
            new("System.AppDomain", "add_ProcessExit"),
            new("System.Runtime.Loader.AssemblyLoadContext", "add_Unloading"),
            new("Orleans.Durable.Storage.Diagnostics.FatalErrorHandler", "set_Interceptor"),
        ];
        ReferencedMember exit = new("System.Environment", "Exit");
        return
        [
            .. types.SelectMany(type => type.Members
                .Where(member => member == exit ? type.FullName != "Orleans.Durable.Storage.Diagnostics.FatalErrorHandler" : forbidden.Contains(member))
                .Select(member => $"{type.FullName} -> {member}"))
                .Distinct(StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Returns the full names of the outermost types whose code calls the fatal-error handler's <paramref name="report"/>,
    /// in ordinal order.
    /// </summary>
    private static List<string> Reporters(IEnumerable<ScannedType> types, string report) =>
    [
        .. types
            .Where(type => type.Members.Contains(new ReferencedMember("Orleans.Durable.Storage.Diagnostics.FatalErrorHandler", report)))
            .Select(static type => type.FullName.Split('+')[0])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    private static List<string> CodeViolations(IEnumerable<(string Path, string Source)> sources)
    {
        var seamFiles = Seams.Select(static seam => seam[(seam.LastIndexOf('.') + 1)..]).ToHashSet(StringComparer.Ordinal);
        var sites = Enum.GetValues<FatalErrorSite>().ToDictionary(static site => site.ToString(), static _ => new List<(string Path, bool InSeam)>(), StringComparer.Ordinal);
        foreach (var (path, source) in sources)
        {
            var inSeam = seamFiles.Contains(Path.GetFileName(path).Split('.')[0]);
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();
            foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (access.Expression is IdentifierNameSyntax { Identifier.ValueText: nameof(FatalErrorSite) }
                    && sites.TryGetValue(access.Name.Identifier.ValueText, out var found))
                {
                    found.Add((path, inSeam));
                }
            }
        }

        var violations = new List<string>();
        foreach (var site in Enum.GetValues<FatalErrorSite>())
        {
            if ((int)site is < 79 or > 125)
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture, $"{site} = {(int)site} lies outside 79–125"));
            }

            var found = sites[site.ToString()];
            if (site == FatalErrorSite.Defect ? found.Count == 0 : found.Count != 1)
            {
                violations.Add($"{site} is named by {found.Count} production sites: {string.Join(", ", found.Select(static named => named.Path))}");
            }

            var defect = site == FatalErrorSite.Defect;
            foreach (var (path, _) in found.Where(named => named.InSeam == defect))
            {
                violations.Add(defect ? $"{site}, the defect site, is named in a storage seam: {path}" : $"{site}, a storage site, is named outside the storage seams: {path}");
            }
        }

        return violations;
    }

    private static List<string> CatchViolations(IEnumerable<(string File, string Source)> sources, IReadOnlyDictionary<string, string> reviewed) =>
    [
        .. sources
            .SelectMany(static source => BroadCatches(source.File, source.Source))
            .Where(static site => !HandsOn(site.Clause))
            .Select(static site => site.Site)
            .Where(site => !reviewed.ContainsKey(site)),
    ];

    /// <summary>
    /// Returns the catches in <paramref name="source"/> which could meet a failed local storage operation: those of no
    /// type or a broad type, unless a filter narrows them to answers, each by file and enclosing member.
    /// </summary>
    private static IEnumerable<(string Site, CatchClauseSyntax Clause)> BroadCatches(string file, string source) =>
        CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<CatchClauseSyntax>()
            .Where(static clause => IsBroad(clause.Declaration?.Type) && !NarrowsToNarrowTypes(clause.Filter) && !NarrowsToReported(clause.Filter))
            .Select(clause => ($"{file}:{EnclosingMember(clause)}", clause));

    private static bool IsBroad(TypeSyntax? type) => type is null || BroadExceptions.Contains(LastIdentifier(type));

    /// <summary>
    /// Returns whether a filter admits only failures already reported, as
    /// <c>when (FatalErrorHandler.IsReported(exception))</c> does: their report stopped the process, so only an
    /// intercepting test reaches the catch.
    /// </summary>
    private static bool NarrowsToReported(CatchFilterClauseSyntax? filter) =>
        filter?.FilterExpression is InvocationExpressionSyntax invocation && InvokedName(invocation.Expression) == nameof(FatalErrorHandler.IsReported);

    /// <summary>
    /// Returns whether a filter tests only for exception types none of which is broad, as
    /// <c>when (exception is FileNotFoundException or DirectoryNotFoundException)</c> does.
    /// </summary>
    private static bool NarrowsToNarrowTypes(CatchFilterClauseSyntax? filter)
    {
        if (filter?.FilterExpression is not IsPatternExpressionSyntax { Pattern: var pattern })
        {
            return false;
        }

        var types = new List<string>();
        return Collect(pattern, types) && types.Count > 0 && !types.Any(BroadExceptions.Contains);

        static bool Collect(PatternSyntax pattern, List<string> types)
        {
            switch (pattern)
            {
                case BinaryPatternSyntax binary when binary.OperatorToken.IsKind(SyntaxKind.OrKeyword):
                    return Collect(binary.Left, types) && Collect(binary.Right, types);
                case TypePatternSyntax typePattern:
                    types.Add(LastIdentifier(typePattern.Type));
                    return true;
                case ConstantPatternSyntax { Expression: NameSyntax name }:
                    types.Add(LastIdentifier(name));
                    return true;
                case DeclarationPatternSyntax declaration:
                    types.Add(LastIdentifier(declaration.Type));
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Returns whether a catch rethrows, reports, or hands on its failure, reporting it before any hand-on when it does
    /// both.
    /// </summary>
    private static bool HandsOn(CatchClauseSyntax clause)
    {
        var calls = clause.Block.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(static invocation => InvokedName(invocation.Expression)).ToList();
        var report = calls.FindIndex(Reports.Contains);
        var transfer = calls.FindIndex(Transfers.Contains);
        if (report >= 0 && transfer >= 0)
        {
            return report < transfer;
        }

        return report >= 0 || transfer >= 0 || clause.Block.DescendantNodes().Any(static node => node is ThrowStatementSyntax or ThrowExpressionSyntax);
    }

    private static string InvokedName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        SimpleNameSyntax name => name.Identifier.ValueText,
        _ => string.Empty,
    };

    private static string LastIdentifier(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => type.ToString(),
    };

    private static string EnclosingMember(SyntaxNode node) => node.Ancestors().FirstOrDefault(static ancestor => ancestor is MemberDeclarationSyntax or LocalFunctionStatementSyntax) switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        LocalFunctionStatementSyntax local => local.Identifier.ValueText,
        ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        AccessorDeclarationSyntax accessor => accessor.Keyword.ValueText,
        { } other => other.Kind().ToString(),
        null => "?",
    };
}
