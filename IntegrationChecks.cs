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
            var before=Directory.GetFiles(desktop).ToDictionary(p=>p,File.ReadAllBytes);
            using var window=new Forms.Form { ShowInTaskbar=false }; using var list=new Forms.ListView { Dock=Forms.DockStyle.Fill }; window.Controls.Add(list); window.Show(); Forms.Application.DoEvents();
            var integration=new DesktopIntegration(Path.Combine(root,"state"));
            Require(IsWindowVisible(list.Handle),"fixture icon layer initially visible");
            integration.HideIconLayer(list.Handle); Require(!IsWindowVisible(list.Handle),"icon layer hidden");
            foreach(var pair in before) Require(File.Exists(pair.Key)&&File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value),"files remain unchanged while hidden");
            new DesktopIntegration(Path.Combine(root,"state")).Recover(); Require(IsWindowVisible(list.Handle),"layer restored from journal");
            window.Close();
            // Upgrade from 0.2 must restore formerly moved links without overwriting new files.
            var old=Path.Combine(root,"legacy.lnk"); File.WriteAllText(old,"old");
            var state=Path.Combine(root,"legacy"); Directory.CreateDirectory(state);
            File.WriteAllText(Path.Combine(state,"desktop-recovery.json"),JsonSerializer.Serialize(new DesktopIntegration.Journal { Links=new() { new(shortcut,old) } }));
            new DesktopIntegration(state).Recover();
            Require(File.Exists(shortcut)&&Directory.GetFiles(desktop,"Test (Бета 1 *).lnk").Length==1,"legacy migration preserves conflicts");
            return 0;
        }
        catch(Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"integration-error.txt"),ex.ToString()); return 4; }
        finally { try { Directory.Delete(root,true); } catch { } }
    }
    static byte[] Pixels(BitmapSource bitmap) { int stride=(bitmap.PixelWidth*bitmap.Format.BitsPerPixel+7)/8; var bytes=new byte[stride*bitmap.PixelHeight]; bitmap.CopyPixels(bytes,stride,0); return bytes; }
    static void Require(bool test,string name) { if(!test) throw new InvalidOperationException(name); }
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr handle);
}
