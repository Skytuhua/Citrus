using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Citrus.Core;

namespace Citrus.Windows.Presentation;

/// <summary>Two raw HWNDs. No WPF/OLE/input registration; the hidden owner never belongs to the wheel.</summary>
internal sealed class NativeBackdropWindow : IDisposable
{
    private readonly string className = "Citrus.Backdrop." + Guid.NewGuid().ToString("N");
    private readonly BackdropNativeMethods.WindowProcedure procedure;
    private readonly nint instance = BackdropNativeMethods.GetModuleHandle(null);
    private bool registered;
    public nint Handle { get; private set; }
    public nint Owner { get; private set; }

    public NativeBackdropWindow()
    {
        procedure = WindowProc;
        try
        {
            var windowClass = new BackdropNativeMethods.WindowClass
            {
                Size = (uint)Marshal.SizeOf<BackdropNativeMethods.WindowClass>(), Procedure = procedure,
                Instance = instance, ClassName = className
            };
            if (BackdropNativeMethods.RegisterClassEx(ref windowClass) == 0) throw new Win32Exception();
            registered = true;
            const int ex = BackdropNativeMethods.WsExNoActivate | BackdropNativeMethods.WsExToolWindow | BackdropNativeMethods.WsExTransparent;
            const uint style = (uint)(BackdropNativeMethods.WsPopup | BackdropNativeMethods.WsDisabled);
            Owner = BackdropNativeMethods.CreateWindowEx(ex, className, "Citrus backdrop lifetime", style, 0, 0, 0, 0, 0, 0, instance, 0);
            if (Owner == 0) throw new Win32Exception();
            Handle = BackdropNativeMethods.CreateWindowEx(ex, className, "Citrus Acrylic companion", style, 0, 0, 0, 0, Owner, 0, instance, 0);
            if (Handle == 0) throw new Win32Exception();
        }
        catch { Dispose(); throw; }
    }

    public void PlaceBehind(nint wheel, ScreenRect bounds, double scale)
    {
        // Inward rounding: the clip cannot protrude beyond the fixed OLE capture rectangle.
        int left = (int)Math.Ceiling(8 * scale), top = left;
        int right = (int)Math.Floor(360 * scale), bottom = right;
        if (right > bounds.Width || bottom > bounds.Height || right <= left) throw new ArgumentOutOfRangeException(nameof(bounds));
        nint region = BackdropNativeMethods.CreateEllipticRgn(left, top, right, bottom);
        if (region == 0) throw new Win32Exception();
        if (BackdropNativeMethods.SetWindowRgn(Handle, region, false) == 0)
        {
            BackdropNativeMethods.DeleteObject(region); // On success the OS owns the region.
            throw new Win32Exception();
        }
        nint batch = BackdropNativeMethods.BeginDeferWindowPos(2);
        if (batch == 0) throw new Win32Exception();
        batch = BackdropNativeMethods.DeferWindowPos(batch, wheel, -1, 0, 0, 0, 0,
            BackdropNativeMethods.NoActivate | BackdropNativeMethods.NoMove | BackdropNativeMethods.NoSize);
        if (batch == 0) throw new Win32Exception();
        batch = BackdropNativeMethods.DeferWindowPos(batch, Handle, wheel, (int)bounds.Left, (int)bounds.Top,
            (int)bounds.Width, (int)bounds.Height, BackdropNativeMethods.NoActivate | BackdropNativeMethods.ShowWindowFlag);
        if (batch == 0 || !BackdropNativeMethods.EndDeferWindowPos(batch)) throw new Win32Exception();
    }

    public void Hide() { if (Handle != 0) BackdropNativeMethods.ShowWindow(Handle, 0); }
    public void Dispose()
    {
        Hide();
        if (Handle != 0) { BackdropNativeMethods.DestroyWindow(Handle); Handle = 0; }
        if (Owner != 0) { BackdropNativeMethods.DestroyWindow(Owner); Owner = 0; }
        if (registered) { BackdropNativeMethods.UnregisterClass(className, instance); registered = false; }
        GC.KeepAlive(procedure);
    }
    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
        => message switch
        {
            0x21 => 3, // WM_MOUSEACTIVATE: MA_NOACTIVATE
            0x84 => -1, // WM_NCHITTEST: HTTRANSPARENT (WS_DISABLED is the input exclusion guarantee)
            0x14 => 1, // Background belongs to DWM, never paint a software desktop texture.
            _ => BackdropNativeMethods.DefWindowProc(hwnd, message, wParam, lParam)
        };
}
