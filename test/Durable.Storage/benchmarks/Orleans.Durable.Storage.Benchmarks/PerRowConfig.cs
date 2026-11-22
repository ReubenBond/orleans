using System.Globalization;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>Adds a column of the mean time of one batch divided by its rows, for benchmarks with a <c>Rows</c> parameter.</summary>
internal sealed class PerRowConfig : ManualConfig
{
    public PerRowConfig() => AddColumn(new PerRowColumn());

    private sealed class PerRowColumn : IColumn
    {
        public string Id => nameof(PerRowColumn);

        public string ColumnName => "Mean per row";

        public bool AlwaysShow => true;

        public ColumnCategory Category => ColumnCategory.Statistics;

        public int PriorityInCategory => 1;

        public bool IsNumeric => true;

        public UnitType UnitType => UnitType.Time;

        public string Legend => "The mean time of one batch divided by its rows";

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => GetValue(summary, benchmarkCase, SummaryStyle.Default);

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
        {
            var mean = summary[benchmarkCase]?.ResultStatistics?.Mean;
            var rows = Convert.ToInt32(benchmarkCase.Parameters["Rows"], CultureInfo.InvariantCulture);
            return mean is { } nanoseconds ? string.Create(CultureInfo.InvariantCulture, $"{nanoseconds / rows:N1} ns") : "NA";
        }

        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

        public bool IsAvailable(Summary summary) => true;
    }
}
