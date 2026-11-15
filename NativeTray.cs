using System;
using System.Runtime.InteropServices;
using System.Windows.Automation;
namespace Beta1;
// Keep Explorer as owner of notification icons. Clip its taskbar to the live tray:
// no icon scraping, callback guessing, injected hooks, or cross-process reparenting.
internal static class NativeTray
{
    public sealed class SavedRegion
    {
        public long Handle { get; set; }
        public uint Process { get; set; }
        public byte[]? Region { get; set; }
        public bool Visible { get; set; }
        public bool Topmost { get; set; }
    }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; public int Width=>Right-Left; public int Height=>Bottom-Top; }
    public static bool Find(out IntPtr taskbar,out Rect tray,out Rect bar)
    {
        taskbar=FindWindow("Shell_TrayWnd",null); tray=default; bar=default;
        if(taskbar==IntPtr.Zero || !GetWindowRect(taskbar,out bar)) return false;
        var child=FindWindowEx(taskbar,IntPtr.Zero,"TrayNotifyWnd",null);
        if(child!=IntPtr.Zero && GetWindowRect(child,out tray) && tray.Width>0 && tray.Height>0) return true;
        try
        {
            var element=AutomationElement.FromHandle(taskbar).FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"SystemTray.SystemTrayFrame"));
            if(element!=null) { var r=element.Current.BoundingRectangle; if(!r.IsEmpty && r.Width>0) { tray=new Rect { Left=(int)r.Left,Top=(int)r.Top,Right=(int)r.Right,Bottom=(int)r.Bottom }; return true; } }
        }
        catch(ElementNotAvailableException) { } catch(COMException) { } catch(ArgumentException) { } catch(InvalidOperationException) { }
        return false;
    }
    public static uint ProcessId(IntPtr h) { GetWindowThreadProcessId(h,out uint pid); return pid; }
    public static SavedRegion Capture(IntPtr h)
    {
        GetWindowThreadProcessId(h,out uint pid); var saved=new SavedRegion { Handle=h.ToInt64(),Process=pid,Visible=IsWindowVisible(h),Topmost=(GetWindowLong(h,-20)&8)!=0 };
        var region=CreateRectRgn(0,0,0,0);
        try
        {
            if(GetWindowRgn(h,region)>0)
            {
                uint count=GetRegionData(region,0,null); if(count>0 && count<1024*1024) { saved.Region=new byte[count]; GetRegionData(region,count,saved.Region); }
            }
        }
        finally { DeleteObject(region); }
        return saved;
    }
    public static void Clip(IntPtr h,Rect tray,Rect bar,bool visible)
    {
        var region=CreateRectRgn(tray.Left-bar.Left,tray.Top-bar.Top,tray.Right-bar.Left,tray.Bottom-bar.Top);
        if(SetWindowRgn(h,region,true)==0) { DeleteObject(region); throw new InvalidOperationException("Не удалось ограничить область панели задач Windows."); }
        ShowWindow(h,visible?8:0); // no activation
        if(visible) SetWindowPos(h,new IntPtr(-1),0,0,0,0,0x13);
    }
    public static void Restore(SavedRegion saved)
    {
        var h=new IntPtr(saved.Handle); GetWindowThreadProcessId(h,out uint pid); if(pid!=saved.Process) return;
        IntPtr region=IntPtr.Zero;
        if(saved.Region!=null) { region=ExtCreateRegion(IntPtr.Zero,(uint)saved.Region.Length,saved.Region); if(region==IntPtr.Zero) return; }
        if(SetWindowRgn(h,region,true)==0 && region!=IntPtr.Zero) DeleteObject(region);
        ShowWindow(h,saved.Visible?8:0);
        SetWindowPos(h,new IntPtr(saved.Topmost?-1:-2),0,0,0,0,0x13);
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string cls,string? title);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr p,IntPtr a,string cls,string? title);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out Rect rect);
    [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr h,IntPtr region);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h,IntPtr region,bool redraw);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int index);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h,int cmd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int cx,int cy,uint flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int left,int top,int right,int bottom);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern uint GetRegionData(IntPtr region,uint size,[Out] byte[]? buffer);
    [DllImport("gdi32.dll")] static extern IntPtr ExtCreateRegion(IntPtr transform,uint size,byte[] buffer);
}
