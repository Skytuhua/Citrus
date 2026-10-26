using System;
using System.Collections.Generic;
using Citrus.Core;

namespace Citrus.Windows;

internal sealed class WorkspaceDragAdmission
{
    private readonly Func<IReadOnlyList<string>, OperationMode, bool> inspect;
    private object? identity;
    private OperationMode mode;
    private bool accepted;

    public WorkspaceDragAdmission(Func<IReadOnlyList<string>, OperationMode, bool>? inspect = null) => this.inspect = inspect ?? DropValidation.Accepts;

    // WPF keeps one IDataObject instance for a native drag. Cache preview admission only;
    // the drop callback must read and validate its current paths independently.
    public bool Accepts(object identity, OperationMode mode, Func<string[]?> readPaths)
    {
        if (ReferenceEquals(this.identity, identity) && this.mode == mode) return accepted;
        Clear();
        accepted = readPaths() is { } paths && inspect(paths, mode);
        this.identity = identity;
        this.mode = mode;
        return accepted;
    }

    public void Clear() { identity = null; accepted = false; }
}
