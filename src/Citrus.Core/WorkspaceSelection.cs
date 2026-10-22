using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Citrus.Core;

/// <summary>Explicit batch membership; UI selection and keyboard modifiers only choose the preview.</summary>
public sealed class WorkspaceInput(string path) : INotifyPropertyChanged
{
    private bool included = true;
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
    public bool IsIncluded
    {
        get => included;
        set { if (included == value) return; included = value; PropertyChanged?.Invoke(this, new(nameof(IsIncluded))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class WorkspaceSelection
{
    private readonly ObservableCollection<WorkspaceInput> items = [];
    private readonly HashSet<string> membershipPaths = new(StringComparer.OrdinalIgnoreCase);
    public ReadOnlyObservableCollection<WorkspaceInput> Items { get; }
    public WorkspaceInput? Active { get; private set; }
    public IReadOnlyList<string> IncludedPaths => Array.AsReadOnly(items.Where(item => item.IsIncluded).Select(item => item.Path).ToArray());
    public event Action? MembershipChanged;
    public WorkspaceSelection() => Items = new(items);
    public void Add(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (!membershipPaths.Add(path)) continue;
            var item = new WorkspaceInput(path);
            item.PropertyChanged += InclusionChanged;
            items.Add(item);
        }
        Active ??= items.FirstOrDefault();
        MembershipChanged?.Invoke();
    }
    public void Preview(WorkspaceInput? input)
    {
        if (input is not null && !items.Contains(input)) throw new ArgumentException("Preview must be in the workspace.", nameof(input));
        Active = input;
    }
    public void Remove(WorkspaceInput input)
    {
        bool wasActive = Active == input;
        int index = items.IndexOf(input);
        if (index < 0) return;
        // Collection observers may add the same path again during removal.
        membershipPaths.Remove(input.Path);
        items.RemoveAt(index);
        input.PropertyChanged -= InclusionChanged;
        // Bound selectors can clear selection synchronously during CollectionChanged.
        if (wasActive) Active = items.FirstOrDefault();
        MembershipChanged?.Invoke();
    }
    public void Clear()
    {
        foreach (var item in items) item.PropertyChanged -= InclusionChanged;
        membershipPaths.Clear();
        items.Clear(); Active = null;
        MembershipChanged?.Invoke();
    }
    public void SetAllIncluded(bool included)
    {
        foreach (var item in items) { item.PropertyChanged -= InclusionChanged; item.IsIncluded = included; item.PropertyChanged += InclusionChanged; }
        MembershipChanged?.Invoke();
    }
    private void InclusionChanged(object? sender, PropertyChangedEventArgs e) => MembershipChanged?.Invoke();
}
