using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Beta1;
internal sealed record WindowTask(IntPtr Handle,string Title,string Executable,BitmapSource? Icon);
internal static class WindowTasks
{
    public static List<WindowTask> Read(int? excludedProcess=null)
    {
        var result=new List<WindowTask>();
        EnumWindows((h,_)=> {
            if(!IsWindowVisible(h) || GetWindowTextLength(h)==0) return true;
            int style=GetWindowLong(h,-20);
            if((style&0x80)!=0 || ((style&0x40000)==0 && GetWindow(h,4)!=IntPtr.Zero)) return true;
            if(DwmGetWindowAttribute(h,14,out int cloaked,4)==0 && cloaked!=0) return true;
            var cls=new StringBuilder(256); GetClassName(h,cls,256);
            if(cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            GetWindowThreadProcessId(h,out uint pid); if(pid==excludedProcess) return true;
            var title=new StringBuilder(GetWindowTextLength(h)+1); GetWindowText(h,title,title.Capacity);
            string path=""; var process=OpenProcess(0x1000,false,pid);
            if(process!=IntPtr.Zero) { try { var text=new StringBuilder(32768); int size=text.Capacity; if(QueryFullProcessImageName(process,0,text,ref size)) path=text.ToString(); } finally { CloseHandle(process); } }
            // Explorer folders should have their folder icon, not a document or shortcut overlay.
            BitmapSource? icon=path.Length>0?ShellCatalog.Icon(path):null;
            if(icon==null && SendMessageTimeout(h,0x7F,new IntPtr(1),IntPtr.Zero,2,100,out var handle)!=IntPtr.Zero && handle!=IntPtr.Zero)
                try { icon=Imaging.CreateBitmapSourceFromHIcon(handle,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions()); icon.Freeze(); } catch { }
            result.Add(new(h,title.ToString(),path,icon)); return true;
        },IntPtr.Zero);
        return result;
    }
    public static IntPtr Foreground => GetForegroundWindow();
    public static void Minimize(IntPtr window) { if(IsWindow(window)) ShowWindowAsync(window,6); }
    public static void Activate(IntPtr window,bool toggle=true)
    {
        if(!IsWindow(window)) return;
        if(toggle && Foreground==window && !IsIconic(window)) { ShowWindowAsync(window,6); return; }
        if(IsIconic(window)) ShowWindowAsync(window,9);
        else ShowWindowAsync(window,5);
        SetForegroundWindow(window);
    }
    static readonly Dictionary<string,string> targets=new(StringComparer.OrdinalIgnoreCase);
    public static string Target(Shortcut shortcut)
    {
        if(targets.TryGetValue(shortcut.Path,out var value)) return value;
        string path=shortcut.Path;
        if(Path.GetExtension(path).Equals(".lnk",StringComparison.OrdinalIgnoreCase))
        {
            object? shell=null,link=null;
            try { shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); link=((dynamic)shell!).CreateShortcut(path); string target=((dynamic)link).TargetPath; if(target.Length>0) path=Environment.ExpandEnvironmentVariables(target); }
            catch(COMException) { }
            finally { if(link!=null) Marshal.FinalReleaseComObject(link); if(shell!=null) Marshal.FinalReleaseComObject(shell); }
        }
        return targets[shortcut.Path]=path;
    }
    public static bool Matches(Shortcut pin,WindowTask task) => task.Executable.Length>0 && Target(pin).Equals(task.Executable,StringComparison.OrdinalIgnoreCase);
    delegate bool EnumProc(IntPtr h,IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback,IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h,uint cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int index);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder text,int size);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder text,int size);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr h,int command);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h,uint msg,IntPtr w,IntPtr l,uint flags,uint timeout,out IntPtr result);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h,int attribute,out int value,int size);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder path,ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
