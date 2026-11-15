using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Beta1;
// All desktop changes have a durable recovery journal. No shortcut is deleted.
internal sealed class DesktopIntegration
{
    internal sealed record MovedLink(string Original, string Stored);
    internal sealed class Journal
    {
        public List<MovedLink> Links { get; set; } = new();
        public List<long> IconLayers { get; set; } = new();
        public List<long> Taskbars { get; set; } = new();
    }
    readonly string root;
    readonly string journalPath;
    Journal journal = new();
    public DesktopIntegration(string root) { this.root=root; journalPath=Path.Combine(root,"desktop-recovery.json"); }
    void Save()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(journalPath+".tmp",JsonSerializer.Serialize(journal));
        File.Move(journalPath+".tmp",journalPath,true);
    }
    public IReadOnlyList<MovedLink> Links => journal.Links;
    public void Recover()
    {
        if(File.Exists(journalPath)) journal=JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath))??new();
        RestoreAll();
    }
    public void HideTaskbars()
    {
        EnumWindows((h,_) => {
            var name=new System.Text.StringBuilder(256); GetClassName(h,name,name.Capacity);
            if(name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                if(IsWindowVisible(h))
                {
                    if(!journal.Taskbars.Contains(h.ToInt64())) { journal.Taskbars.Add(h.ToInt64()); Save(); }
                    ShowWindow(h,0);
                }
            }
            return true;
        },IntPtr.Zero);
    }
    public void RestoreTaskbars()
    {
        foreach(var value in journal.Taskbars)
        {
            var handle=new IntPtr(value); var name=new System.Text.StringBuilder(256); GetClassName(handle,name,256);
            if(name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") ShowWindow(handle,5);
        }
        journal.Taskbars.Clear(); Save();
    }
    public void HideDesktopIcons()
    {
        EnumWindows((window,_)=> {
            var view=FindWindowEx(window,IntPtr.Zero,"SHELLDLL_DefView",null);
            if(view!=IntPtr.Zero)
            {
                var list=FindWindowEx(view,IntPtr.Zero,"SysListView32",null);
                if(list!=IntPtr.Zero) HideIconLayer(list);
            }
            return true;
        },IntPtr.Zero);
    }
    internal void HideIconLayer(IntPtr layer)
    {
        if(!IsWindowVisible(layer)) return;
        if(!journal.IconLayers.Contains(layer.ToInt64())) { journal.IconLayers.Add(layer.ToInt64()); Save(); }
        ShowWindow(layer,0);
    }
    public void RestoreDesktopIcons()
    {
        foreach(var value in journal.IconLayers)
        {
            var handle=new IntPtr(value); var name=new System.Text.StringBuilder(256); GetClassName(handle,name,256);
            if(name.ToString()=="SysListView32") ShowWindow(handle,5);
        }
        journal.IconLayers.Clear(); Save();
    }
    // Migration only: restore shortcuts moved by versions <= 0.2.0.
    public void RestoreLinks()
    {
        foreach(var item in journal.Links.ToArray())
        {
            try
            {
                if(File.Exists(item.Stored))
                {
                    // Never overwrite a shortcut the user created while Beta 1 was running.
                    var destination=item.Original;
                    if(File.Exists(destination)) destination=Path.Combine(Path.GetDirectoryName(destination)!,Path.GetFileNameWithoutExtension(destination)+" (Бета 1 "+Guid.NewGuid().ToString("N")[..6]+")"+Path.GetExtension(destination));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(item.Stored,destination);
                }
                journal.Links.Remove(item);
            }
            catch(IOException) { } catch(UnauthorizedAccessException) { }
        }
        Save(); NotifyDesktop();
    }
    public void RestoreAll() { try { RestoreLinks(); } finally { try { RestoreDesktopIcons(); } finally { RestoreTaskbars(); } } }
    static void NotifyDesktop() => SHChangeNotify(0x08000000,0,IntPtr.Zero,IntPtr.Zero);
    public static void WatchParent(int pid,string root)
    {
        try { Process.GetProcessById(pid).WaitForExit(); } catch(ArgumentException) { }
        // Parent closes the journal before releasing the single-instance mutex.
        using var gate=new System.Threading.Mutex(false,"Beta1.Desktop.SingleInstance");
        bool held=false;
        try { try { held=gate.WaitOne(10000); } catch(System.Threading.AbandonedMutexException) { held=true; } if(held) new DesktopIntegration(root).Recover(); }
        catch(IOException) { } catch(UnauthorizedAccessException) { }
        finally { if(held) gate.ReleaseMutex(); }
    }
    delegate bool EnumProc(IntPtr h,IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback,IntPtr p);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,System.Text.StringBuilder name,int count);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string name,string? title);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h,int command);
    [DllImport("shell32.dll")] static extern void SHChangeNotify(uint id,uint flags,IntPtr item1,IntPtr item2);
}
