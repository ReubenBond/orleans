using System.Globalization;
using System.Text.RegularExpressions;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// Pins decision 12's names (implementation plan rows B.8b and B.8c) and decision 17's Archive: no text the repository tracks names a retired layer,
/// project, type, spec file, or invariant ID, and no code names a replica handle a drive, outside quotations and the
/// places which keep an old name on purpose. Each exemption says why it stays, and an exemption which no longer applies
/// fails.
/// </summary>
public sealed partial class RetiredNamesTests
{
    /// <summary>
    /// The retired names, each set with the implementation plan's decisions which keep it: decision 12 maps every retired
    /// name to its successor, decisions 10, 11, and 14 record the choices of row B.8c's fold in the names it retired, and
    /// decision 17 names the Archive and its implementations.
    /// </summary>
    private static readonly RetiredSet[] RetiredSets =
    [
        new(RetiredName(), KeptBy: [12]),
        new(FoldedName(), KeptBy: [10, 11, 12, 14]),
        new(ArchiveRename(), KeptBy: [17]),
    ];

    /// <summary>Text other repositories own, generated text, encoded data, and this file, which names what it retires.</summary>
    private static readonly string[] SkippedPaths =
    [
        "test/Durable.Storage/docs/engineering-principles/", "test/Durable.Storage/fuzz/corpus/",
        "test/Durable.Storage/Orleans.Durable.Storage.Architecture.Tests/RetiredNamesTests.cs",
    ];

