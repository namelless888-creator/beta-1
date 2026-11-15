using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Threading;
using Forms=System.Windows.Forms;
namespace Beta1;
internal sealed class BottomBarController : IDisposable
{
    readonly Window dock,tray;
    readonly Func<bool> autoHide;
    readonly Action<bool> shellVisibility;
    readonly DispatcherTimer timer=new() { Interval=TimeSpan.FromMilliseconds(80) };
    DateTime holdUntil=DateTime.MinValue,leaveAt=DateTime.MinValue;
    bool shown=true;
    double dockTop,trayTop;
    public IntPtr LastExternalForeground { get; private set; }
    public bool Shown=>shown;
    public BottomBarController(Window dock,Window tray,Func<bool> autoHide,Action<bool> shellVisibility,bool start=true)
    {
        this.dock=dock; this.tray=tray; this.autoHide=autoHide; this.shellVisibility=shellVisibility; Relayout();
        timer.Tick+=(_,_)=>Update(); if(start) timer.Start();
        dock.PreviewMouseDown+=(_,_)=>Hold(); tray.PreviewMouseDown+=(_,_)=>Hold();
    }
    public void Relayout() { dock.BeginAnimation(Window.TopProperty,null); tray.BeginAnimation(Window.TopProperty,null); dockTop=dock.Top; trayTop=tray.Top; if(!shown) { dock.Top=SystemParameters.PrimaryScreenHeight-2; tray.Top=SystemParameters.PrimaryScreenHeight-2; } }
    public void Hold() { holdUntil=DateTime.UtcNow.AddMilliseconds(350); SetShown(true,true); }
    public void RevealForTest() { holdUntil=DateTime.UtcNow.AddSeconds(30); SetShown(true,true); }
    void Update()
    {
        var foreground=WindowTasks.Foreground; GetWindowThreadProcessId(foreground,out uint pid);
        if(foreground!=new WindowInteropHelper(dock).Handle && foreground!=new WindowInteropHelper(tray).Handle && foreground!=IntPtr.Zero) LastExternalForeground=foreground;
        var cls=new StringBuilder(256); GetClassName(foreground,cls,256);
        bool desktop=cls.ToString() is "Progman" or "WorkerW" || (pid==Environment.ProcessId && (GetWindowLong(foreground,-20)&0x80)!=0 && foreground!=new WindowInteropHelper(dock).Handle && foreground!=new WindowInteropHelper(tray).Handle);
        var transform=PresentationSource.FromVisual(dock)?.CompositionTarget?.TransformFromDevice;
        var point=new Point(Forms.Cursor.Position.X,Forms.Cursor.Position.Y); if(transform.HasValue) point=transform.Value.Transform(point);
        var screen=new Rect(0,0,SystemParameters.PrimaryScreenWidth,SystemParameters.PrimaryScreenHeight);
        bool bottom=point.X>=screen.Left && point.X<=screen.Right && point.Y>=screen.Bottom-4 && point.Y<=screen.Bottom;
        bool over=shown && (new Rect(dock.Left-8,dockTop-8,dock.Width+16,dock.Height+40).Contains(point) || point.X>=tray.Left-8 && point.Y>=trayTop-10 && point.Y<=screen.Bottom);
        var gui=new GUI { Size=Marshal.SizeOf<GUI>() }; bool menu=GetGUIThreadInfo(0,ref gui) && (gui.Flags&0x1C)!=0 || MenuOpen(dock) || MenuOpen(tray);
        bool overflow=Visible("NotifyIconOverflowWindow") || Visible("TopLevelWindowForOverflowXamlIsland");
        bool desired=!autoHide() || desktop || (bottom && desktop) || (over && (desktop || pid==Environment.ProcessId)) || menu || overflow || DateTime.UtcNow<holdUntil;
        if(desired) { leaveAt=DateTime.MinValue; SetShown(true,!dock.Topmost); }
        else { if(leaveAt==DateTime.MinValue) leaveAt=DateTime.UtcNow; if((DateTime.UtcNow-leaveAt).TotalMilliseconds>120) SetShown(false); }
    }
    void SetShown(bool value,bool force=false)
    {
        if(value==shown && !force) return;
        shown=value;
        foreach(var w in new[] { dock })
        {
            double target=value?(w==dock?dockTop:trayTop):SystemParameters.PrimaryScreenHeight-2;
            w.Topmost=value;
            w.BeginAnimation(Window.TopProperty,new DoubleAnimation(w.Top,target,TimeSpan.FromMilliseconds(110)) { EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut }, FillBehavior=FillBehavior.HoldEnd });
        }
        shellVisibility(true);
    }
    static bool MenuOpen(DependencyObject root)
    {
        if(root is FrameworkElement element && element.ContextMenu?.IsOpen==true) return true;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) if(MenuOpen(VisualTreeHelper.GetChild(root,i))) return true;
        return false;
    }
    static bool Visible(string cls) { var h=FindWindow(cls,null); return h!=IntPtr.Zero && IsWindowVisible(h); }
    public void Dispose() { timer.Stop(); dock.BeginAnimation(Window.TopProperty,null); tray.BeginAnimation(Window.TopProperty,null); }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L,T,R,B; }
    [StructLayout(LayoutKind.Sequential)] struct GUI { public int Size; public uint Flags; public IntPtr Active,Focus,Capture,MenuOwner,MoveSize,Caret; public RECT Rect; }
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int index);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread,ref GUI info);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder name,int size);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string cls,string? title);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
}
