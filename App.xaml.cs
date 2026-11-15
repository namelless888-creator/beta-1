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
public sealed record Shortcut(string Name, string Path);
public sealed class Preferences
{
    public double Opacity { get; set; } = .94;
    public double PanelScale { get; set; } = 1;
    public int AppIconSize { get; set; } = 44;
    public int DockIconSize { get; set; } = 36;
    public bool HideTaskbar { get; set; } = true;
    public bool HideDuplicates { get; set; } = true;
    public bool ShowIconNames { get; set; } = true;
    public int LayoutVersion { get; set; } = 2;
    public string Workspace { get; set; } = "Работа";
    public Dictionary<string, double[]> Positions { get; set; } = new();
    public List<Shortcut> Pinned { get; set; } = new();
    public List<Shortcut> Folders { get; set; } = new();
}
public partial class App : System.Windows.Application
{
    readonly List<Window> panels = new();
    readonly List<Shortcut> apps = new();
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
    readonly Dictionary<string,string> originalPaths = new(StringComparer.OrdinalIgnoreCase);
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
        try { if(File.Exists(storage)) { var json=File.ReadAllText(storage); prefs=JsonSerializer.Deserialize<Preferences>(json)??new(); using var doc=JsonDocument.Parse(json); if(!doc.RootElement.TryGetProperty("LayoutVersion",out _)) prefs.Positions.Clear(); } } catch { }
        if (prefs.Folders.Count == 0) prefs.Folders = new() {
            new("Проекты", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            new("Документы", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            new("Загрузки", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")) };
        prefs.Opacity=Math.Clamp(prefs.Opacity,.55,1);
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
                if(prefs.HideDuplicates) ImportDuplicates();
                if(prefs.HideTaskbar) desktop.HideTaskbars();
            }
            else { prefs.HideTaskbar=false; prefs.HideDuplicates=false; }
            CreateSettingsShortcut();
        }
        using(var iconStream=GetResourceStream(new Uri("pack://application:,,,/Assets/beta1.ico")).Stream) brandIcon=new System.Drawing.Icon(iconStream);
        CreateTop(); CreateClock(); CreateMusic(); CreateApps(); CreateFolders(); CreateDock(); CreateTrayPanel();
        tray = new Forms.NotifyIcon { Icon = brandIcon, Text = "Бета 1 — рабочий стол", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Показать / скрыть панели", null, (_, _) => Dispatcher.Invoke(Toggle));
        menu.Items.Add("Настройки", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(() => Shutdown()));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(OpenSettings);
        timer.Interval = TimeSpan.FromSeconds(1); timer.Tick += (_, _) => Tick(); timer.Start(); Tick();
        SystemEvents.DisplaySettingsChanged += DisplayChanged;

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
                OpenSettings(); valid &= settings?.IsVisible==true; settings?.Close();
                Shutdown(valid?0:3); };
            check.Start();
        }
    }
    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storage)!);
        var stablePins=prefs.Pinned.Select(p=>originalPaths.TryGetValue(p.Path,out var original)?p with { Path=original }:p).ToList();
        var copy=JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(prefs))!; copy.Pinned=stablePins;
        File.WriteAllText(storage+".tmp",JsonSerializer.Serialize(copy,new JsonSerializerOptions { WriteIndented=true })); File.Move(storage+".tmp",storage,true);
    }
    static Brush Brush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
    static TextBlock Label(string text, double size = 14) => new() { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
    static Button Button(string label, Action action, string? hint = null)
    {
        var b = new Button { Content = label, ToolTip = hint ?? label }; b.Click += (_, _) => action(); return b;
    }
    Rect DesktopArea => new(0,0,SystemParameters.PrimaryScreenWidth,SystemParameters.PrimaryScreenHeight);
    double EffectiveScale => Math.Min(prefs.PanelScale,Math.Min(DesktopArea.Width/1280,DesktopArea.Height/780));
    Window Panel(string id, double x, double y, double width, double height, UIElement content)
    {
        var area=DesktopArea;
        double scale=id=="Док" ? DockScale : EffectiveScale;
        var w = new Window { Title="Бета 1 · "+id, Width=width*scale, Height=height*scale, WindowStyle=WindowStyle.None,
            ResizeMode=ResizeMode.NoResize, AllowsTransparency=true, Background=Brushes.Transparent,
            ShowInTaskbar=false, ShowActivated=false, Topmost=false, Tag=new double[] { x,y,width,height } };
        var border=new Border { CornerRadius=new CornerRadius(16), BorderThickness=new Thickness(1), BorderBrush=Brush("#609BA8A2"),
            Background=new SolidColorBrush(Color.FromArgb((byte)(255*prefs.Opacity),23,29,27)),
            Padding=id is "Верхняя панель" or "Док" or "Состояние" ? new Thickness(12,7,12,7) : new Thickness(16), Child=content,
            LayoutTransform=new ScaleTransform(scale,scale), ClipToBounds=true };
        w.Content=border;
        w.PreviewMouseLeftButtonDown += (_,ev) => {
            if(Keyboard.Modifiers==ModifierKeys.Alt) { w.DragMove(); ev.Handled=true; prefs.Positions[id]=new[] { w.Left,w.Top }; Save(); }
        };
        w.PreviewKeyDown += (_,ev) => { if(ev.Key==Key.Escape) w.Hide(); };
        w.Deactivated += (_,_) => Lower(w);
        w.SourceInitialized += (_,_) => { var h=new WindowInteropHelper(w).Handle; SetWindowLong(h,-20,GetWindowLong(h,-20)|0x80); var owner=FindWindow("Progman",null); if(owner!=IntPtr.Zero) SetWindowLongPtr(h,-8,owner); };
        panels.Add(w); Position(w); w.Show(); Lower(w); return w;
    }
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
        left.Children.Add(Button("БЕТА 1",OpenSettings,"Панель управления Бета 1"));
        foreach(var name in new[] { "Работа","Личное" }) left.Children.Add(Button(name,()=> { prefs.Workspace=name; Save(); RefreshApps(); }));
        row.Children.Add(left);
        topDate.HorizontalAlignment=HorizontalAlignment.Center; Grid.SetColumn(topDate,1); row.Children.Add(topDate);
        var right=new StackPanel { Orientation=Orientation.Horizontal }; Grid.SetColumn(right,2);
        right.Children.Add(Button("Сеть",()=>Launch("ms-settings:network")));
        right.Children.Add(Button("Звук",()=>Launch("ms-settings:sound")));
        right.Children.Add(Symbol("\uE713",OpenSettings,"Панель управления")); row.Children.Add(right);
        Panel("Верхняя панель",.0125,.012,(DesktopArea.Width-48*EffectiveScale)/EffectiveScale,58,row);
    }
    static Button Symbol(string glyph,Action action,string hint,int size=22)
    {
        var b=Button(hint,action,hint); b.Content=new TextBlock { Text=glyph, FontFamily=new FontFamily("Segoe MDL2 Assets"), FontSize=size, VerticalAlignment=VerticalAlignment.Center }; return b;
    }
    void CreateClock()
    {
        var col = new StackPanel(); clock.FontSize = 52; clock.FontWeight = FontWeights.Light; date.FontSize = 14;
        col.Children.Add(clock); col.Children.Add(date); Panel("Часы", .0125,.1,325,132,col);
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
        Panel("Музыка", .0125,.255,325,166,col);
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
        var col = new StackPanel();
        col.Children.Add(Label("Приложения",22));
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,8,0,8) };
        foreach (var name in new[] { "Все", "Работа", "Игры" }) tabs.Children.Add(Button(name, () => { category=name; RefreshApps(); }));
        col.Children.Add(tabs);
        search.ToolTip = "Найти приложение"; search.TextChanged += (_, _) => RefreshApps(); col.Children.Add(search);
        grid.Margin = new Thickness(0,12,0,0);
        col.Children.Add(new ScrollViewer { Content=grid, Height=240, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled });
        Panel("Приложения", -1,.1,AppPanelWidth,410,col); RefreshApps();
    }
    double AppPanelWidth => 4*(prefs.AppIconSize+34)+50;
    void RefreshApps()
    {
        grid.Children.Clear();
        var filtered = apps.Where(a => a.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase));
        if(category == "Игры") filtered = filtered.Where(a => IsGame(a.Name));
        if(category == "Работа") filtered = filtered.Where(a => !IsGame(a.Name));
        foreach (var a in filtered.DistinctBy(a=>a.Name,StringComparer.OrdinalIgnoreCase))
        {
            var contents = new StackPanel { Width=prefs.AppIconSize+18, MinHeight=prefs.AppIconSize+(prefs.ShowIconNames?30:6) };
            contents.Children.Add(Icon(a.Path, prefs.AppIconSize));
            if(prefs.ShowIconNames) contents.Children.Add(new TextBlock { Text=a.Name, FontSize=12, TextAlignment=TextAlignment.Center, TextTrimming=TextTrimming.CharacterEllipsis, Margin=new Thickness(0,7,0,0) });
            var b = Button(a.Name, () => Launch(a.Path), a.Name + "\nПравый клик — закрепить в доке"); b.Content=contents; b.Padding=new Thickness(7,8,7,8);
            var menu = new ContextMenu(); var pin = new MenuItem { Header="Закрепить / убрать из дока" };
            pin.Click += (_, _) => { if(prefs.Pinned.Any(p => p.Path==a.Path)) prefs.Pinned.RemoveAll(p => p.Path==a.Path); else prefs.Pinned.Add(a); Save(); RefreshDock(); }; menu.Items.Add(pin); b.ContextMenu=menu;
            grid.Children.Add(b);
        }
        if(grid.Children.Count==0) grid.Children.Add(new TextBlock { Text="Приложения не найдены", Margin=new Thickness(4,16,0,0) });
    }
    static bool IsGame(string name) => new[] { "steam", "game", "forza", "counter", "discord", "игр", "xbox", "battle", "epic" }.Any(s => name.Contains(s,StringComparison.OrdinalIgnoreCase));
    void CreateFolders() { Panel("Папки", -1,.5,300,190,folders); RefreshFolders(); }
    void RefreshFolders()
    {
        folders.Children.Clear(); folders.Children.Add(Label("▱  Папки",20));
        foreach(var f in prefs.Folders.Take(3)) { var b=Button("▱    " + f.Name + "    ›", () => Launch(f.Path),f.Path); b.HorizontalContentAlignment=HorizontalAlignment.Left; folders.Children.Add(b); }
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
        var explorer=Symbol("\uE8B7",()=>Launch("explorer.exe"),"Проводник",size); explorer.Padding=new Thickness(9,8,9,8); dock.Children.Add(explorer);
        var pinned=prefs.Pinned.Count>0 ? prefs.Pinned : apps.Where(a=>new[] { "edge","telegram","steam","obsidian" }.Any(n=>a.Name.Contains(n,StringComparison.OrdinalIgnoreCase))).Take(5).ToList();
        foreach(var a in pinned.Take(7)) { var b=Button(a.Name,()=>Launch(a.Path)); b.Content=Icon(a.Path,size); b.Padding=new Thickness(9,8,9,8); dock.Children.Add(b); }
        var control=Symbol("\uE713",OpenSettings,"Настройки Бета 1",size); control.Padding=new Thickness(9,8,9,8); dock.Children.Add(control);
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
        Range("Плотность фона",.55,1,prefs.Opacity,v=>prefs.Opacity=v);
        Check("Показывать названия приложений",prefs.ShowIconNames,v=> { prefs.ShowIconNames=v; RefreshApps(); });
        Check("Скрывать стандартную панель задач Windows",prefs.HideTaskbar,v=> { prefs.HideTaskbar=v; if(v) desktop.HideTaskbars(); else desktop.RestoreTaskbars(); });
        Check("Убирать с рабочего стола ярлыки, добавленные в Бета 1",prefs.HideDuplicates,v=> { prefs.HideDuplicates=v; ReloadApps(); });
        body.Children.Add(new TextBlock { Text="Ярлыки временно хранятся в профиле Бета 1 и возвращаются при выходе. Остальные файлы, папки и системные значки остаются на месте. Обои не меняются.", TextWrapping=TextWrapping.Wrap, Opacity=.7, Margin=new Thickness(0,0,0,12) });
        bool autostart;
        using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) autostart=key?.GetValue("Beta1")!=null;
        Check("Запускать при входе в Windows",autostart,v=> { using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); if(v) key.SetValue("Beta1","\""+Environment.ProcessPath+"\""); else key.DeleteValue("Beta1",false); });
        body.Children.Add(Label("Быстрый доступ к папкам",19));
        for(int i=0;i<Math.Min(3,prefs.Folders.Count);i++) { int index=i; var choose=Button(prefs.Folders[i].Name+"  ·  Выбрать папку",()=> { var dialog=new OpenFolderDialog(); if(dialog.ShowDialog()==true) { prefs.Folders[index]=new(Path.GetFileName(dialog.FolderName),dialog.FolderName); Save(); RefreshFolders(); } }); body.Children.Add(choose); }
        body.Children.Add(Button("Обновить список приложений",ReloadApps));
        body.Children.Add(Button("Сбросить расположение панелей",()=> { prefs.Positions.Clear(); Reposition(); Save(); }));
        body.Children.Add(new TextBlock { Text="Alt + перетаскивание — переместить панель. Правый клик на приложении — закрепить в доке. Значок Бета 1 в собственном трее открывает эту панель управления.", TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,18,0,12), Opacity=.75 });
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
            if(name=="Приложения") spec[2]=AppPanelWidth;
            if(name=="Верхняя панель") spec[2]=(DesktopArea.Width-48*EffectiveScale)/EffectiveScale;
            var border=(Border)w.Content;
            border.Background=new SolidColorBrush(Color.FromArgb((byte)(255*prefs.Opacity),23,29,27));
            border.LayoutTransform=new ScaleTransform(EffectiveScale,EffectiveScale);
            w.Width=spec[2]*EffectiveScale; w.Height=spec[3]*EffectiveScale;
        }
        RefreshApps(); RefreshDock(); Reposition();
    }
    void ImportDuplicates()
    {
        var map=desktop.ImportDesktopLinks(apps);
        foreach(var pair in map) originalPaths[pair.Value]=pair.Key;
        for(int i=0;i<apps.Count;i++) if(map.TryGetValue(apps[i].Path,out var stored)) apps[i]=apps[i] with { Path=stored };
        for(int i=0;i<prefs.Pinned.Count;i++) if(map.TryGetValue(prefs.Pinned[i].Path,out var stored)) prefs.Pinned[i]=prefs.Pinned[i] with { Path=stored };
    }
    void ReloadApps()
    {
        NormalizePins(); desktop.RestoreLinks(); originalPaths.Clear(); apps.Clear(); DiscoverApps();
        if(prefs.HideDuplicates && !smoke) ImportDuplicates(); RefreshApps(); RefreshDock(); Save();
    }
    void NormalizePins()
    {
        for(int i=0;i<prefs.Pinned.Count;i++) if(originalPaths.TryGetValue(prefs.Pinned[i].Path,out var original)) prefs.Pinned[i]=prefs.Pinned[i] with { Path=original };
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
    void DiscoverApps()
    {
        foreach(var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
            Scan(root, !root.Equals(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),StringComparison.OrdinalIgnoreCase) && !root.Equals(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),StringComparison.OrdinalIgnoreCase));
        var sorted=apps.DistinctBy(a=>a.Path,StringComparer.OrdinalIgnoreCase).OrderBy(a=>a.Name).ToList(); apps.Clear(); apps.AddRange(sorted);
    }
    void Scan(string root,bool recursive=true)
    {
        if(!Directory.Exists(root)) return;
        try { foreach(var path in Directory.GetFiles(root)) if(new[]{".lnk",".url",".exe"}.Contains(Path.GetExtension(path).ToLowerInvariant())) apps.Add(new(Path.GetFileNameWithoutExtension(path),path)); if(recursive) foreach(var dir in Directory.GetDirectories(root)) Scan(dir); } catch(UnauthorizedAccessException) { } catch(IOException) { }
    }
    static UIElement Icon(string path,int size)
    {
        try { using var icon=System.Drawing.Icon.ExtractAssociatedIcon(path); if(icon!=null) { var source=Imaging.CreateBitmapSourceFromHIcon(icon.Handle,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions()); source.Freeze(); return new System.Windows.Controls.Image { Source=source, Width=size, Height=size }; } } catch { }
        return new TextBlock { Text="▣", FontSize=size-5, TextAlignment=TextAlignment.Center, Foreground=Brush("#54E0DA") };
    }
    static void Launch(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute=true }); } catch(Exception ex) { Forms.MessageBox.Show("Не удалось открыть:\n"+path+"\n\n"+ex.Message,"Бета 1"); }
    }
    static void MediaKey(byte key) { keybd_event(key,0,0,UIntPtr.Zero); keybd_event(key,0,2,UIntPtr.Zero); }
    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged-=DisplayChanged; timer.Stop();
        if(ownsInstance)
        {
            try { NormalizePins(); if(!smoke) desktop.RestoreAll(); Save(); } catch(Exception ex) { Trace.WriteLine(ex); }
        }
        tray?.Dispose(); brandIcon?.Dispose(); openSettingsEvent?.Dispose(); if(ownsInstance) mutex?.ReleaseMutex(); mutex?.Dispose(); base.OnExit(e);
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
