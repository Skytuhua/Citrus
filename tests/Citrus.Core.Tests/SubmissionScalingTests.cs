using System.Collections;
using Citrus.Core;

namespace Citrus.Core.Tests;

public sealed class SubmissionScalingTests
{
    private static readonly OperationDescriptor Copy = new("test.copy", "Copy", OperationMode.Convert, ".out", false, false);

    [Fact]
    public async Task Large_batch_reads_inspection_results_a_linear_number_of_times()
    {
        const int count = 1_000;
        var paths = Enumerable.Range(0, count).Select(i => Path.GetFullPath($"source-{i}.txt")).ToArray();
        var inspections = new CountedInspections(paths.Select(path => new InputInspection(path, null, null, "Unreadable fixture")).ToArray());
        var service = new SubmissionService(new JobQueue([]), (_, _) => Task.FromResult<IReadOnlyList<InputInspection>>(inspections));

        var jobs = await service.SubmitAsync(Copy, paths, (_, _) => throw new InvalidOperationException("No dialog expected."));

        Assert.Equal(count, jobs.Count);
        Assert.All(jobs, job => Assert.Equal(ConversionJobState.Failed, job.State));
        Assert.InRange(inspections.Reads, count, count * 4);
    }

    [Fact]
    public async Task Duplicate_inspections_keep_the_first_error_for_each_ordinary_item()
    {
        string path = Path.GetFullPath("duplicate.txt");
        var service = new SubmissionService(new JobQueue([]), (_, _) => Task.FromResult<IReadOnlyList<InputInspection>>([
            new(path, null, null, null), new(path, null, null, "First error"), new(path, null, null, "Later error")]));

        var jobs = await service.SubmitAsync(Copy, [path, path], (_, _) => throw new InvalidOperationException("No dialog expected."));

        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, job => Assert.Equal("First error", job.Error?.Message));
        Assert.All(jobs, job => Assert.Equal(path, Assert.Single(job.Request.Inputs)));
    }

    [Fact]
    public async Task Reordered_group_keeps_first_inspection_error_and_confirmed_input_order()
    {
        var group = Copy with { Id = "archive.zip", IsGroup = true };
        string first = Path.GetFullPath("first.txt"), second = Path.GetFullPath("second.txt");
        var service = new SubmissionService(new JobQueue([]), (_, _) => Task.FromResult<IReadOnlyList<InputInspection>>([
            new(first, null, null, "First inspection error"), new(second, null, null, "Second inspection error")]));

        var jobs = await service.SubmitAsync(group, [first, second], (draft, _) =>
            Task.FromResult<SubmissionChoice?>(new([second, first], draft.Options, null)));

        var job = Assert.Single(jobs);
        Assert.Equal("First inspection error", job.Error?.Message);
        Assert.Equal([second, first], job.Request.Inputs);
    }

    private sealed class CountedInspections(InputInspection[] items) : IReadOnlyList<InputInspection>
    {
        public int Reads { get; private set; }
        public int Count => items.Length;
        public InputInspection this[int index] { get { Reads++; return items[index]; } }
        public IEnumerator<InputInspection> GetEnumerator()
        {
            foreach (var item in items) { Reads++; yield return item; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
