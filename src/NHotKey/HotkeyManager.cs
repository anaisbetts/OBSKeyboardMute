using Splat;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace NHotkey.WindowsForms
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Microsoft.Design",
        "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
        Justification = "This is a singleton; disposing it would break it")]
    public class HotkeyManager : HotkeyManagerBase
    {
        public static HotkeyManager Current {
            get {
                if (ModeDetector.InUnitTestRunner()) {
                    return LazyInitializer.TestInstance.Value;
                } else {
                    return LazyInitializer.Instance;
                }
            }
        }

        private static class LazyInitializer
        {
            static LazyInitializer() { }

            public static readonly ThreadLocal<HotkeyManager> TestInstance = new ThreadLocal<HotkeyManager>(() => new HotkeyManager());
            public static readonly HotkeyManager Instance = new HotkeyManager();
        }

        // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
        private readonly MessageWindow _messageWindow;

        private HotkeyManager()
        {
            _messageWindow = new MessageWindow(this);
            SetHwnd(_messageWindow.Handle);
        }

        public void AddOrReplace(string name, Keys keys, bool noRepeat, EventHandler<HotkeyEventArgs> handler)
        {
            var flags = GetFlags(keys, noRepeat);
            var vk = unchecked((uint)(keys & ~Keys.Modifiers));
            AddOrReplace(name, vk, flags, handler);
        }

        public void AddOrReplace(string name, Keys keys, EventHandler<HotkeyEventArgs> handler)
        {
            AddOrReplace(name, keys, false, handler);
        }

        private static HotkeyFlags GetFlags(Keys hotkey, bool noRepeat)
        {
            var noMod = hotkey & ~Keys.Modifiers;
            var flags = HotkeyFlags.None;
            if (hotkey.HasFlag(Keys.Alt))
                flags |= HotkeyFlags.Alt;
            if (hotkey.HasFlag(Keys.Control))
                flags |= HotkeyFlags.Control;
            if (hotkey.HasFlag(Keys.Shift))
                flags |= HotkeyFlags.Shift;
            if (noMod == Keys.LWin || noMod == Keys.RWin)
                flags |= HotkeyFlags.Windows;
            if (noRepeat)
                flags |= HotkeyFlags.NoRepeat;
            return flags;
        }

        class MessageWindow : ContainerControl
        {
            private readonly HotkeyManager _hotkeyManager;

            public MessageWindow(HotkeyManager hotkeyManager)
            {
                _hotkeyManager = hotkeyManager;
            }

            protected override CreateParams CreateParams {
                get {
                    var parameters = base.CreateParams;
                    parameters.Parent = HwndMessage;
                    return parameters;
                }
            }

            protected override void WndProc(ref Message m)
            {
                bool handled = false;
                Hotkey hotkey;

                m.Result = _hotkeyManager.HandleHotkeyMessage(Handle, m.Msg, m.WParam, m.LParam, ref handled, out hotkey);
                if (!handled) base.WndProc(ref m);
            }
        }
    }

    public class GlobalKeyboardHookEventArgs : HandledEventArgs
    {
        public GlobalKeyboardHook.KeyboardState KeyboardState { get; private set; }
        public GlobalKeyboardHook.LowLevelKeyboardEvent KeyboardData { get; private set; }

        public GlobalKeyboardHookEventArgs(
            GlobalKeyboardHook.LowLevelKeyboardEvent keyboardData,
            GlobalKeyboardHook.KeyboardState keyboardState
            )
        {
            KeyboardData = keyboardData;
            KeyboardState = keyboardState;
        }
    }

    public class GlobalKeyboardHook : IDisposable
    {
        public event EventHandler<GlobalKeyboardHookEventArgs> KeyboardPressed;

        IntPtr _kbWindowsHookHandle;
        HookProc _kbHookProc;

        public GlobalKeyboardHook()
        {
            _kbWindowsHookHandle = IntPtr.Zero;
            _kbHookProc = LowLevelKeyboardProc; // we must keep alive _hookProc, because GC is not aware about SetWindowsHookEx behaviour.

            _kbWindowsHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _kbHookProc, GetModuleHandle(IntPtr.Zero), 0);
            if (_kbWindowsHookHandle == IntPtr.Zero) {
                int errorCode = Marshal.GetLastWin32Error();
                throw new Win32Exception(errorCode, $"Failed to adjust keyboard hooks for '{Process.GetCurrentProcess().ProcessName}'. Error {errorCode}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}.");
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing) {
                // because we can unhook only in the same thread, not in garbage collector thread
                if (_kbWindowsHookHandle != IntPtr.Zero) {
                    if (!UnhookWindowsHookEx(_kbWindowsHookHandle)) {
                        int errorCode = Marshal.GetLastWin32Error();
                        throw new Win32Exception(errorCode, $"Failed to remove keyboard hooks for '{Process.GetCurrentProcess().ProcessName}'. Error {errorCode}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}.");
                    }

                    _kbWindowsHookHandle = IntPtr.Zero;
                    _kbHookProc -= LowLevelKeyboardProc;
                }
            }
        }

        ~GlobalKeyboardHook()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("Kernel32", SetLastError = true)]
        static extern IntPtr GetModuleHandle(IntPtr justSetItToZero);

        [DllImport("USER32", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, int dwThreadId);

        [DllImport("USER32", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hHook);

        [DllImport("USER32", SetLastError = true)]
        static extern IntPtr CallNextHookEx(IntPtr hHook, int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct LowLevelKeyboardEvent
        {
            public int VirtualCode;
            public int HardwareScanCode;

            public int Flags;
            public int TimeStamp;
            public IntPtr AdditionalInformation;
        }

        public const int WH_KEYBOARD_LL = 13;

        public enum KeyboardState
        {
            KeyDown = 0x0100,
            KeyUp = 0x0101,
            SysKeyDown = 0x0104,
            SysKeyUp = 0x0105
        }

        public IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode != 0) {
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            var wparamTyped = wParam.ToInt32();
            var o = Marshal.PtrToStructure(lParam, typeof(LowLevelKeyboardEvent));
            var p = (LowLevelKeyboardEvent)o;

            var eventArguments = new GlobalKeyboardHookEventArgs(p, (KeyboardState)wparamTyped);

            EventHandler<GlobalKeyboardHookEventArgs> handler = KeyboardPressed;
            handler?.Invoke(this, eventArguments);

            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
    }

    public class GlobalMouseHookEventArgs : HandledEventArgs
    {
        public GlobalMouseHook.LowLevelMouseEvent MouseData { get; private set; }
        public GlobalMouseHook.MouseMessages MouseMessage { get; private set; }

        public GlobalMouseHookEventArgs(
            GlobalMouseHook.LowLevelMouseEvent mouseData,
            GlobalMouseHook.MouseMessages mouseMessage
            )
        {
            MouseData = mouseData;
            MouseMessage = mouseMessage;
        }
    }

    public class GlobalMouseHook : IDisposable
    {
        public event EventHandler<GlobalMouseHookEventArgs> MouseInput;

        IntPtr _mouseWindowsHookHandle;
        HookProc _mouseHookProc;

        public GlobalMouseHook()
        {
            _mouseWindowsHookHandle = IntPtr.Zero;
            _mouseHookProc = LowLevelMouseProc;

            _mouseWindowsHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, GetModuleHandle(IntPtr.Zero), 0);
            if (_mouseWindowsHookHandle == IntPtr.Zero) {
                int errorCode = Marshal.GetLastWin32Error();
                throw new Win32Exception(errorCode, $"Failed to adjust mouse hooks for '{Process.GetCurrentProcess().ProcessName}'. Error {errorCode}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}.");
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing) {
                // because we can unhook only in the same thread, not in garbage collector thread
                if (_mouseWindowsHookHandle != IntPtr.Zero) {
                    if (!UnhookWindowsHookEx(_mouseWindowsHookHandle)) {
                        int errorCode = Marshal.GetLastWin32Error();
                        throw new Win32Exception(errorCode, $"Failed to remove keyboard hooks for '{Process.GetCurrentProcess().ProcessName}'. Error {errorCode}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}.");
                    }

                    _mouseWindowsHookHandle = IntPtr.Zero;
                    _mouseHookProc -= LowLevelMouseProc;
                }

            }
        }

        ~GlobalMouseHook()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("Kernel32", SetLastError = true)]
        static extern IntPtr GetModuleHandle(IntPtr justSetItToZero);

        [DllImport("USER32", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, int dwThreadId);

        [DllImport("USER32", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hHook);

        [DllImport("USER32", SetLastError = true)]
        static extern IntPtr CallNextHookEx(IntPtr hHook, int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct LowLevelKeyboardInputEvent
        {
            public int VirtualCode;
            public int HardwareScanCode;

            public int Flags;
            public int TimeStamp;
            public IntPtr AdditionalInformation;
        }

        public enum MouseMessages
        {
            WM_LBUTTONDOWN = 0x0201,
            WM_LBUTTONUP = 0x0202,
            WM_MOUSEMOVE = 0x0200,
            WM_MOUSEWHEEL = 0x020A,
            WM_RBUTTONDOWN = 0x0204,
            WM_RBUTTONUP = 0x0205
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LowLevelMouseEvent
        {
            public Point pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;

        public enum KeyboardState
        {
            KeyDown = 0x0100,
            KeyUp = 0x0101,
            SysKeyDown = 0x0104,
            SysKeyUp = 0x0105
        }

        public IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode != 0) {
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            var wparamTyped = wParam.ToInt32();
            var o = Marshal.PtrToStructure(lParam, typeof(LowLevelMouseEvent));
            var p = (LowLevelMouseEvent)o;

            var eventArguments = new GlobalMouseHookEventArgs(p, (MouseMessages)wparamTyped);

            EventHandler<GlobalMouseHookEventArgs> handler = MouseInput;
            handler?.Invoke(this, eventArguments);

            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
    }
}
