using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Forms=System.Windows.Forms;
namespace Beta1;
internal static class IntegrationChecks
{
    public static int Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"Beta1-check-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var desktop=Path.Combine(root,"desktop"); Directory.CreateDirectory(desktop);
            var folder=Path.Combine(desktop,"Проект"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder,"nested.txt"),"nested");
            File.WriteAllText(Path.Combine(desktop,"Notes.txt"),"notes");
            var shortcut=Path.Combine(desktop,"Test.lnk");
            dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic link=shell.CreateShortcut(shortcut); link.TargetPath=Environment.ProcessPath; link.Save(); Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell);
            var entries=ShellCatalog.Desktop(desktop);
            Require(entries.Count==3 && entries.Count(e=>e.IsFolder)==1,"desktop only, no recursion");
            Require(entries.All(e=>!e.Name.Contains("nested")),"no nested apps");
            var source=ShellCatalog.Icon(Environment.ProcessPath!)!; var fromLink=ShellCatalog.Icon(shortcut)!;
            Require(source!=null && fromLink!=null,"icons loaded");
            Require(Pixels(source!).SequenceEqual(Pixels(fromLink!)),"shortcut icon must equal original icon, without arrow overlay");
            var directoryIcon=ShellCatalog.Icon(folder)!;
            Require(directoryIcon!=null && !Pixels(source!).SequenceEqual(Pixels(directoryIcon!)),"application and folder must not share a generic fallback icon");
            var before=Directory.GetFiles(desktop).ToDictionary(p=>p,File.ReadAllBytes);
            using var window=new Forms.Form { ShowInTaskbar=false }; window.Show(); Forms.Application.DoEvents();
            var controls=new CommonControls { Size=8, Classes=1 }; InitCommonControlsEx(ref controls);
            var list=CreateWindowEx(0,"SysListView32","Beta1 test",0x50000000,0,0,200,200,window.Handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            Require(list!=IntPtr.Zero,"native list view created");
            var integration=new DesktopIntegration(Path.Combine(root,"state"));
            Require(IsWindowVisible(list),"fixture icon layer initially visible");
            integration.HideIconLayer(list); Require(!IsWindowVisible(list),"icon layer hidden");
            foreach(var pair in before) Require(File.Exists(pair.Key)&&File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value),"files remain unchanged while hidden");
            new DesktopIntegration(Path.Combine(root,"state")).Recover(); Require(IsWindowVisible(list),"layer restored from journal");
            DestroyWindow(list); window.Close();
            using var existing=new Forms.Form { Text="Beta1 existing-window fixture", ShowInTaskbar=true }; existing.Show(); Forms.Application.DoEvents();
            var beforeStart=WindowTasks.Read(); Require(beforeStart.Any(w=>w.Handle==existing.Handle),"already-open window discovered");
            var pinned=new Shortcut("Fixture",shortcut); Require(WindowTasks.Matches(pinned,beforeStart.First(w=>w.Handle==existing.Handle)),"running window matched to pinned shortcut target");
            existing.WindowState=Forms.FormWindowState.Minimized; Forms.Application.DoEvents();
            Require(WindowTasks.Read().Any(w=>w.Handle==existing.Handle),"minimized window remains in dock");
            WindowTasks.Activate(existing.Handle,false); Forms.Application.DoEvents();
            var saved=NativeTray.Capture(existing.Handle);
            NativeTray.Clip(existing.Handle,new NativeTray.Rect { Right=100,Bottom=80 },new NativeTray.Rect { Right=300,Bottom=200 },true);
            Require(GetWindowRgnBox(existing.Handle,out var clipped)>0 && clipped.Right==100,"native tray region can be clipped");
            NativeTray.Restore(saved); Require(GetWindowRgnBox(existing.Handle,out _) == 0,"original region restored");
            var closedHandle=existing.Handle; existing.Close(); Forms.Application.DoEvents();
            Require(!WindowTasks.Read().Any(w=>w.Handle==closedHandle),"closed window removed");
            // Upgrade from 0.2 must restore formerly moved links without overwriting new files.
            var old=Path.Combine(root,"legacy.lnk"); File.WriteAllText(old,"old");
            var state=Path.Combine(root,"legacy"); Directory.CreateDirectory(state);
            File.WriteAllText(Path.Combine(state,"desktop-recovery.json"),JsonSerializer.Serialize(new DesktopIntegration.Journal { Links=new() { new(shortcut,old) } }));
            new DesktopIntegration(state).Recover();
            Require(File.Exists(shortcut)&&Directory.GetFiles(desktop,"Test (Бета 1 *).lnk").Length==1,"legacy migration preserves conflicts");
            ShellActions.Shortcut(0x11,0x1B); System.Threading.Thread.Sleep(1800); string startProcess=WindowTasks.ForegroundProcessName(); CaptureScreen("start-control.png"); ShellActions.Escape();
            System.Threading.Thread.Sleep(200); ShellActions.Shortcut(0x5B,0x53); System.Threading.Thread.Sleep(1800); string searchProcess=WindowTasks.ForegroundProcessName(); CaptureScreen("search-control.png"); ShellActions.Escape();
            foreach(double dpi in new[] { 1.0,1.25,1.5,2.0 })
            {
                var nativeDip=new System.Windows.Rect(1580/dpi,1020/dpi,340/dpi,60/dpi);
                var frame=TrayLayout.Frame(nativeDip,new System.Windows.Size(1920/dpi,1080/dpi),1);
                Require(Math.Abs(frame.Right-1920/dpi)<.01 && frame.Contains(nativeDip),"unified tray contains native controls and aligns right at DPI "+dpi);
                Require(frame.Left<nativeDip.Left,"Beta icon has its own space before overflow");
            }
            bool trayFound=NativeTray.Find(out var actualTaskbar,out var trayArea,out var taskbarArea);
            if(trayFound)
            {
                var original=NativeTray.Capture(actualTaskbar);
                try
                {
                    NativeTray.Clip(actualTaskbar,trayArea,taskbarArea,true);
                    System.Threading.Thread.Sleep(250);
                    Require(GetWindowRgnBox(actualTaskbar,out var nativeClip)>0 && nativeClip.Right-nativeClip.Left==trayArea.Width,"actual Explorer tray region applied");
                    CaptureScreen("native-tray-control.png");
                    var frameWindow=new System.Windows.Window { Width=400,Height=70,WindowStyle=System.Windows.WindowStyle.None,ResizeMode=System.Windows.ResizeMode.NoResize,AllowsTransparency=true,Background=System.Windows.Media.Brushes.Transparent,ShowInTaskbar=false,ShowActivated=false,Topmost=true };
                    var badge=new System.Windows.Controls.Button { Content="B",Width=36,Height=36,HorizontalAlignment=System.Windows.HorizontalAlignment.Left,Margin=new System.Windows.Thickness(12,0,0,0) };
                    frameWindow.Content=new System.Windows.Controls.Border { Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32,42,38)),BorderBrush=System.Windows.Media.Brushes.Gray,BorderThickness=new System.Windows.Thickness(1),CornerRadius=new System.Windows.CornerRadius(16),Child=badge };
                    try
                    {
                        frameWindow.Show();
                        var transform=System.Windows.PresentationSource.FromVisual(frameWindow)!.CompositionTarget!.TransformFromDevice;
                        var topLeft=transform.Transform(new System.Windows.Point(trayArea.Left,trayArea.Top)); var bottomRight=transform.Transform(new System.Windows.Point(trayArea.Right,trayArea.Bottom));
                        var frame=TrayLayout.Frame(new System.Windows.Rect(topLeft,bottomRight),new System.Windows.Size(System.Windows.SystemParameters.PrimaryScreenWidth,System.Windows.SystemParameters.PrimaryScreenHeight),1);
                        frameWindow.Left=frame.Left; frameWindow.Top=frame.Top; frameWindow.Width=frame.Width; frameWindow.Height=frame.Height; frameWindow.UpdateLayout();
                        Pump(300); NativeTray.Raise(); Pump(300); CaptureScreen("unified-tray-control.png");
                        var automation=System.Windows.Automation.AutomationElement.FromHandle(actualTaskbar);
                        var overflow=automation.FindFirst(System.Windows.Automation.TreeScope.Descendants,new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty,"Show hidden icons"));
                        if(overflow!=null && overflow.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern,out var invoke))
                        {
                            ((System.Windows.Automation.InvokePattern)invoke).Invoke(); Pump(400); CaptureScreen("overflow-tray-control.png"); ShellActions.Escape();
                        }
                    }
                    finally { frameWindow.Close(); }
                }
                finally { NativeTray.Restore(original); }
            }
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!,"integration-report.txt"),$"Unified tray geometry at 100/125/150/200% DPI: PASS\nExisting and minimized windows: PASS\nPinned target matching: PASS\nNative region restoration: PASS\nExplorer tray detected: {trayFound}, width {trayArea.Width}\nStart foreground: {startProcess}\nSearch foreground: {searchProcess}\n");
            return 0;
        }
        catch(Exception ex) { File.WriteAllText(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "integration-error.txt"),ex.ToString()); return 4; }
        finally { try { Directory.Delete(root,true); } catch { } }
    }
    static void Pump(int milliseconds)
    {
        var frame=new System.Windows.Threading.DispatcherFrame(); var timer=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(milliseconds) }; timer.Tick+=(_,_)=> { timer.Stop(); frame.Continue=false; }; timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
    static void CaptureScreen(string name)
    {
        var bounds=Forms.Screen.PrimaryScreen!.Bounds;
        using var image=new System.Drawing.Bitmap(bounds.Width,bounds.Height);
        using var graphics=System.Drawing.Graphics.FromImage(image); graphics.CopyFromScreen(bounds.Location,System.Drawing.Point.Empty,bounds.Size);
        image.Save(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!,name),System.Drawing.Imaging.ImageFormat.Png);
    }
    static byte[] Pixels(BitmapSource bitmap) { int stride=(bitmap.PixelWidth*bitmap.Format.BitsPerPixel+7)/8; var bytes=new byte[stride*bitmap.PixelHeight]; bitmap.CopyPixels(bytes,stride,0); return bytes; }
    static void Require(bool test,string name) { if(!test) throw new InvalidOperationException(name); }
    static int GetWindowRgnBox(IntPtr window,out NativeTray.Rect box)
    {
        var region=CreateRectRgn(0,0,0,0); try { int status=GetWindowRgn(window,region); GetRgnBox(region,out box); return status; } finally { DeleteObject(region); }
    }
    [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr window,IntPtr region);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l,int t,int r,int b);
    [DllImport("gdi32.dll")] static extern int GetRgnBox(IntPtr region,out NativeTray.Rect box);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr region);
    [StructLayout(LayoutKind.Sequential)] struct CommonControls { public uint Size,Classes; }
    [DllImport("comctl32.dll")] static extern bool InitCommonControlsEx(ref CommonControls controls);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr handle);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr handle);
}
