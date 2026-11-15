using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
namespace Beta1;
internal static class ShellActions
{
    public static void Start() => Later(()=>Shortcut(0x11,0x1B)); // Ctrl+Esc works with the taskbar clipped.
    public static void Search() => Later(()=>Shortcut(0x5B,0x53)); // Win+S
    public static void Network() => Later(()=>Process.Start(new ProcessStartInfo("ms-availablenetworks:") { UseShellExecute=true }));
    static void Later(Action action)
    {
        var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(160) };
        timer.Tick+=(_,_)=> { timer.Stop(); action(); }; timer.Start();
    }
    internal static void Shortcut(ushort modifier,ushort key)
    {
        var input=new[] { Key(modifier,false),Key(key,false),Key(key,true),Key(modifier,true) };
        if(SendInput((uint)input.Length,input,Marshal.SizeOf<INPUT>())!=input.Length) throw new InvalidOperationException("Windows не приняла сочетание клавиш. Проверьте, что Бета 1 и активное приложение запущены с одинаковыми правами.");
    }
    internal static void Escape() { var input=new[] { Key(0x1B,false),Key(0x1B,true) }; SendInput(2,input,Marshal.SizeOf<INPUT>()); }
    static INPUT Key(ushort key,bool up) => new() { Type=1, Data=new UNION { Keyboard=new KEYBOARD { Key=key, Flags=up?2u:0 } } };
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint Type; public UNION Data; }
    [StructLayout(LayoutKind.Explicit,Size=32)] struct UNION { [FieldOffset(0)] public KEYBOARD Keyboard; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBOARD { public ushort Key,Scan; public uint Flags,Time; public IntPtr Extra; }
    [DllImport("user32.dll")] static extern uint SendInput(uint count,INPUT[] inputs,int size);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string cls,string? title);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h,uint msg,IntPtr w,IntPtr l);
}
