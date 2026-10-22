using System.Windows.Threading;
using Citrus.Windows.Presentation;

namespace Citrus.Windows;

internal interface IDragSourceLifetime : IDisposable
{
    bool HasCompleted(bool leftReleased);
}

public partial class WheelWindow
{
    private readonly Func<IDragSourceLifetime> captureDragSource;
    private readonly Func<bool> physicalLeftButtonDown;
    private IDragSourceLifetime? dragSourceLifetime;
    private bool cancelDraining, cancelDrainReleased, cancelDrainLeave;
    private long cancelDrainGeneration, cancelDrainEpoch, cancelDrainLeaveEpoch;
    private DispatcherTimer? cancelDrainProbe;

    /// <summary>Called synchronously by the low-level hook before OLE can reenter this target.</summary>
    public bool TryObserveCancellation()
    {
        Dispatcher.VerifyAccess();
        if (presentationReleased || cancelDraining || !IsVisible || !session.IsConfirmed ||
            (Presentation.Opacity <= 0 && !pendingMaterial) || outsideCapture is null) return false;
        // Snapshot the admitted generation before cancelling WheelSession clears its payload.
        cancelDrainGeneration = generation;
        cancelDrainEpoch = ++inputEpoch;
        cancelDraining = true;
        cancelDrainReleased = cancelDrainLeave = false;
        ++cancelDrainLeaveEpoch;
        acceptsEntry = false;
        session.Cancel(generation);
        timer.Stop();
        releaseCleanup?.Stop(); releaseCleanup = null;
        CancelWork(); CancelMaterialPresentation();
        pendingPlacement?.Abort(); pendingPlacement = null;
        preparing = false; armed = false; selected = -1; navigation = 0;
        operations = []; availableOperations.Clear();
        ++presentationEpoch;
        audio.Stop(); CancelEmphasis();
        glass.Stop(); backdrop.Hide(generation);
        SetPresentationVisible(false);
        Sectors.Children.Clear(); petals.Clear();
        // Hiding an owner also hides its owned capture. Keep both HWNDs shown until OLE finishes.
        StartCancelDrainProbe();
        // A release may have preceded this right-button callback. Do not wait for
        // another hook event that can no longer arrive for the admitted drag.
        if (!physicalLeftButtonDown()) ObserveCancelledRelease();
        return true;
    }

    private void StartCancelDrainProbe()
    {
        cancelDrainProbe?.Stop();
        long expectedGeneration = cancelDrainGeneration, expectedEpoch = cancelDrainEpoch;
        var probe = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        cancelDrainProbe = probe;
        probe.Tick += (_, _) =>
        {
            if (!IsCurrentCancelDrain(expectedGeneration, expectedEpoch)) { probe.Stop(); return; }
            // Recover if the hook missed LEFT_UP or was removed while Explorer's
            // drag loop was unwinding. A still-held drag never expires by elapsed time.
            if (!cancelDrainReleased && !physicalLeftButtonDown()) ObserveCancelledRelease();
            // The full-desktop target may never receive Leave; capture disappearance
            // after actual release and retained source exit are sufficient corroboration.
            if (dragSourceLifetime?.HasCompleted(cancelDrainReleased) == true)
                FinishCancelDrain(expectedGeneration, expectedEpoch);
        };
        probe.Start();
    }

    private void ObserveCancelledRelease()
    {
        if (!cancelDraining || cancelDrainReleased) return;
        cancelDrainReleased = true;
        // Keep rejecting while OLE delivers Drop/Leave after the hook returns. If
        // neither arrives, an unknown source must not leave a global input shield.
        long expectedGeneration = cancelDrainGeneration, expectedEpoch = cancelDrainEpoch;
        var cleanup = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(250) };
        releaseCleanup = cleanup;
        cleanup.Tick += (_, _) =>
        {
            cleanup.Stop();
            if (ReferenceEquals(releaseCleanup, cleanup)) releaseCleanup = null;
            FinishCancelDrain(expectedGeneration, expectedEpoch);
        };
        cleanup.Start();
    }

    private bool IsCurrentCancelDrain(long expectedGeneration, long expectedEpoch) =>
        cancelDraining && !presentationReleased && generation == expectedGeneration &&
        cancelDrainGeneration == expectedGeneration && cancelDrainEpoch == expectedEpoch;

    private bool RejectCancelledDrag(WheelDragInput e, bool terminalDrop = false, bool resetLeaveCandidate = true)
    {
        if (!cancelDraining) return false;
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (terminalDrop) QueueCancelDrainCompletion(terminalDrop: true);
        else if (resetLeaveCandidate)
        {
            cancelDrainLeave = false;
            ++cancelDrainLeaveEpoch;
        }
        return true;
    }

    private bool ObserveCancelledLeave(WheelDragInput e)
    {
        if (!RejectCancelledDrag(e, resetLeaveCandidate: false)) return false;
        cancelDrainLeave = true;
        ++cancelDrainLeaveEpoch;
        // Native WPF OleDragLeave supplies KeyStates.None even while the mouse is held.
        // The hook or a physical-button probe establishes release. That plus this
        // terminal leave can complete without reopening an unavailable source identity.
        if (cancelDrainReleased || dragSourceLifetime?.HasCompleted(false) == true)
            QueueCancelDrainCompletion(terminalDrop: false);
        // A leave while still held may only mean crossing between our two HWNDs.
        // Keep rejecting until actual release or independently observed source exit.
        return true;
    }

    private void QueueCancelDrainCompletion(bool terminalDrop)
    {
        long expectedGeneration = cancelDrainGeneration, expectedEpoch = cancelDrainEpoch;
        long expectedLeaveEpoch = cancelDrainLeaveEpoch;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!terminalDrop && (!cancelDrainLeave || expectedLeaveEpoch != cancelDrainLeaveEpoch)) return;
            FinishCancelDrain(expectedGeneration, expectedEpoch);
        }));
    }

    private void FinishCancelDrain(long expectedGeneration, long expectedEpoch)
    {
        if (!IsCurrentCancelDrain(expectedGeneration, expectedEpoch)) return;
        cancelDraining = false;
        ++cancelDrainEpoch;
        cancelDrainProbe?.Stop(); cancelDrainProbe = null;
        Dismiss();
    }

    private void ReleaseDragSource()
    {
        dragSourceLifetime?.Dispose(); dragSourceLifetime = null;
    }

    private void StopCancelDrain()
    {
        cancelDraining = false; ++cancelDrainEpoch;
        cancelDrainProbe?.Stop(); cancelDrainProbe = null;
        ReleaseDragSource();
    }
}
