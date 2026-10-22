using Citrus.Core;
using System.Diagnostics;
using System.Collections.Specialized;
using Xunit.Abstractions;

namespace Citrus.Core.Tests;

public sealed class WorkspaceSelectionTests(ITestOutputHelper output)
{
    [Fact]
    public void Removing_and_clearing_release_case_insensitive_membership_for_later_additions()
    {
        var selection = new WorkspaceSelection();
        selection.Add(["C:\\Photos\\one.png", "C:\\Photos\\two.png"]);
        var removed = selection.Items[0];
        selection.Remove(removed);
        selection.Add(["c:\\photos\\ONE.png", "C:\\PHOTOS\\ONE.PNG"]);
        Assert.Equal(["C:\\Photos\\two.png", "c:\\photos\\ONE.png"], selection.IncludedPaths);
        Assert.NotSame(removed, selection.Items[1]);
        Assert.True(selection.Items[1].IsIncluded);

        selection.Clear();
        selection.Add(["C:\\PHOTOS\\ONE.PNG"]);
        Assert.Single(selection.Items);
        Assert.Same(selection.Items[0], selection.Active);
    }

    [Fact]
    public void Collection_observers_can_readd_removed_paths_and_clear_then_readd()
    {
        var selection = new WorkspaceSelection();
        selection.Add(["one.png"]);
        ((INotifyCollectionChanged)selection.Items).CollectionChanged += (_, args) =>
        {
            if (args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Reset)
                selection.Add(["ONE.PNG"]);
        };

        selection.Remove(selection.Items[0]);
        Assert.Equal("ONE.PNG", Assert.Single(selection.Items).Path);
        selection.Clear();
        Assert.Equal("ONE.PNG", Assert.Single(selection.Items).Path);
    }

    [Fact]
    public void Large_batch_admission_preserves_order_and_uses_one_membership_notification()
    {
        var selection = new WorkspaceSelection();
        string[] paths = Enumerable.Range(0, 10_000).Select(index => $"C:\\Photos\\{index:D5}.png").ToArray();
        int notifications = 0;
        selection.MembershipChanged += () => notifications++;
        long start = Stopwatch.GetTimestamp();
        selection.Add(paths);
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        output.WriteLine($"Admission of {paths.Length:N0} distinct paths: {elapsed:F3} ms");
        Assert.Equal(paths, selection.IncludedPaths);
        Assert.Equal(1, notifications);
        Assert.Same(selection.Items[0], selection.Active);
    }

    [Fact]
    public void Preview_navigation_never_changes_explicit_batch_membership()
    {
        var selection = new WorkspaceSelection();
        selection.Add(["C:\\Photos\\one.png", "C:\\Photos\\two.png"]);
        selection.Items[0].IsIncluded = false;
        selection.Preview(selection.Items[0]);
        Assert.Equal(selection.Items[0], selection.Active);
        Assert.Equal(["C:\\Photos\\two.png"], selection.IncludedPaths);
        selection.Preview(selection.Items[1]);
        selection.Preview(null);
        Assert.Equal(["C:\\Photos\\two.png"], selection.IncludedPaths);
    }

    [Fact]
    public void Inputs_deduplicate_case_insensitively_without_resetting_inclusion_or_order()
    {
        var selection = new WorkspaceSelection();
        selection.Add(["C:\\Photos\\one.png", "C:\\Photos\\two.png"]);
        selection.Items[0].IsIncluded = false;
        selection.Add(["c:\\photos\\ONE.png", "C:\\Photos\\three.png"]);
        Assert.Equal(3, selection.Items.Count);
        Assert.False(selection.Items[0].IsIncluded);
        Assert.Equal(["C:\\Photos\\two.png", "C:\\Photos\\three.png"], selection.IncludedPaths);
    }

    [Fact]
    public void Removing_preview_selects_remaining_item_and_does_not_delete_file()
    {
        string file = Path.GetTempFileName();
        try
        {
            var selection = new WorkspaceSelection();
            selection.Add([file, "second.png"]);
            selection.Remove(selection.Active!);
            Assert.True(File.Exists(file));
            Assert.Equal("second.png", selection.Active?.Path);
            selection.Clear();
            Assert.Null(selection.Active);
            Assert.Empty(selection.IncludedPaths);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Removing_active_item_survives_ui_clearing_selection_during_collection_change()
    {
        var selection = new WorkspaceSelection();
        selection.Add(["one.png", "two.png"]);
        ((System.Collections.Specialized.INotifyCollectionChanged)selection.Items).CollectionChanged += (_, _) => selection.Preview(null);
        selection.Remove(selection.Active!);
        Assert.Equal("two.png", selection.Active?.Path);
    }

    [Fact]
    public void Submission_snapshot_is_stable_when_preview_and_inclusion_change()
    {
        var selection = new WorkspaceSelection();
        selection.Add(["one.png", "two.png"]);
        var snapshot = selection.IncludedPaths;
        selection.SetAllIncluded(false);
        selection.Preview(selection.Items[1]);
        Assert.Equal(["one.png", "two.png"], snapshot);
        Assert.Empty(selection.IncludedPaths);
        selection.SetAllIncluded(true);
        Assert.Equal(snapshot, selection.IncludedPaths);
    }
}
