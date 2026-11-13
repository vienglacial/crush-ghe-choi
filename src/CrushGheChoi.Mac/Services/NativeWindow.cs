using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace CrushGheChoi.Services;

/// <summary>
/// Các tinh chỉnh cửa sổ mà Avalonia không có sẵn: cho chuột bấm xuyên qua cửa sổ crush, không đổ bóng,
/// hiện trên mọi desktop. Mỗi hệ điều hành một cách; lỗi ở đây chỉ được ghi nhật ký, không làm app dừng.
/// </summary>
internal static class NativeWindow
{
    public static void MakeClickThroughOverlay(Window window)
    {
        try
        {
            var handle = window.TryGetPlatformHandle();
            if (handle is null || handle.Handle == IntPtr.Zero)
            {
                Log.Warn("Không lấy được cửa sổ hệ thống của crush; chuột có thể không bấm xuyên qua được.");
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                Mac.MakeOverlay(handle.Handle);
            }
            else if (OperatingSystem.IsWindows())
            {
                Win.MakeOverlay(handle.Handle);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Không chỉnh được cửa sổ crush", ex);
        }
    }

    /// <summary>Đưa app lên trước để cửa sổ Cài đặt không bị nằm sau app khác (app không có biểu tượng ở Dock).</summary>
    public static void ActivateApp()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            Mac.ActivateApp();
        }
        catch (Exception ex)
        {
            Log.Warn($"Không đưa được app lên trước: {ex.Message}");
        }
    }

    private static class Mac
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        // NSWindowCollectionBehavior: canJoinAllSpaces | stationary | ignoresCycle | fullScreenAuxiliary
        private const ulong OverlayCollectionBehavior = (1UL << 0) | (1UL << 4) | (1UL << 6) | (1UL << 8);

        public static void MakeOverlay(IntPtr handle)
        {
            var window = handle;

            // Avalonia trả NSWindow; nếu là NSView thì lấy cửa sổ chứa nó.
            if (!Responds(window, "setIgnoresMouseEvents:"))
            {
                if (!Responds(window, "window"))
                {
                    Log.Warn("Cửa sổ hệ thống không nhận lệnh bỏ qua chuột.");
                    return;
                }

                window = SendPtr(window, Sel("window"));
                if (window == IntPtr.Zero || !Responds(window, "setIgnoresMouseEvents:"))
                {
                    return;
                }
            }

            SendBool(window, Sel("setIgnoresMouseEvents:"), true);
            if (Responds(window, "setHasShadow:"))
            {
                SendBool(window, Sel("setHasShadow:"), false);
            }

            if (Responds(window, "setCollectionBehavior:"))
            {
                SendULong(window, Sel("setCollectionBehavior:"), OverlayCollectionBehavior);
            }
        }

        public static void ActivateApp()
        {
            var cls = objc_getClass("NSApplication");
            if (cls == IntPtr.Zero)
            {
                return;
            }

            var app = SendPtr(cls, Sel("sharedApplication"));
            if (app != IntPtr.Zero && Responds(app, "activateIgnoringOtherApps:"))
            {
                SendBool(app, Sel("activateIgnoringOtherApps:"), true);
            }
        }

        private static IntPtr Sel(string name) => sel_registerName(name);

        private static bool Responds(IntPtr obj, string selector) =>
            obj != IntPtr.Zero && SendSelBool(obj, Sel("respondsToSelector:"), Sel(selector));

        [DllImport(ObjC)]
        private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(ObjC)]
        private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendPtr(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendULong(IntPtr receiver, IntPtr selector, ulong value);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SendSelBool(IntPtr receiver, IntPtr selector, IntPtr argument);
    }

    /// <summary>Chỉ dùng khi chạy thử bản này trên Windows.</summary>
    private static class Win
    {
        private const int GwlExStyle = -20;
        private const long WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x80000, WsExNoActivate = 0x08000000;

        public static void MakeOverlay(IntPtr hwnd)
        {
            var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExTransparent | WsExLayered | WsExToolWindow | WsExNoActivate));
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    }
}
