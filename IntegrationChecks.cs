using System;
using System.IO;
namespace Beta1;
internal static class IntegrationChecks
{
    public static int Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"Beta1-check-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var desktop=Path.Combine(root,"desktop"); Directory.CreateDirectory(desktop);
            var first=Path.Combine(desktop,"Imported.lnk"); var other=Path.Combine(desktop,"Untouched.lnk");
            var document=Path.Combine(desktop,"Notes.txt"); var executable=Path.Combine(desktop,"Tool.exe");
            File.WriteAllText(first,"original"); File.WriteAllText(other,"keep"); File.WriteAllText(document,"notes"); File.WriteAllText(executable,"exe");
            var integration=new DesktopIntegration(Path.Combine(root,"state"));
            var imported=integration.ImportDesktopLinks(new[] { new Shortcut("Imported",first),new Shortcut("Notes",document),new Shortcut("Tool",executable) },new[] { desktop });
            Require(imported.Count==1 && !File.Exists(first) && File.Exists(imported[first]),"selective import");
            Require(File.Exists(other)&&File.Exists(document)&&File.Exists(executable),"unrelated files preserved");
            // Simulate a process crash: a fresh instance must recover from the journal.
            new DesktopIntegration(Path.Combine(root,"state")).Recover();
            Require(File.ReadAllText(first)=="original","crash recovery");
            integration=new DesktopIntegration(Path.Combine(root,"state"));
            integration.ImportDesktopLinks(new[] { new Shortcut("Imported",first) },new[] { desktop });
            File.WriteAllText(first,"new shortcut"); integration.RestoreAll();
            Require(File.ReadAllText(first)=="new shortcut","restore never overwrites new files");
            Require(Directory.GetFiles(desktop,"Imported (Бета 1 *).lnk").Length==1,"conflicting shortcut recovered separately");
            return 0;
        }
        catch(Exception ex) { File.WriteAllText(Path.Combine(Path.GetTempPath(),"Beta1-integration-error.txt"),ex.ToString()); return 4; }
        finally { try { Directory.Delete(root,true); } catch { } }
    }
    static void Require(bool test,string name) { if(!test) throw new InvalidOperationException(name); }
}
