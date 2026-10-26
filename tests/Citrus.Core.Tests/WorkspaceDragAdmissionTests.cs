using Citrus.Windows;

namespace Citrus.Core.Tests;

public sealed class WorkspaceDragAdmissionTests
{
    [Fact]
    public void Repeated_hover_inspects_a_file_selection_once()
    {
        using var files = new DragFiles();
        string[] paths = Enumerable.Range(0, 32).Select(index => files.Add(index + ".txt")).ToArray();
        int reads = 0, inspections = 0;
        var admission = new WorkspaceDragAdmission((value, mode) => { inspections++; return DropValidation.Accepts(value, mode); });
        var identity = new object();

        for (int callback = 0; callback < 250; callback++)
            Assert.True(admission.Accepts(identity, OperationMode.Actions, () => { reads++; return paths; }));

        Assert.Equal(1, reads);
        Assert.Equal(1, inspections);
    }

    [Fact]
    public void Hover_result_can_be_cached_after_deletion_but_fresh_drop_validation_rejects_it()
    {
        using var files = new DragFiles();
        string[] paths = [files.Add("deleted.txt")];
        var admission = new WorkspaceDragAdmission();
        var identity = new object();
        Assert.True(admission.Accepts(identity, OperationMode.Actions, () => paths));
        File.Delete(paths[0]);

        Assert.True(admission.Accepts(identity, OperationMode.Actions, () => paths));
        Assert.False(DropValidation.Accepts(paths, OperationMode.Actions));
        admission.Clear();
        Assert.False(admission.Accepts(identity, OperationMode.Actions, () => paths));
    }

    [Fact]
    public void New_data_object_uses_reference_identity_and_reinspects_the_payload()
    {
        using var files = new DragFiles();
        string[] paths = [files.Add("deleted.txt")];
        var admission = new WorkspaceDragAdmission();
        var first = new DragIdentity(1);
        Assert.True(admission.Accepts(first, OperationMode.Actions, () => paths));
        File.Delete(paths[0]);

        Assert.False(admission.Accepts(new DragIdentity(1), OperationMode.Actions, () => paths));
    }

    [Fact]
    public void Changing_mode_reinspects_directory_eligibility()
    {
        using var files = new DragFiles();
        int inspections = 0;
        var admission = new WorkspaceDragAdmission((paths, mode) => { inspections++; return DropValidation.Accepts(paths, mode); });
        var identity = new object();

        Assert.True(admission.Accepts(identity, OperationMode.Actions, () => [files.Root]));
        Assert.False(admission.Accepts(identity, OperationMode.Convert, () => [files.Root]));
        Assert.False(admission.Accepts(identity, OperationMode.Convert, () => [files.Root]));
        Assert.True(admission.Accepts(identity, OperationMode.Actions, () => [files.Root]));
        Assert.Equal(3, inspections);
    }

    [Fact]
    public void Unsupported_payload_is_rejected_until_the_drag_is_cleared()
    {
        using var files = new DragFiles();
        string[] paths = [files.Add("ready.txt")];
        int reads = 0, inspections = 0;
        var admission = new WorkspaceDragAdmission((value, mode) => { inspections++; return DropValidation.Accepts(value, mode); });
        var identity = new object();

        Assert.False(admission.Accepts(identity, OperationMode.Actions, () => { reads++; return null; }));
        Assert.False(admission.Accepts(identity, OperationMode.Actions, () => { reads++; return paths; }));
        Assert.Equal(1, reads);
        Assert.Equal(0, inspections);
        admission.Clear();
        admission.Clear();
        Assert.True(admission.Accepts(identity, OperationMode.Actions, () => paths));
        Assert.Equal(1, inspections);
    }

    private sealed record DragIdentity(int Value);

    private sealed class DragFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "citrus-drag-" + Guid.NewGuid().ToString("N"));
        public DragFiles() => Directory.CreateDirectory(Root);
        public string Add(string name) { string path = Path.Combine(Root, name); File.WriteAllText(path, "drag fixture"); return path; }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
