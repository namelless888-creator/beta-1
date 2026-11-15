using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Media.Control;
using Forms = System.Windows.Forms;

namespace Beta1;
public sealed record Shortcut(string Name, string Path) { public bool IsFolder { get; init; } }
public sealed class Preferences
{
    public double Opacity { get; set; } = .97;
    public double PanelScale { get; set; } = 1;
    public int AppIconSize { get; set; } = 44;
    public int DockIconSize { get; set; } = 36;
    public bool HideTaskbar { get; set; } = true;
    public bool HideDesktopIcons { get; set; } = true;
    public bool ShowIconNames { get; set; } = true;
    public int LayoutVersion { get; set; } = 3;
    public string Workspace { get; set; } = "Работа";
    public Dictionary<string, double[]> Positions { get; set; } = new();

}
public partial class App : System.Windows.Application
{
    readonly List<Window> panels = new();
    readonly List<Shortcut> apps = new();
    readonly List<Shortcut> desktopFolders = new();
    readonly List<Shortcut> taskbarPins = new();
    bool exactPins;
    readonly List<FileSystemWatcher> watchers = new();
    DispatcherTimer refreshTimer = new() { Interval=TimeSpan.FromMilliseconds(800) };
    int syncTicks;
    readonly Dictionary<string,Button> tabs = new();
    readonly Dictionary<string,Button> workspaces = new();
    WrapPanel folderGrid = new();
    readonly string storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Beta1", "settings.json");
    Preferences prefs = new();
    Forms.NotifyIcon? tray;
    DispatcherTimer timer = new();
    TextBlock clock = new(), date = new(), topDate = new(), status = new();
    WrapPanel grid = new();
    StackPanel folders = new();
    StackPanel dock = new();
    TextBox search = new();
    TextBlock track = new(), artist = new();
    System.Windows.Controls.Image artwork = new() { Width=66, Height=66 };
    ProgressBar progress = new() { Minimum=0, Maximum=1, Height=4, Background=new SolidColorBrush(Color.FromArgb(40,255,255,255)), BorderThickness=new Thickness(0) };
    GlobalSystemMediaTransportControlsSessionManager? media;
    bool readingMedia;
    DesktopIntegration desktop = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Beta1"));
    System.Threading.EventWaitHandle? openSettingsEvent;
    bool smoke;

    System.Drawing.Icon? brandIcon;

    bool ownsInstance;

