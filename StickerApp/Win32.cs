using System.Runtime.InteropServices;
using System.Text;

namespace StickerApp;

public static class Win32
{
    // Hooks
    public const int WH_MOUSE_LL    = 14;
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_LBUTTONUP    = 0x0202;
    public const int WM_RBUTTONDOWN  = 0x0204;
    public const int WM_RBUTTONUP    = 0x0205;
    public const int WM_XBUTTONDOWN  = 0x020B;
    public const int WM_XBUTTONUP    = 0x020C;
    public const int WM_MBUTTONDOWN  = 0x0207;
    public const int WM_MBUTTONUP    = 0x0208;
    public const int WM_KEYDOWN      = 0x0100;
    public const int XBUTTON1        = 0x0001;
    public const int XBUTTON2        = 0x0002;

    // Virtual keys
    public const int VK_LBUTTON = 0x01;
    public const int VK_RETURN = 0x0D;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_BACK   = 0x08;
    public const int VK_TAB    = 0x09;
    public const int VK_HOME   = 0x24;
    public const int VK_LEFT   = 0x25;
    public const int VK_UP     = 0x26;
    public const int VK_RIGHT  = 0x27;
    public const int VK_DOWN   = 0x28;
    public const int VK_DELETE = 0x2E;
    public const int VK_F4     = 0x73;
    public const int VK_END    = 0x23;
    public const int VK_SHIFT  = 0x10;
    public const int VK_CTRL   = 0x11;
    public const int VK_ALT    = 0x12;
    // Low-level keyboard hook delivers L/R-specific virtual key codes for the
    // modifier keys instead of the merged 0x10/0x11/0x12 codes, so anything
    // that filters modifier keystrokes must include both variants.
    public const int VK_LSHIFT   = 0xA0;
    public const int VK_RSHIFT   = 0xA1;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_LMENU    = 0xA4;
    public const int VK_RMENU    = 0xA5;
    public const int VK_WIN_L   = 0x5B;
    public const int VK_WIN_R   = 0x5C;
    public const int VK_CAPITAL = 0x14;
    public const int VK_SPACE   = 0x20;

    public static bool IsModifierVk(uint vk) =>
        vk == VK_SHIFT || vk == VK_LSHIFT || vk == VK_RSHIFT
        || vk == VK_CTRL || vk == VK_LCONTROL || vk == VK_RCONTROL
        || vk == VK_ALT  || vk == VK_LMENU    || vk == VK_RMENU
        || vk == VK_WIN_L || vk == VK_WIN_R
        || vk == VK_CAPITAL;
    public const int VK_OEM_PLUS = 0xBB;
    public const int VK_ADD      = 0x6B;

    // Window styles
    public const int GWL_EXSTYLE     = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    // Messages
    public const int WM_MOUSEWHEEL     = 0x020A;
    public const int WM_MOUSEACTIVATE  = 0x0021;
    public const int WM_NCLBUTTONDOWN  = 0x00A1;
    public const int MA_NOACTIVATE     = 3;
    public const int WM_NCHITTEST      = 0x0084;
    public const int WM_ENTERSIZEMOVE  = 0x0231;
    public const int WM_EXITSIZEMOVE   = 0x0232;

    // Hit test values
    public const int HTCLIENT      = 1;
    public const int HTCAPTION     = 2;
    public const int HTLEFT        = 10;
    public const int HTRIGHT       = 11;
    public const int HTTOP         = 12;
    public const int HTTOPLEFT     = 13;
    public const int HTTOPRIGHT    = 14;
    public const int HTBOTTOM      = 15;
    public const int HTBOTTOMLEFT  = 16;
    public const int HTBOTTOMRIGHT = 17;

    // SetWindowPos flags
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOMOVE     = 0x0002;
    public const uint SWP_NOSIZE     = 0x0001;
    public const uint SWP_NOZORDER   = 0x0004;
    public const uint SWP_NOREDRAW   = 0x0008;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    // Delegates
    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Imports
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] public static extern bool   UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] public static extern short  GetKeyState(int nVirtKey);
    [DllImport("user32.dll")] public static extern short  GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern bool   GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] public static extern int    GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern int    SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] public static extern bool   SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool   GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool   ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool   GetKeyboardState(byte[] lpKeyState);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint   GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint idThread);
    [DllImport("user32.dll")] public static extern int    GetKeyboardLayoutList(int nBuff, [Out] IntPtr[] lpList);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    public static MSLLHOOKSTRUCT   GetHookStruct(IntPtr lParam)    => Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
    public static KBDLLHOOKSTRUCT  GetKbHookStruct(IntPtr lParam)  => Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

    public static int HiWord(uint val) => (int)(val >> 16);
}
