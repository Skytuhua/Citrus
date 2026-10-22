using Citrus.Core;

namespace Citrus.Windows;

/// <summary>Keep only one pending UI callback and the latest state of each changed job.</summary>
internal sealed class JobProgressDispatcher(Action<Action> post, Action<IReadOnlyCollection<ConversionJob>> refresh)
{
    private readonly object gate = new();
    private readonly HashSet<ConversionJob> pending = [];
    private bool scheduled, stopped;

    internal void Enqueue(ConversionJob job)
    {
        lock (gate)
        {
            if (stopped) return;
            pending.Add(job);
            if (scheduled) return;
            scheduled = true;
        }
        post(Drain);
    }

    private void Drain()
    {
        ConversionJob[] changed;
        lock (gate)
        {
            scheduled = false;
            if (stopped) return;
            changed = pending.ToArray();
            pending.Clear();
        }
        // Events arriving during refresh get another dispatch turn rather than
        // extending this callback indefinitely and delaying input or rendering.
        refresh(changed);
    }

    internal void Stop()
    {
        lock (gate)
        {
            stopped = true;
            pending.Clear();
        }
    }
}