    string category = "Все";
    Window? settings;
    System.Threading.Mutex? mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Length==2 && e.Args[0]=="--watch-parent" && int.TryParse(e.Args[1],out int parent))
        {
            DesktopIntegration.WatchParent(parent,Path.GetDirectoryName(storage)!); Shutdown(); return;
        }
        if(e.Args.Contains("--integration-test")) { Shutdown(IntegrationChecks.Run()); return; }
        smoke=e.Args.Contains("--smoke-test");
        openSettingsEvent=new System.Threading.EventWaitHandle(false,System.Threading.EventResetMode.AutoReset,"Beta1.OpenSettings");
        mutex = new System.Threading.Mutex(false, "Beta1.Desktop.SingleInstance");
        bool first;
        try { first=mutex.WaitOne(0); } catch(System.Threading.AbandonedMutexException) { first=true; }
        if (!first) { openSettingsEvent.Set(); Shutdown(); return; }
        ownsInstance=true;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            if(!smoke && File.Exists(storage))
            {
                var json=File.ReadAllText(storage); prefs=JsonSerializer.Deserialize<Preferences>(json)??new();
                using var doc=JsonDocument.Parse(json);
                if(!doc.RootElement.TryGetProperty("LayoutVersion",out var version) || version.GetInt32()<3)
                { prefs.Positions.Clear(); prefs.Opacity=.97; prefs.AppIconSize=48; prefs.DockIconSize=36; prefs.PanelScale=1; prefs.LayoutVersion=3; }
            }
        }
        catch { }
        prefs.Opacity=Math.Clamp(prefs.Opacity,.9,1);
        prefs.PanelScale=Math.Clamp(prefs.PanelScale,.7,1.4);
        prefs.AppIconSize=Math.Clamp(prefs.AppIconSize,24,72);
        prefs.DockIconSize=Math.Clamp(prefs.DockIconSize,24,64);
        if(!smoke) desktop.Recover();
        DiscoverApps();
        if(!smoke)
        {
            bool recoveryReady=false;
            try { recoveryReady=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--watch-parent "+Environment.ProcessId) { UseShellExecute=false, CreateNoWindow=true })!=null; } catch { }
            if(recoveryReady)
            {
                if(prefs.HideDesktopIcons) desktop.HideDesktopIcons();
                if(prefs.HideTaskbar) desktop.HideTaskbars();
            }
            else { prefs.HideTaskbar=false; prefs.HideDesktopIcons=false; }
            CreateSettingsShortcut();
        }
        using(var iconStream=GetResourceStream(new Uri("pack://application:,,,/Assets/beta1.ico")).Stream) brandIcon=new System.Drawing.Icon(iconStream);
        CreateTop(); CreateClock(); CreateMusic(); CreateApps(); CreateFolders(); CreateDock(); CreateTrayPanel();
        tray = new Forms.NotifyIcon { Icon = brandIcon, Text = "Бета 1 — рабочий стол", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Показать / скрыть панели", null, (_, _) => Dispatcher.Invoke(Toggle));
        menu.Items.Add("Настройки", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(() => Shutdown()));
        menu.Renderer=new Forms.ToolStripProfessionalRenderer(new TrayColors()); menu.BackColor=System.Drawing.Color.FromArgb(32,42,38); menu.ForeColor=System.Drawing.Color.White;
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(OpenSettings);
        timer.Interval = TimeSpan.FromSeconds(1); timer.Tick += (_, _) => Tick(); timer.Start(); Tick();
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        if(!smoke) StartWatching();

        DispatcherUnhandledException += (_, args) => { if(smoke) { Shutdown(2); return; } Forms.MessageBox.Show(args.Exception.Message, "Бета 1"); args.Handled = true; };
        if(!smoke && e.Args.Contains("--settings")) OpenSettings();
        if(smoke)
        {
            var check = new DispatcherTimer { Interval=TimeSpan.FromSeconds(3) };
            check.Tick += (_, _) => { check.Stop();
                bool valid=panels.Count==7 && panels.All(w=>w.IsVisible && new WindowInteropHelper(w).Handle!=IntPtr.Zero);
                foreach(var scale in new[] { .7,1.0,1.4 }) foreach(var size in new[] { 24,48,72 })
                {
                    prefs.PanelScale=scale; prefs.AppIconSize=size; prefs.DockIconSize=Math.Min(size,64); ApplyAppearance();
                    foreach(var w in panels) { w.UpdateLayout(); valid &= w.Left>=0 && w.Top>=0 && w.Left+w.Width<=DesktopArea.Width+1 && w.Top+w.Height<=DesktopArea.Height+1; }
                    var d=panels.First(w=>w.Title.EndsWith("Док")); var t=panels.First(w=>w.Title.EndsWith("Состояние")); valid &= d.Left+d.Width<t.Left;
                }
                prefs.PanelScale=1; prefs.AppIconSize=48; prefs.DockIconSize=36; prefs.Positions.Clear(); ApplyAppearance();
                // Populate preview only, with local shell resources. No changes to the Windows desktop.
                var previewRoot=Path.Combine(Path.GetTempPath(),"Beta1-preview"); Directory.CreateDirectory(previewRoot);
                foreach(var name in new[] { "Проекты","Документы","Загрузки","Игры" }) { var folder=Path.Combine(previewRoot,name); Directory.CreateDirectory(folder); desktopFolders.Add(new(name,folder) { IsFolder=true }); }
                apps.Clear(); foreach(var exe in new[] { "notepad.exe","cmd.exe","explorer.exe","regedit.exe" })
                { string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),exe); if(!File.Exists(path)) path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),exe); if(File.Exists(path)) apps.Add(new(Path.GetFileNameWithoutExtension(exe),path)); }
                RefreshApps(); RefreshFolders(); PreviewCapture.Desktop(panels);
                OpenSettings(); valid &= settings?.IsVisible==true; PreviewCapture.Element((FrameworkElement)settings!.Content,"settings-preview.png"); settings?.Close();
                var trayPanel=panels.First(w=>w.Title.EndsWith("Состояние")); var trayRow=(StackPanel)((Border)trayPanel.Content).Child;
                var previewMenu=((Button)trayRow.Children[0]).ContextMenu; previewMenu.IsOpen=true; previewMenu.UpdateLayout(); PreviewCapture.Element(previewMenu,"menu-preview.png");
                valid &= ((SolidColorBrush)previewMenu.Background).Color.R<80; previewMenu.IsOpen=false;
                Shutdown(valid?0:3); };
            check.Start();
        }
    }
    void Save()
    {
        if(smoke) return;
        Directory.CreateDirectory(Path.GetDirectoryName(storage)!);
        File.WriteAllText(storage+".tmp",JsonSerializer.Serialize(prefs,new JsonSerializerOptions { WriteIndented=true })); File.Move(storage+".tmp",storage,true);
    }
    static Brush Brush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
    static TextBlock Label(string text, double size = 14) => new() { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
    static Button Button(string label, Action action, string? hint = null)
    {
        var b = new Button { Content = label, ToolTip = hint ?? label }; b.Click += (_, _) => action(); return b;
    }
    Rect DesktopArea => smoke ? new(0,0,1920,1080) : new(0,0,SystemParameters.PrimaryScreenWidth,SystemParameters.PrimaryScreenHeight);
    double EffectiveScale => Math.Min(prefs.PanelScale,Math.Min(DesktopArea.Width/1280,DesktopArea.Height/780));
    Window Panel(string id, double x, double y, double width, double height, UIElement content)
    {
        var area=DesktopArea;
        double scale=id=="Док" ? DockScale : EffectiveScale;
        var w = new Window { Title="Бета 1 · "+id, Width=width*scale, Height=height*scale, WindowStyle=WindowStyle.None,
            ResizeMode=ResizeMode.NoResize, AllowsTransparency=true, Background=Brushes.Transparent,
            ShowInTaskbar=false, ShowActivated=false, Topmost=false, Tag=new double[] { x,y,width,height } };
        var border=new Border { CornerRadius=new CornerRadius(18), BorderThickness=new Thickness(1), BorderBrush=Brush("#84988C"),
            Background=PanelBackground(),
            Padding=id is "Верхняя панель" or "Док" or "Состояние" ? new Thickness(12,7,12,7) : new Thickness(16), Child=content,
            LayoutTransform=new ScaleTransform(scale,scale), ClipToBounds=true };
        w.Content=border;
        w.PreviewMouseLeftButtonDown += (_,ev) => {
            if(Keyboard.Modifiers==ModifierKeys.Alt) { w.DragMove(); ev.Handled=true; prefs.Positions[id]=new[] { w.Left,w.Top }; Save(); }
        };
        w.PreviewKeyDown += (_,ev) => { if(ev.Key==Key.Escape) w.Hide(); };
        w.Deactivated += (_,_) => Lower(w);
        w.SourceInitialized += (_,_) => { var h=new WindowInteropHelper(w).Handle; SetWindowLong(h,-20,GetWindowLong(h,-20)|0x80); var owner=FindWindow("Progman",null); if(owner!=IntPtr.Zero) SetWindowLongPtr(h,-8,owner); };
        TextOptions.SetTextFormattingMode(w,TextFormattingMode.Display);
        panels.Add(w); Position(w); w.Show(); Lower(w); return w;
    }
    Brush PanelBackground() => new LinearGradientBrush(Color.FromArgb((byte)(255*prefs.Opacity),38,47,39),Color.FromArgb((byte)(255*prefs.Opacity),22,29,25),90);
    static void Lower(Window w) { if(w.IsVisible) SetWindowPos(new WindowInteropHelper(w).Handle,new IntPtr(1),0,0,0,0,0x13); }
    void Position(Window w)
    {
        var a=DesktopArea; var spec=(double[])w.Tag;
        var name=w.Title.Replace("Бета 1 · ","");
        var saved=prefs.Positions.GetValueOrDefault(name);
        double margin=24*EffectiveScale;
        double left=spec[0]<0 ? a.Width-w.Width-margin : spec[0]==.5 ? (a.Width-w.Width)/2 : spec[0]*a.Width;
        double top=spec[1]<0 ? a.Height-w.Height-margin : spec[1]*a.Height;
        if(name=="Музыка") { var clockWindow=panels.FirstOrDefault(p=>p.Title.EndsWith("Часы")); if(clockWindow!=null) top=clockWindow.Top+clockWindow.Height+16*EffectiveScale; }
        if(name=="Папки") { var appsWindow=panels.FirstOrDefault(p=>p.Title.EndsWith("Приложения")); if(appsWindow!=null) top=appsWindow.Top+appsWindow.Height+16*EffectiveScale; }
        w.Left=Math.Clamp(saved?.ElementAtOrDefault(0)??left,0,Math.Max(0,a.Width-w.Width));
        w.Top=Math.Clamp(saved?.ElementAtOrDefault(1)??top,0,Math.Max(0,a.Height-w.Height));
    }
    void CreateTop()
    {
        var row=new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        var left=new StackPanel { Orientation=Orientation.Horizontal };
        var brand=Button("БЕТА 1",OpenSettings,"Панель управления Бета 1"); brand.FontSize=20; brand.FontWeight=FontWeights.SemiBold; brand.Margin=new Thickness(0,0,14,0); left.Children.Add(brand);
        foreach(var name in new[] { "Работа","Личное" }) { var b=Button(name,()=> { prefs.Workspace=name; category=name=="Работа"?"Работа":"Все"; Save(); RefreshApps(); }); workspaces[name]=b; left.Children.Add(b); }
        row.Children.Add(left);
        topDate.HorizontalAlignment=HorizontalAlignment.Center; Grid.SetColumn(topDate,1); row.Children.Add(topDate);
        var right=new StackPanel { Orientation=Orientation.Horizontal }; Grid.SetColumn(right,2);
        right.Children.Add(Symbol("\uE701",()=>Launch("ms-settings:network"),"Сеть",18));
        right.Children.Add(Symbol("\uE767",()=>Launch("ms-settings:sound"),"Звук",18));
        right.Children.Add(Symbol("\uE713",OpenSettings,"Панель управления")); row.Children.Add(right);
        Panel("Верхняя панель",.0125,.012,(DesktopArea.Width-48*EffectiveScale)/EffectiveScale,58,row);
    }
    static Button Symbol(string glyph,Action action,string hint,int size=22)
    {
        var b=Button(hint,action,hint); b.Content=new TextBlock { Text=glyph, FontFamily=new FontFamily("Segoe MDL2 Assets"), FontSize=size, VerticalAlignment=VerticalAlignment.Center }; return b;
    }
    void CreateClock()
    {
        var col = new StackPanel { Margin=new Thickness(10,0,0,0) }; clock.FontSize = 52; clock.FontWeight = FontWeights.Light; date.FontSize = 14;
        col.Children.Add(clock); col.Children.Add(date); col.Children.Add(new Border { Height=2, Width=48, Background=Brush("#64E4DA"), HorizontalAlignment=HorizontalAlignment.Left, Margin=new Thickness(0,8,0,0) }); Panel("Часы", .016,.09,350,124,col);
    }
    void CreateMusic()
    {
        var col = new StackPanel();
        var row = new DockPanel(); artwork.Margin=new Thickness(0,0,12,0); DockPanel.SetDock(artwork,Dock.Left); row.Children.Add(artwork);
        var info=new StackPanel(); track.Text="Музыка"; track.FontSize=19; track.TextTrimming=TextTrimming.CharacterEllipsis;
        artist.Text="Открой проигрыватель"; artist.Opacity=.75; artist.TextTrimming=TextTrimming.CharacterEllipsis; info.Children.Add(track); info.Children.Add(artist);
        var controls = new StackPanel { Orientation=Orientation.Horizontal };
        controls.Children.Add(Symbol("\uE892", () => MediaKey(0xB1), "Предыдущий трек",18));
        controls.Children.Add(Symbol("\uE768", () => MediaKey(0xB3), "Воспроизведение / пауза",20));
        controls.Children.Add(Symbol("\uE893", () => MediaKey(0xB0), "Следующий трек",18)); info.Children.Add(controls);
        row.Children.Add(info); col.Children.Add(row); progress.Margin=new Thickness(0,12,0,0); progress.Foreground=Brush("#54E0DA"); col.Children.Add(progress);
        Panel("Музыка", .016,.255,350,168,col);
    }
    async void UpdateMedia()
    {
        if(readingMedia) return;
        readingMedia=true;
        try
        {
            media ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session=media.GetCurrentSession();
            if(session==null) { track.Text="Музыка"; artist.Text="Открой проигрыватель"; artwork.Source=null; progress.Value=0; return; }
            var props=await session.TryGetMediaPropertiesAsync();
            bool changed=track.Text!=props.Title;
            track.Text=string.IsNullOrWhiteSpace(props.Title)?"Без названия":props.Title; artist.Text=props.Artist;
            var timeline=session.GetTimelineProperties();
            progress.Maximum=Math.Max(1,(timeline.EndTime-timeline.StartTime).TotalSeconds); progress.Value=Math.Max(0,(timeline.Position-timeline.StartTime).TotalSeconds);
            if(changed)
            {
                artwork.Source=null;
                if(props.Thumbnail!=null)
                {
                    using var stream=await props.Thumbnail.OpenReadAsync();
                    using var data=stream.AsStreamForRead();
                    var image=new BitmapImage(); image.BeginInit(); image.CacheOption=BitmapCacheOption.OnLoad; image.StreamSource=data; image.EndInit(); image.Freeze(); artwork.Source=image;
                }
            }
        }
        catch { artist.Text="Управление медиаклавишами"; }
        finally { readingMedia=false; }
    }
    void CreateApps()
    {
        var col=new Grid(); col.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); col.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); col.RowDefinitions.Add(new RowDefinition());
        var heading=new DockPanel { Margin=new Thickness(0,0,0,14) };
        var categories=new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Right }; DockPanel.SetDock(categories,Dock.Right);
        foreach(var name in new[] { "Все","Работа","Игры" }) { var b=Button(name,()=> { category=name; RefreshApps(); }); b.Padding=new Thickness(9,6,9,6); b.FontSize=12; tabs[name]=b; categories.Children.Add(b); }
        heading.Children.Add(categories); heading.Children.Add(Label("Приложения",20)); col.Children.Add(heading);
        var searchBox=new Grid { Margin=new Thickness(0,0,0,10) }; Grid.SetRow(searchBox,1);
        search.ToolTip="Найти на рабочем столе"; search.Padding=new Thickness(34,8,10,8); search.TextChanged+=(_,_)=>RefreshApps(); searchBox.Children.Add(search);
        var hint=new TextBlock { Text="Найти на рабочем столе…", Foreground=Brush("#BCC9C0"), Margin=new Thickness(34,0,0,0), VerticalAlignment=VerticalAlignment.Center, IsHitTestVisible=false };
        search.TextChanged+=(_,_)=>hint.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed; searchBox.Children.Add(hint);
        searchBox.Children.Add(new TextBlock { Text="\uE721", FontFamily=new FontFamily("Segoe MDL2 Assets"), Margin=new Thickness(12,0,0,0), VerticalAlignment=VerticalAlignment.Center, IsHitTestVisible=false }); col.Children.Add(searchBox);
        var scroll=new ScrollViewer { Content=grid, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled }; Grid.SetRow(scroll,2); col.Children.Add(scroll);
        Panel("Приложения",-1,.09,AppPanelWidth,370,col); RefreshApps();
    }
    double AppPanelWidth => Math.Max(428,4*(prefs.AppIconSize+34)+44);
    Button Tile(Shortcut item)
    {
        var contents=new StackPanel { Width=prefs.AppIconSize+18, MinHeight=prefs.AppIconSize+(prefs.ShowIconNames?42:8) };
        contents.Children.Add(Icon(item.Path,prefs.AppIconSize));
        if(prefs.ShowIconNames) contents.Children.Add(new TextBlock { Text=item.Name, FontSize=12, Foreground=Brush("#FFFFFF"), TextAlignment=TextAlignment.Center, TextWrapping=TextWrapping.Wrap, TextTrimming=TextTrimming.CharacterEllipsis, MaxHeight=34, Margin=new Thickness(0,8,0,0) });
        var b=Button(item.Name,()=>Launch(item.Path),item.Name); b.Content=contents; b.Padding=new Thickness(8); b.Margin=new Thickness(0,2,0,2);
        var menu=new ContextMenu(); var open=new MenuItem { Header="Открыть" }; open.Click+=(_,_)=>Launch(item.Path); menu.Items.Add(open);
        var location=new MenuItem { Header="Показать на рабочем столе" }; location.Click+=(_,_)=>Process.Start(new ProcessStartInfo("explorer.exe",$"/select,\"{item.Path}\"") { UseShellExecute=true }); menu.Items.Add(location); b.ContextMenu=menu;
        return b;
    }
    void RefreshApps()
    {
        grid.Children.Clear();
        foreach(var pair in tabs) pair.Value.Background=pair.Key==category?Brush("#31594F"):Brushes.Transparent;
        foreach(var pair in workspaces) pair.Value.Background=pair.Key==prefs.Workspace?Brush("#31594F"):Brushes.Transparent;
        var filtered=apps.Where(a=>a.Name.Contains(search.Text,StringComparison.OrdinalIgnoreCase));
        if(category=="Игры") filtered=filtered.Where(a=>IsGame(a.Name));
        if(category=="Работа") filtered=filtered.Where(a=>!IsGame(a.Name));
        foreach(var item in filtered) grid.Children.Add(Tile(item));
        if(grid.Children.Count==0) grid.Children.Add(new TextBlock { Text="На рабочем столе нет подходящих элементов", Foreground=Brush("#CDD7D0"), TextWrapping=TextWrapping.Wrap, Width=AppPanelWidth-50, Margin=new Thickness(2,14,0,0) });
    }
    static bool IsGame(string name) => new[] { "steam","game","forza","counter","игр","xbox","battle","epic" }.Any(s=>name.Contains(s,StringComparison.OrdinalIgnoreCase));
    void CreateFolders()
    {
        var col=new Grid(); col.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); col.RowDefinitions.Add(new RowDefinition());
        col.Children.Add(new TextBlock { Text="Папки", FontSize=20, Margin=new Thickness(0,0,0,12) });
        var scroll=new ScrollViewer { Content=folderGrid, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled }; Grid.SetRow(scroll,1); col.Children.Add(scroll);
        Panel("Папки",-1,.5,AppPanelWidth,260,col); RefreshFolders();
    }
    void RefreshFolders()
    {
        folderGrid.Children.Clear(); foreach(var item in desktopFolders) folderGrid.Children.Add(Tile(item));
        if(desktopFolders.Count==0) folderGrid.Children.Add(new TextBlock { Text="На рабочем столе нет папок", Foreground=Brush("#CDD7D0"), Margin=new Thickness(0,12,0,0) });
    }
    void CreateDock()
    {
        dock.Orientation=Orientation.Horizontal; dock.HorizontalAlignment=HorizontalAlignment.Center;
        RefreshDock(); Panel("Док",.5,-1,DockWidth,prefs.DockIconSize+38,dock);
    }
    double DockScale => Math.Min(EffectiveScale,Math.Max(220,DesktopArea.Width-2*(315*EffectiveScale+40))/Math.Max(1,DockWidth));
    double DockWidth => dock.Children.Count*(prefs.DockIconSize+18)+26;
    void RefreshDock()
    {
        dock.Children.Clear();
        int size=prefs.DockIconSize;
        var start=Symbol("\uE782",()=>MediaKey(0x5B),"Пуск",size); start.Padding=new Thickness(9,8,9,8); dock.Children.Add(start);
        var find=Symbol("\uE721",()=> { var w=panels.First(p=>p.Title.EndsWith("Приложения")); w.Show(); w.Activate(); search.Focus(); },"Найти приложение",size); find.Padding=new Thickness(9,8,9,8); dock.Children.Add(find);
        
        foreach(var a in taskbarPins) { var b=Button(a.Name,()=>Launch(a.Path)); b.Content=Icon(a.Path,size); b.Padding=new Thickness(9,8,9,8); dock.Children.Add(b); }
        if(taskbarPins.Count==0) { var empty=Button("Нет закреплений",()=>Launch("ms-settings:taskbar"),"Закрепите приложения в панели задач Windows и обновите список в настройках"); dock.Children.Add(empty); }
        var window=panels.FirstOrDefault(p=>p.Title.EndsWith("Док"));
        if(window!=null) { ((Border)window.Content).LayoutTransform=new ScaleTransform(DockScale,DockScale); window.Width=DockWidth*DockScale; window.Height=(size+38)*DockScale; Position(window); }
    }
    void CreateTrayPanel()
    {
        var row=new StackPanel { Orientation=Orientation.Horizontal, VerticalAlignment=VerticalAlignment.Center };
        var beta=Button("Бета 1",OpenSettings,"Бета 1 — настройки"); beta.Content=Icon(Environment.ProcessPath!,24);
        var menu=new ContextMenu();
        foreach(var item in new[] { ("Настройки",(Action)OpenSettings),("Показать / скрыть панели",(Action)Toggle),("Вернуть панель задач Windows",(Action)(()=> { prefs.HideTaskbar=false; desktop.RestoreTaskbars(); Save(); })),("Выход",(Action)(()=>Shutdown())) }) { var entry=new MenuItem { Header=item.Item1 }; entry.Click+=(_,_)=>item.Item2(); menu.Items.Add(entry); }
        beta.ContextMenu=menu; row.Children.Add(beta);
        row.Children.Add(Symbol("\uE701",()=>Launch("ms-settings:network"),"Сеть",18));
        row.Children.Add(Symbol("\uE767",()=>Launch("ms-settings:sound"),"Громкость",18));
        status.FontSize=13; row.Children.Add(status);
        Panel("Состояние",-1,-1,315,58,row);
    }
    void Tick()
    {
        UpdateMedia();
        if(!smoke && prefs.HideTaskbar) desktop.HideTaskbars();
        if(!smoke && prefs.HideDesktopIcons) desktop.HideDesktopIcons();
        if(!smoke && ++syncTicks%15==0) { taskbarPins.Clear(); taskbarPins.AddRange(ShellCatalog.Taskbar(out exactPins)); RefreshDock(); }
        if(openSettingsEvent?.WaitOne(0)==true) OpenSettings();

        var now=DateTime.Now; var culture=System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        clock.Text=now.ToString("HH:mm"); date.Text=now.ToString("dddd, d MMMM",culture); topDate.Text=date.Text;
        var battery=Forms.SystemInformation.PowerStatus; status.Text="  " + now.ToString("HH:mm") + (battery.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery) ? "" : "  ·  " + Math.Round(battery.BatteryLifePercent*100) + "%");
    }
    void Toggle() { bool show=panels.Any(w => !w.IsVisible); foreach(var w in panels) if(show) { w.Show(); Lower(w); } else w.Hide(); }
    void OpenSettings()
    {
        if(settings!=null) { settings.WindowState=WindowState.Normal; settings.Activate(); return; }
        var body=new StackPanel { Margin=new Thickness(28) };
        body.Children.Add(Label("Бета 1 · Панель управления",25));
        body.Children.Add(new TextBlock { Text="Внешний вид и рабочий стол", Opacity=.7, Margin=new Thickness(0,6,0,22) });
        void Range(string name,double min,double max,double value,Action<double> update,bool integer=false)
        {
            var label=Label(name+": "+Math.Round(value*(integer?1:100))+(integer?" px":"%")); label.Margin=new Thickness(0,12,0,6); body.Children.Add(label);
            var slider=new Slider { Minimum=min, Maximum=max, Value=value, TickFrequency=integer?2:.05, IsSnapToTickEnabled=true, Margin=new Thickness(0,2,0,8) };
            slider.ValueChanged+=(_,_)=> { update(slider.Value); label.Text=name+": "+Math.Round(slider.Value*(integer?1:100))+(integer?" px":"%"); ApplyAppearance(); };
            slider.PreviewMouseLeftButtonUp+=(_,_)=>Save(); body.Children.Add(slider);
        }
        void Check(string name,bool value,Action<bool> changed)
        {
            var check=new CheckBox { Content=name, IsChecked=value, Foreground=Brushes.White, Margin=new Thickness(0,10,0,10), FontSize=14 };
            check.Click+=(_,_)=> { changed(check.IsChecked==true); Save(); }; body.Children.Add(check);
        }
        Range("Масштаб интерфейса",.7,1.4,prefs.PanelScale,v=>prefs.PanelScale=v);
        Range("Размер иконок приложений",24,72,prefs.AppIconSize,v=>prefs.AppIconSize=(int)v,true);
        Range("Размер иконок дока",24,64,prefs.DockIconSize,v=>prefs.DockIconSize=(int)v,true);
        Range("Плотность фона",.9,1,prefs.Opacity,v=>prefs.Opacity=v);
        Check("Показывать названия приложений",prefs.ShowIconNames,v=> { prefs.ShowIconNames=v; RefreshApps(); });
        Check("Скрывать стандартную панель задач Windows",prefs.HideTaskbar,v=> { prefs.HideTaskbar=v; if(v) desktop.HideTaskbars(); else desktop.RestoreTaskbars(); });
        Check("Скрывать значки рабочего стола Windows",prefs.HideDesktopIcons,v=> { prefs.HideDesktopIcons=v; if(v) desktop.HideDesktopIcons(); else desktop.RestoreDesktopIcons(); });
        body.Children.Add(new TextBlock { Text="Файлы и ярлыки остаются на своих местах. Скрывается только отображение значков Windows; при выходе оно восстанавливается.", TextWrapping=TextWrapping.Wrap, Foreground=Brush("#CCD6CF"), Margin=new Thickness(0,0,0,12) });
        bool autostart;
        using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) autostart=key?.GetValue("Beta1")!=null;
        Check("Запускать при входе в Windows",autostart,v=> { using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); if(v) key.SetValue("Beta1","\""+Environment.ProcessPath+"\""); else key.DeleteValue("Beta1",false); });
        body.Children.Add(Label("Источники значков",19));
        body.Children.Add(new TextBlock { Text="Приложения и папки: только рабочий стол. Док: закреплённые приложения Windows."+(exactPins?"":"\nWindows не предоставила полный список: показаны доступные закреплённые ярлыки. Порядок и некоторые приложения могут отличаться."), Foreground=Brush("#CCD6CF"), TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,10,0,10) });
        body.Children.Add(Button("Обновить список приложений",ReloadApps));
        body.Children.Add(Button("Сбросить расположение панелей",()=> { prefs.Positions.Clear(); Reposition(); Save(); }));
        body.Children.Add(new TextBlock { Text="Alt + перетаскивание — переместить панель. Закрепления дока настраиваются в штатной панели задач Windows. Значок Бета 1 в собственном трее открывает эту панель управления.", TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,18,0,12), Opacity=.75 });
        body.Children.Add(Button("Вернуть рабочий стол Windows и выйти",()=>Shutdown()));
        settings=new Window { Title="Бета 1 — Панель управления", Width=620, Height=Math.Min(850,SystemParameters.WorkArea.Height-40), MinWidth=480, MinHeight=450,
            Background=Brush("#171D1B"), Content=new ScrollViewer { Content=body, VerticalScrollBarVisibility=ScrollBarVisibility.Auto }, WindowStartupLocation=WindowStartupLocation.CenterScreen };
        settings.Icon=Imaging.CreateBitmapSourceFromHIcon(brandIcon!.Handle,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
        settings.Closed+=(_,_)=> { Save(); settings=null; }; settings.Show(); settings.Activate();
    }
    void ApplyAppearance()
    {
        foreach(var w in panels)
        {
            var spec=(double[])w.Tag; var name=w.Title.Replace("Бета 1 · ","");
            if(name is "Приложения" or "Папки") spec[2]=AppPanelWidth;
            if(name=="Верхняя панель") spec[2]=(DesktopArea.Width-48*EffectiveScale)/EffectiveScale;
            var border=(Border)w.Content;
            border.Background=PanelBackground();
            border.LayoutTransform=new ScaleTransform(EffectiveScale,EffectiveScale);
            w.Width=spec[2]*EffectiveScale; w.Height=spec[3]*EffectiveScale;
        }
        RefreshApps(); RefreshFolders(); RefreshDock(); Reposition();
    }
    void ReloadApps()
    {
        DiscoverApps(); ShellCatalog.ClearIconCache(); RefreshApps(); RefreshFolders(); RefreshDock();
    }
    void StartWatching()
    {
        refreshTimer.Tick+=(_,_)=> { refreshTimer.Stop(); ReloadApps(); };
        foreach(var path in DesktopRoots.Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),@"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar")))
        {
            if(!Directory.Exists(path)) continue;
            var watcher=new FileSystemWatcher(path) { IncludeSubdirectories=false, NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName|NotifyFilters.LastWrite };
            FileSystemEventHandler changed=(_,_)=>Dispatcher.BeginInvoke(()=> { refreshTimer.Stop(); refreshTimer.Start(); });
            watcher.Created+=changed; watcher.Deleted+=changed; watcher.Changed+=changed; watcher.Renamed+=(_,_)=>Dispatcher.BeginInvoke(()=> { refreshTimer.Stop(); refreshTimer.Start(); }); watcher.EnableRaisingEvents=true; watchers.Add(watcher);
        }
    }
    void CreateSettingsShortcut()
    {
        try
        {
            var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),"Programs"); Directory.CreateDirectory(directory);
            var type=Type.GetTypeFromProgID("WScript.Shell"); if(type==null) return;
            dynamic shell=Activator.CreateInstance(type)!; dynamic link=shell.CreateShortcut(Path.Combine(directory,"Бета 1 — Настройки.lnk"));
            link.TargetPath=Environment.ProcessPath; link.Arguments="--settings"; link.IconLocation=Environment.ProcessPath+",0"; link.Save();
            Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell);
        }
        catch { }
    }
    void Reposition() { foreach(var w in panels) Position(w); }
    void DisplayChanged(object? sender,EventArgs e) => Dispatcher.BeginInvoke(Reposition);
    static string[] DesktopRoots => new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) };
    void DiscoverApps()
    {
        var all=ShellCatalog.Desktop(DesktopRoots); apps.Clear(); desktopFolders.Clear();
        apps.AddRange(all.Where(a=>!a.IsFolder)); desktopFolders.AddRange(all.Where(a=>a.IsFolder));
        taskbarPins.Clear(); taskbarPins.AddRange(ShellCatalog.Taskbar(out exactPins));
    }
    static UIElement Icon(string path,int size)
    {
        var source=ShellCatalog.Icon(path);
        if(source!=null) { var image=new System.Windows.Controls.Image { Source=source, Width=size, Height=size, Stretch=Stretch.Uniform }; RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality); return image; }
        return new TextBlock { Text="\uE8A5", FontFamily=new FontFamily("Segoe MDL2 Assets"), FontSize=size-4, TextAlignment=TextAlignment.Center, Foreground=Brush("#64E4DA") };
    }
    static void Launch(string path)
    {
        try { if(path.StartsWith("::{",StringComparison.Ordinal)) Process.Start(new ProcessStartInfo("explorer.exe",path) { UseShellExecute=true }); else Process.Start(new ProcessStartInfo(path) { UseShellExecute=true }); } catch(Exception ex) { Forms.MessageBox.Show("Не удалось открыть:\n"+path+"\n\n"+ex.Message,"Бета 1"); }
    }
    static void MediaKey(byte key) { keybd_event(key,0,0,UIntPtr.Zero); keybd_event(key,0,2,UIntPtr.Zero); }
    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged-=DisplayChanged; timer.Stop();
        if(ownsInstance)
        {
            try { if(!smoke) desktop.RestoreAll(); Save(); } catch(Exception ex) { Trace.WriteLine(ex); }
        }
        refreshTimer.Stop(); foreach(var watcher in watchers) watcher.Dispose();
        tray?.Dispose(); brandIcon?.Dispose(); openSettingsEvent?.Dispose(); if(ownsInstance) mutex?.ReleaseMutex(); mutex?.Dispose(); base.OnExit(e);
    }
    sealed class TrayColors : Forms.ProfessionalColorTable
    {
        public override System.Drawing.Color MenuItemSelected => System.Drawing.Color.FromArgb(54,89,79);
        public override System.Drawing.Color ToolStripDropDownBackground => System.Drawing.Color.FromArgb(32,42,38);
        public override System.Drawing.Color ImageMarginGradientBegin => ToolStripDropDownBackground;
        public override System.Drawing.Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
        public override System.Drawing.Color ImageMarginGradientEnd => ToolStripDropDownBackground;
    }
    delegate bool EnumWindowProc(IntPtr h,IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowProc callback,IntPtr parameter);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string name,string? title);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string name,string? title);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h,int command);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h,int index,IntPtr value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int cx,int cy,uint flags);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h,int index,int value);
    [DllImport("user32.dll")] static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
}
