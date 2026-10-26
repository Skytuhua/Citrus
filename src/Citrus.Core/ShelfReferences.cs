using System.Collections.ObjectModel;

namespace Citrus.Core;

/// <summary>A shelf owned by its caller's UI context. Metadata scans run in the background;
/// collection changes resume on that context. A removed/readded path has a new identity.</summary>
public sealed class ShelfReferences : IDisposable
{
    private sealed class Reference(string path)
    {
        public string Path { get; } = path;
    }

    private readonly ObservableCollection<string> items = [];
    private readonly Dictionary<string, Reference> membership = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, bool> isAvailable;
    private readonly CancellationTokenSource stopping = new();
    private bool refreshing, disposed;

    public ReadOnlyObservableCollection<string> Items { get; }

    public ShelfReferences(Func<string, bool>? isAvailable = null)
    {
        this.isAvailable = isAvailable ?? IsAvailable;
        Items = new(items);
    }

    public bool Add(string path)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (membership.TryGetValue(path, out var existing))
        {
            // Fresh admission also supersedes an in-flight missing result for an existing row.
            membership[path] = new(existing.Path);
            return false;
        }
        membership.Add(path, new(path));
        items.Add(path);
        return true;
    }

    public bool Remove(string path)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!membership.Remove(path, out var reference)) return false;
        return items.Remove(reference.Path);
    }

    /// <summary>Recheck only the transfer selection, pruning unavailable members immediately.</summary>
    public string[] ValidateSelection(IReadOnlyList<string> paths)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var valid = new List<string>(paths.Count);
        foreach (string path in paths)
        {
            if (!membership.TryGetValue(path, out var reference)) continue;
            if (isAvailable(reference.Path)) valid.Add(reference.Path);
            else Remove(reference.Path);
        }
        return valid.ToArray();
    }

    /// <summary>Ignore repeat polling requests until the current scan and its UI updates finish.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (disposed || refreshing) return;
        cancellationToken.ThrowIfCancellationRequested();
        refreshing = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, cancellationToken);
        CancellationToken token = cancellation.Token;
        try
        {
            Reference[] snapshot = membership.Values.ToArray();
            var missing = await Task.Run(() =>
            {
                var result = new List<Reference>();
                foreach (var reference in snapshot)
                {
                    token.ThrowIfCancellationRequested();
                    if (!isAvailable(reference.Path)) result.Add(reference);
                }
                return result;
            }, token);

            token.ThrowIfCancellationRequested();
            foreach (var reference in missing)
                if (membership.TryGetValue(reference.Path, out var current) && ReferenceEquals(current, reference))
                    Remove(reference.Path);
        }
        finally { refreshing = false; }
    }

    private static bool IsAvailable(string path) => DropValidation.Accepts([path], OperationMode.Actions) &&
        (File.Exists(path) || Directory.Exists(path));

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stopping.Cancel();
        stopping.Dispose();
    }
}