    private static readonly HashSet<string> TextExtensions = new(
        [
            ".cs", ".csproj", ".props", ".targets", ".slnx", ".json", ".md", ".ps1", ".psm1", ".yml", ".yaml", ".tla", ".cfg",
            ".qnt", ".txt", ".mjs", ".js", ".xml", ".editorconfig", ".sh", ".toml",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] CodeRoots = ["src/", "test/Durable.Storage/", "playground/DurableStorage/"];

    /// <summary>The roots whose text <see cref="NoBuildOrTestSettingNamesClog"/> reads.</summary>
    private static readonly string[] SettingRoots = [.. CodeRoots, "eng/durable-storage/"];

    /// <summary>The Clog names which keep their spelling in Durable Storage's text, with the reason each stays.</summary>
    private static readonly Exemption[] SettingExemptions =
    [
        new(@"\bClog\.LogDrivePerf\b", "wal.md cites the benchmark which took its measurement by the name it ran under."),
    ];

    /// <summary>The names which keep their spelling in text, with the reason each stays.</summary>
    private static readonly Exemption[] TextExemptions =
    [
        new(@"\bLogDrive(?=\s+paper|, Delos)", "The LogDrive paper keeps its name."),
        new(@"\bClog\.LogDrivePerf\b", "wal.md cites the benchmark which took its measurement by the name it ran under."),
        new(@"\bresearch/papers/[\w./-]+", "The papers' paths."),
    ];

    /// <summary>The code names which keep a drive, with the reason each stays.</summary>
    private static readonly Exemption[] DriveExemptions =
    [
        new(@"\bDrive(?:Info|Type|Format)\b", "The platform's names for a disk."),
    ];

    /// <summary>
    /// No tracked text names a retired name: FsLog, LogDrive, SegmentStore, Tier 1 or 2, the JournalStore project, a
    /// retired spec file or its link text, a type named for segments, or a SEG, SS, FL, or T2 invariant ID; nor, since
    /// row B.8c's fold, AtomicLog or a name which contains it (<c>IAtomicLog</c> among them), its spec file
    /// <c>atomiclog.md</c>, <c>SequencerSlot</c>, or an AL or LD invariant ID; nor, since the Archive was named, the
    /// History layer's projects and store types, key logs, their spec files, or an AR or KL invariant ID. Quotations are
    /// exempt: Markdown blockquotes, and quoted text in Markdown and in comments. So are the implementation plan's done
    /// rows, which record what landed in the names of their time, and the decisions <see cref="RetiredSets"/> lists for each set.
    /// </summary>
    [Fact]
    public void NoTextNamesARetiredName()
    {
        var violations = new List<string>();
        foreach (var (path, lines) in TrackedText())
        {
            foreach (var retired in RetiredSets)
            {
                var text = CheckedText(path, lines, retired.KeptBy);
                violations.AddRange(Violations(retired.Names.Matches(text), text, TextExemptions).Select(match =>
                    string.Create(CultureInfo.InvariantCulture, $"{path}:{LineOf(text, match.Index)}: {match.Value}")));
            }
        }

        Assert.True(violations.Count == 0, string.Join('\n', violations));
        Assert.Empty(StaleExemptions(TextExemptions));
    }

    /// <summary>
    /// No code names a replica handle a drive: no identifier in a project's C# sources, outside comments and strings,
    /// contains the word.
    /// </summary>
    [Fact]
    public void NoCodeNamesADrive()
    {
        var violations = new List<string>();
        foreach (var (path, lines) in TrackedText())
        {
            if (!path.EndsWith(".cs", StringComparison.Ordinal) || !CodeRoots.Any(root => path.StartsWith(root, StringComparison.Ordinal)))
            {
                continue;
            }

            var code = Blank(StringOrComment(), string.Join('\n', lines));
            violations.AddRange(Violations(DriveName().Matches(code), code, DriveExemptions).Select(match =>
                string.Create(CultureInfo.InvariantCulture, $"{path}:{LineOf(code, match.Index)}: {match.Value}")));
        }

        Assert.True(violations.Count == 0, string.Join('\n', violations));
        Assert.Empty(StaleExemptions(DriveExemptions));
    }

    /// <summary>
    /// No code names a Clog namespace, type, or member: since row 6.3's rename, the code's names are of the
    /// Orleans.Durable.Storage family, and no identifier in a project's C# sources, outside comments and strings, starts
    /// with Clog.
    /// </summary>
    [Fact]
    public void NoCodeNamesAClogName()
    {
        var violations = new List<string>();
        foreach (var (path, lines) in TrackedText())
        {
            if (!path.EndsWith(".cs", StringComparison.Ordinal) || !CodeRoots.Any(root => path.StartsWith(root, StringComparison.Ordinal)))
            {
                continue;
            }

            var code = Blank(StringOrComment(), string.Join('\n', lines));
            violations.AddRange(ClogName().Matches(code).Select(match =>
                string.Create(CultureInfo.InvariantCulture, $"{path}:{LineOf(code, match.Index)}: {match.Value}")));
        }

        Assert.True(violations.Count == 0, string.Join('\n', violations));
    }

    [Theory]
    [InlineData("using Clog.Ledgers;", "Clog.Ledgers")]
    [InlineData("services.AddClogJournaling();", "AddClogJournaling")]
    [InlineData("var options = new ClogJournalingOptions();", "ClogJournalingOptions")]
    [InlineData("var clog = Clogged;", null)]
    public void TheClogNameCheckFindsANamespaceATypeAndAMember(string code, string? found) =>
        Assert.Equal(found, ClogName().Match(code) is { Success: true } match ? match.Value : null);

    /// <summary>
    /// No tracked text of Durable Storage, its build settings, or its sample names a Clog setting: an MSBuild property,
    /// item, or target such as <c>ClogDebugLeg</c>, a <c>CLOG_</c> environment variable, or a <c>CLOG</c> diagnostic ID.
    /// Their names are of the Durable Storage family: <c>DurableStorage…</c>, <c>DURABLE_STORAGE_…</c>, and <c>ODS…</c>.
    /// </summary>
    [Fact]
    public void NoBuildOrTestSettingNamesClog()
    {
        var violations = new List<string>();
        foreach (var (path, lines) in TrackedText())
        {
            if (!SettingRoots.Any(root => path.StartsWith(root, StringComparison.Ordinal)))
            {
                continue;
            }

            var text = string.Join('\n', lines);
            foreach (var pattern in (Regex[])[ClogName(), ClogSetting()])
            {
                violations.AddRange(Violations(pattern.Matches(text), text, SettingExemptions).Select(match =>
                    string.Create(CultureInfo.InvariantCulture, $"{path}:{LineOf(text, match.Index)}: {match.Value}")));
            }
        }

        Assert.True(violations.Count == 0, string.Join('\n', violations));
        Assert.Empty(StaleExemptions(SettingExemptions));
    }

    [Theory]
    [InlineData("<ClogDebugLeg>true</ClogDebugLeg>", "ClogDebugLeg")]
    [InlineData("$env:CLOG_SIM_SEEDS = '1..500'", "CLOG_SIM_SEEDS")]
    [InlineData("Code=\"CLOG0008\"", "CLOG0008")]
    [InlineData("<DurableStorageDebugLeg>true</DurableStorageDebugLeg>", null)]
    public void TheClogSettingCheckFindsAPropertyAVariableAndADiagnostic(string text, string? found) =>
        Assert.Equal(found, (ClogName().Match(text) is { Success: true } name ? name : ClogSetting().Match(text)) is { Success: true } match ? match.Value : null);
    /// <summary>
    /// Regression test, #175: the checks read only what git tracks or would track, so a file under an ignored folder, here
    /// the scripts' artifacts directory, is never read, whatever names it holds.
    /// </summary>
    [Fact]
    public void AFileUnderAnIgnoredFolderIsNotRead()
    {
        var root = Repository.FindRoot();
        var cache = Path.Combine(Repository.StorageRoot, "artifacts");
        var created = !Directory.Exists(cache);
        var probe = Path.Combine(cache, "clog-retired-names-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(probe);
        try
        {
            File.WriteAllText(Path.Combine(probe, "Probe.xml"), "<doc>SegmentMatch</doc>");
            var relative = Path.GetRelativePath(root, probe).Replace('\\', '/') + "/";
            Assert.DoesNotContain(TrackedText(), file => file.Path.StartsWith(relative, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(probe, recursive: true);
            if (created && !Directory.EnumerateFileSystemEntries(cache).Any())
            {
                Directory.Delete(cache);
            }
        }
    }

    /// <summary>Returns each match no exemption covers, counting the exemptions which cover one.</summary>
    private static List<Match> Violations(MatchCollection matches, string text, Exemption[] exemptions)
    {
        var spans = exemptions.Select(exemption => exemption.Spans(text)).ToArray();
        var uncovered = new List<Match>();
        foreach (Match match in matches)
        {
            var covering = Array.FindIndex(
                spans,
                exempted => exempted.Any(span => span.Index <= match.Index && match.Index + match.Length <= span.Index + span.Length));
            if (covering < 0)
            {
                uncovered.Add(match);
            }
            else
            {
                exemptions[covering].Uses++;
            }
        }

        return uncovered;
    }

    /// <summary>
    /// The text of a file which the check reads, its lines joined by line breaks: Markdown blockquotes, quotations in
    /// Markdown and in C# comment lines, and the implementation plan's done rows and the decisions in
    /// <paramref name="keptBy"/> are blank.
    /// </summary>
    private static string CheckedText(string path, string[] lines, int[] keptBy)
    {
        var plan = path == "docs/implementation-plan.md";
        var markdown = path.EndsWith(".md", StringComparison.Ordinal);
        var csharp = path.EndsWith(".cs", StringComparison.Ordinal);
        var decision = 0;
        var checkedLines = new string[lines.Length];
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (plan && PlanDecision().Match(line) is { Success: true } numbered)
            {
                decision = int.Parse(numbered.Groups["number"].Value, CultureInfo.InvariantCulture);
            }

            if (plan && (keptBy.Contains(decision) || DoneRow().IsMatch(line)) || markdown && Blockquote().IsMatch(line))
            {
                line = string.Empty;
            }
            else if (csharp && CommentLine().IsMatch(line))
            {
                line = Blank(Quotation(), line);
            }

            checkedLines[index] = line;
        }

        // A Markdown quotation may wrap onto its paragraph's next lines, and so may a name.
        var text = string.Join('\n', checkedLines);
        return markdown ? Blank(Quotation(), text) : text;
    }

    /// <summary>Replaces each match of <paramref name="regex"/> with spaces, keeping its line breaks.</summary>
    private static string Blank(Regex regex, string text) =>
        regex.Replace(text, static match => string.Concat(match.Value.Select(static character => character == '\n' ? '\n' : ' ')));

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static IEnumerable<string> StaleExemptions(Exemption[] exemptions) =>
        exemptions.Where(static exemption => exemption.Uses == 0).Select(static exemption => $"{exemption.Pattern} no longer applies: {exemption.Reason}");

    /// <summary>
    /// The repository's text files which git tracks or would track, by their path from its root with forward slashes,
    /// and their lines. An ignored path, such as the repository-local NuGet cache, holds none of them (#175).
    /// </summary>
    internal static IEnumerable<(string Path, string[] Lines)> TrackedText()
    {
        var root = Repository.FindRoot();
        foreach (var path in Repository.TrackedFiles())
        {
            var name = Path.GetFileName(path);
            if ((TextExtensions.Contains(Path.GetExtension(name)) || name == "Justfile") &&
                !name.EndsWith("packages.lock.json", StringComparison.Ordinal) &&
                !SkippedPaths.Any(skipped => path.StartsWith(skipped, StringComparison.Ordinal)))
            {
                yield return (path, File.ReadAllLines(Path.Combine(root, path)));
            }
        }
    }

    [GeneratedRegex(
        @"FsLog\w*|\b\w*LogDrive\w*|\b\w*SegmentStore\w*|\bTier[ -]?[12]\b|\bClog\.JournalStore\b|\b\w*SegmentJournalStore\w*" +
        @"|\b(?:fslog|logdrive|segmentstore|journalstore|tier2)\b|\bsegments(?:\.md\b| §)|\bSegments layer\b|\bSegment[A-Z]\w*" +
        @"|\b(?:SEG|SS|FL|T2)-I\d+",
        RegexOptions.None,
        matchTimeoutMilliseconds: 10_000)]
    private static partial Regex RetiredName();

    /// <summary>The names row B.8c's fold retired, with the ledger protocol's and the replica contract's old ID prefixes.</summary>
    [GeneratedRegex(@"\b\w*AtomicLog\w*|\batomiclog\b|\bSequencerSlot\w*|\b(?:AL|LD)-I\d+", RegexOptions.None, matchTimeoutMilliseconds: 10_000)]
    private static partial Regex FoldedName();

    /// <summary>
    /// The names the Archive retired: the History layer's and key logs' projects, the history store and its reader, key
    /// logs and their types, history.md and keylogs.md, and the LSM's and key logs' old invariant ID prefixes.
    /// </summary>
    [GeneratedRegex(
        @"\bClog\.(?:History|KeyLogs)\b|\b\w*HistoryStore\w*|\bHistoryReader\b|\b\w*KeyLog\w*|(?<![-\w])[Kk]ey[- ]logs?\b|\b[Hh]istory[- ]stores?\b|\b(?:history|keylogs)\.md\b|\b(?:AR|KL)-I\d+",
        RegexOptions.None,
        matchTimeoutMilliseconds: 10_000)]
    private static partial Regex ArchiveRename();

    /// <summary>A Clog namespace, or an identifier which starts with Clog or names it after a prefix, such as <c>AddClog…</c>.</summary>
    [GeneratedRegex(@"\bClog\.[A-Z]\w*(?:\.\w+)*|\b\w*Clog[A-Z]\w*", RegexOptions.None, matchTimeoutMilliseconds: 10_000)]
    private static partial Regex ClogName();

    /// <summary>A Clog environment variable, such as <c>CLOG_SIM_SEEDS</c>, or a Clog diagnostic ID, such as <c>CLOG0008</c>.</summary>
    [GeneratedRegex(@"\bCLOG(?:_[A-Z]\w*|\d+)\b", RegexOptions.None, matchTimeoutMilliseconds: 10_000)]
    private static partial Regex ClogSetting();

    [GeneratedRegex(@"\b\w*[Dd]rive\w*\b", RegexOptions.None, matchTimeoutMilliseconds: 10_000)]
    private static partial Regex DriveName();

    [GeneratedRegex(@"^\s*>", RegexOptions.None, matchTimeoutMilliseconds: 10_000)]
    private static partial Regex Blockquote();

    [GeneratedRegex(@"^\s*//", RegexOptions.None, matchTimeoutMilliseconds: 10_000)]
    private static partial Regex CommentLine();

    /// <summary>A quotation, which may wrap onto its paragraph's next lines but not past a blank line.</summary>
    [GeneratedRegex(
        @"""(?:[^""\n]|\n(?![ \t]*\n)){3,600}?""|“(?:[^”\n]|\n(?![ \t]*\n)){1,600}?”",
        RegexOptions.None,
        matchTimeoutMilliseconds: 10_000)]
    private static partial Regex Quotation();

    [GeneratedRegex(@"/\*.*?\*/|//[^\n]*|@?\$?""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)'", RegexOptions.Singleline, matchTimeoutMilliseconds: 10_000)]
    private static partial Regex StringOrComment();

    /// <summary>A numbered decision of the implementation plan, by its number.</summary>
    [GeneratedRegex(@"^(?<number>\d+)\. \*\*", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 10_000)]
    private static partial Regex PlanDecision();

    /// <summary>A row of the implementation plan whose status cell records that it landed.</summary>
    [GeneratedRegex(@"^\|.*\| (?:Done|Landed)\b[^|]*\|\s*$", RegexOptions.None, matchTimeoutMilliseconds: 10_000)]
    private static partial Regex DoneRow();

    /// <summary>A set of retired names, and the implementation plan's decisions which keep them, by number.</summary>
    private sealed record RetiredSet(Regex Names, int[] KeptBy);

    /// <summary>A name which keeps its spelling, the reason it stays, and how many names it covered in this run.</summary>
    private sealed class Exemption(string pattern, string reason)
    {
        private readonly Regex _regex = new(pattern, RegexOptions.None, TimeSpan.FromSeconds(10));

        public string Pattern { get; } = pattern;

        public string Reason { get; } = reason;

        public int Uses { get; set; }

        public IReadOnlyList<Match> Spans(string text) => _regex.Matches(text);
    }
}
