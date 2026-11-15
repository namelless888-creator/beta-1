using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Beta1;
internal static class ShellCatalog
{
    // Read only. Desktop contents and taskbar pins are separate sources.
    public static List<Shortcut> Desktop(params string[] roots)
    {
        var items=new List<Shortcut>();
        foreach(var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if(!Directory.Exists(root)) continue;
            try
            {
                foreach(var path in Directory.EnumerateFileSystemEntries(root))
                {
                    var attributes=File.GetAttributes(path);
                    if((attributes & (FileAttributes.Hidden|FileAttributes.System))!=0) continue;
                    bool folder=Directory.Exists(path);
                    string name=folder?Path.GetFileName(path):Path.GetFileNameWithoutExtension(path);
                    if(!folder && Path.GetExtension(path).ToLowerInvariant() is not (".lnk" or ".url" or ".exe")) name=Path.GetFileName(path);
                    items.Add(new(name,path) { IsFolder=folder });
                }
            }
            catch(IOException) { } catch(UnauthorizedAccessException) { }
        }
        return items.DistinctBy(a=>Path.GetFileName(a.Path),StringComparer.OrdinalIgnoreCase).OrderBy(a=>a.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    public static List<Shortcut> Taskbar(out bool exact)
    {
        var result=new List<Shortcut>(); exact=false;
        object? pinObject=null; IEnumIDList? enumerator=null;
        try
        {
            var type=Type.GetTypeFromCLSID(new Guid("90AA3A4E-1CBA-4233-B8BB-535773D48449"));
            pinObject=Activator.CreateInstance(type!);
            int hr=pinObject is IPinnedList3 pins ? pins.EnumObjects(out enumerator) : pinObject is IFlexiblePinnedList flexible ? flexible.EnumObjects(out enumerator) : -1;
            if(hr>=0 && enumerator!=null)
            {
                int next;
                while((next=enumerator.Next(1,out var pidl,out var count))==0 && count==1)
                {
                    try
                    {
                        var path=Name(pidl,0x80028000); // SIGDN_DESKTOPABSOLUTEPARSING, includes packaged apps.
                        var name=Name(pidl,0);
                        if(!string.IsNullOrEmpty(path)) result.Add(new(name,path));
                    }
                    finally { Marshal.FreeCoTaskMem(pidl); }
                }
                exact=next==1;
            }
        }
        catch(COMException) { } catch(InvalidCastException) { }
        finally
        {
            if(enumerator!=null) Marshal.FinalReleaseComObject(enumerator);
            if(pinObject!=null) Marshal.FinalReleaseComObject(pinObject);
        }
        if(!exact)
        {
            result.Clear();
            string pinned=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),@"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if(Directory.Exists(pinned)) result.AddRange(Directory.GetFiles(pinned,"*.lnk").Select(p=>new Shortcut(Path.GetFileNameWithoutExtension(p),p)));
        }
        return result.DistinctBy(a=>a.Path,StringComparer.OrdinalIgnoreCase).ToList();
    }
    static string Name(IntPtr pidl,uint kind)
    {
        if(SHGetNameFromIDList(pidl,kind,out var pointer)<0) return "";
        try { return Marshal.PtrToStringUni(pointer)??""; } finally { Marshal.FreeCoTaskMem(pointer); }
    }
    static readonly Dictionary<string,BitmapSource> icons=new(StringComparer.OrdinalIgnoreCase);
    public static BitmapSource? Icon(string path)
    {
        if(icons.TryGetValue(path,out var cached)) return cached;
        IntPtr pidl=IntPtr.Zero, list=IntPtr.Zero, icon=IntPtr.Zero;
        try
        {
            if(SHParseDisplayName(path,IntPtr.Zero,out pidl,0,out _)<0) return null;
            // Request only the system image-list INDEX, then draw it with NO overlay flags.
            // SHGFI_ICON / ExtractAssociatedIcon add a shortcut arrow even without ADDOVERLAYS.
            var handle=SHGetFileInfo(pidl,0,out var info,(uint)Marshal.SizeOf<SHFILEINFO>(),0x4000 | 0x8);
            if(handle==IntPtr.Zero) return null;
            var iid=new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
            if(SHGetImageList(2,ref iid,out list)>=0) icon=ImageList_GetIcon(list,info.iIcon,1);
            if(icon==IntPtr.Zero) icon=ImageList_GetIcon(handle,info.iIcon,1);
            if(icon==IntPtr.Zero) return null;
            var source=Imaging.CreateBitmapSourceFromHIcon(icon,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions()); source.Freeze(); icons[path]=source; return source;
        }
        catch(COMException) { return null; }
        finally { if(icon!=IntPtr.Zero) DestroyIcon(icon); if(list!=IntPtr.Zero) Marshal.Release(list); if(pidl!=IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
    }
    public static void ClearIconCache() => icons.Clear();
    [ComImport, Guid("0DD79AE2-D156-45D4-9EEB-3B549769E940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPinnedList3 { [PreserveSig] int EnumObjects(out IEnumIDList list); }
    [ComImport, Guid("60274FA2-611F-4B8A-A293-F27BF103D148"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFlexiblePinnedList { [PreserveSig] int EnumObjects(out IEnumIDList list); }
    [ComImport, Guid("000214F2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumIDList { [PreserveSig] int Next(uint count,out IntPtr pidl,out uint fetched); }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct SHFILEINFO { public IntPtr hIcon; public int iIcon; public uint attributes; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string display; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)] public string type; }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern int SHParseDisplayName(string name,IntPtr context,out IntPtr pidl,uint flags,out uint attributes);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SHGetFileInfo(IntPtr pidl,uint attr,out SHFILEINFO info,uint size,uint flags);
    [DllImport("shell32.dll")] static extern int SHGetImageList(int size,ref Guid iid,out IntPtr list);
    [DllImport("shell32.dll")] static extern int SHGetNameFromIDList(IntPtr pidl,uint kind,out IntPtr name);
    [DllImport("comctl32.dll")] static extern IntPtr ImageList_GetIcon(IntPtr list,int index,uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
}
