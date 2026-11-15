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
    public double Opacity { get; set; } = .84;
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
    Slider progress = new() { Minimum=0, Maximum=1, IsHitTestVisible=false };
    GlobalSystemMediaTransportControlsSessionManager? media;
    bool readingMedia;
    bool shellHidden;
    bool ownsInstance;
    readonly List<IntPtr> hiddenShellWindows = new();
    string category = "Все";
    Window? settings;
    System.Threading.Mutex? mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        mutex = new System.Threading.Mutex(true, "Beta1.Desktop.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }
        ownsInstance=true;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try { if (File.Exists(storage)) prefs = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(storage)) ?? new(); } catch { }
        if (prefs.Folders.Count == 0) prefs.Folders = new() {
            new("Проекты", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            new("Документы", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            new("Загрузки", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")) };
        DiscoverApps();
        CreateTop(); CreateClock(); CreateMusic(); CreateApps(); CreateFolders(); CreateDock(); CreateTrayPanel();
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Бета 1 — рабочий стол", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Показать / скрыть панели", null, (_, _) => Dispatcher.Invoke(Toggle));
        menu.Items.Add("Настройки", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(() => Shutdown()));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(OpenSettings);
        timer.Interval = TimeSpan.FromSeconds(1); timer.Tick += (_, _) => Tick(); timer.Start(); Tick();
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        bool smoke = e.Args.Contains("--smoke-test");
        DispatcherUnhandledException += (_, args) => { if(smoke) { Shutdown(2); return; } Forms.MessageBox.Show(args.Exception.Message, "Бета 1"); args.Handled = true; };
        if(smoke)
        {
            var check = new DispatcherTimer { Interval=TimeSpan.FromSeconds(3) };
            check.Tick += (_, _) => { check.Stop(); Shutdown(panels.Count==7 && panels.All(w => w.IsVisible && new WindowInteropHelper(w).Handle!=IntPtr.Zero) ? 0 : 3); };
            check.Start();
        }
    }
    void Save() { Directory.CreateDirectory(Path.GetDirectoryName(storage)!); File.WriteAllText(storage, JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true })); }
    static Brush Brush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
    static TextBlock Label(string text, double size = 14) => new() { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
    static Button Button(string label, Action action, string? hint = null)
    {
        var b = new Button { Content = label, ToolTip = hint ?? label }; b.Click += (_, _) => action(); return b;
    }
    Window Panel(string id, double x, double y, double width, double height, UIElement content)
    {
        var area = SystemParameters.WorkArea;
        width = Math.Min(width, area.Width - 24); height = Math.Min(height, area.Height - 24);
        var w = new Window { Title = "Бета 1 · " + id, Width = width, Height = height, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, ShowActivated = false, Topmost = false };
        w.Tag = new double[] { x, y };
        var saved = prefs.Positions.GetValueOrDefault(id);
        w.Left = area.Left + Math.Clamp(saved?[0] ?? x * area.Width, 0, Math.Max(0, area.Width - width));
        w.Top = area.Top + Math.Clamp(saved?[1] ?? y * area.Height, 0, Math.Max(0, area.Height - height));
        var border = new Border { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1),
            BorderBrush = Brush("#80E8ECE7"), Background = new SolidColorBrush(Color.FromArgb((byte)(255 * prefs.Opacity), 29, 33, 29)),
            Padding = new Thickness(16), Child = content };
        w.Content = border;
        w.PreviewMouseLeftButtonDown += (_, ev) => { if (Keyboard.Modifiers == ModifierKeys.Alt) { w.DragMove(); ev.Handled = true; prefs.Positions[id] = new[] { w.Left - area.Left, w.Top - area.Top }; Save(); } };
        w.PreviewKeyDown += (_, ev) => { if (ev.Key == Key.Escape) w.Hide(); };
        w.Deactivated += (_, _) => Lower(w);
        w.SourceInitialized += (_, _) => { var h = new WindowInteropHelper(w).Handle; SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x80); var desktop=FindWindow("Progman", null); if(desktop!=IntPtr.Zero) SetWindowLongPtr(h, -8, desktop); };
        panels.Add(w); w.Show(); Lower(w); return w;
    }
    static void Lower(Window w) { if (w.IsVisible) SetWindowPos(new WindowInteropHelper(w).Handle, new IntPtr(1), 0, 0, 0, 0, 0x13); }
    void CreateTop()
    {
        var row = new DockPanel { LastChildFill = true };
        var brand = Label("БЕТА 1", 19); brand.FontWeight = FontWeights.SemiBold; brand.Margin = new Thickness(4,0,24,0); DockPanel.SetDock(brand, Dock.Left); row.Children.Add(brand);
        var modes = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(modes,Dock.Left);
        foreach (var name in new[] { "Работа", "Личное" }) modes.Children.Add(Button(name, () => { prefs.Workspace = name; Save(); RefreshApps(); }, "Открыть подборку: " + name)); row.Children.Add(modes);
        var right = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(right,Dock.Right);
        right.Children.Add(Button("⌕", () => { panels.First(w => w.Title.EndsWith("Приложения")).Activate(); search.Focus(); }, "Поиск приложений"));
        right.Children.Add(Button("Wi-Fi", () => Launch("ms-settings:network-wifi")));
        right.Children.Add(Button("Звук", () => Launch("ms-settings:sound")));
        right.Children.Add(Button("⚙", OpenSettings, "Настройки Бета 1")); row.Children.Add(right);
        topDate.HorizontalAlignment = HorizontalAlignment.Center; row.Children.Add(topDate);
        Panel("Верхняя панель", .016, .01, SystemParameters.WorkArea.Width - 48, 52, row);
    }
    void CreateClock()
    {
        var col = new StackPanel(); clock.FontSize = 52; clock.FontWeight = FontWeights.Light; date.FontSize = 14;
        col.Children.Add(clock); col.Children.Add(date); Panel("Часы", .016,.09,325,123,col);
    }
    void CreateMusic()
    {
        var col = new StackPanel();
        var row = new DockPanel(); artwork.Margin=new Thickness(0,0,12,0); DockPanel.SetDock(artwork,Dock.Left); row.Children.Add(artwork);
        var info=new StackPanel(); track.Text="Музыка"; track.FontSize=19; track.TextTrimming=TextTrimming.CharacterEllipsis;
        artist.Text="Открой проигрыватель"; artist.Opacity=.75; artist.TextTrimming=TextTrimming.CharacterEllipsis; info.Children.Add(track); info.Children.Add(artist);
        var controls = new StackPanel { Orientation=Orientation.Horizontal };
        controls.Children.Add(Button("⏮", () => MediaKey(0xB1), "Предыдущий трек"));
        controls.Children.Add(Button("▶ / Ⅱ", () => MediaKey(0xB3), "Воспроизведение / пауза"));
        controls.Children.Add(Button("⏭", () => MediaKey(0xB0), "Следующий трек")); info.Children.Add(controls);
        row.Children.Add(info); col.Children.Add(row); progress.Margin=new Thickness(0,12,0,0); progress.Foreground=Brush("#54E0DA"); col.Children.Add(progress);
        Panel("Музыка", .016,.235,325,155,col);
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
        col.Children.Add(new ScrollViewer { Content=grid, Height=208, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled });
        Panel("Приложения", .762,.083,390,365,col); RefreshApps();
    }
    void RefreshApps()
    {
        grid.Children.Clear();
        var filtered = apps.Where(a => a.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase));
        if(category == "Игры") filtered = filtered.Where(a => IsGame(a.Name));
        if(category == "Работа" || (category == "Все" && prefs.Workspace == "Работа" && search.Text.Length == 0)) filtered = filtered.Where(a => !IsGame(a.Name));
        foreach (var a in filtered.Take(100))
        {
            var contents = new StackPanel { Width=72 };
            contents.Children.Add(Icon(a.Path, 38));
            contents.Children.Add(new TextBlock { Text=a.Name, FontSize=12, TextAlignment=TextAlignment.Center, TextTrimming=TextTrimming.CharacterEllipsis, Margin=new Thickness(0,7,0,0) });
            var b = Button(a.Name, () => Launch(a.Path), a.Name + "\nПравый клик — закрепить в доке"); b.Content=contents; b.Padding=new Thickness(7,8,7,8);
            var menu = new ContextMenu(); var pin = new MenuItem { Header="Закрепить / убрать из дока" };
            pin.Click += (_, _) => { if(prefs.Pinned.Any(p => p.Path==a.Path)) prefs.Pinned.RemoveAll(p => p.Path==a.Path); else prefs.Pinned.Add(a); Save(); RefreshDock(); }; menu.Items.Add(pin); b.ContextMenu=menu;
            grid.Children.Add(b);
        }
        if(grid.Children.Count==0) grid.Children.Add(new TextBlock { Text="Приложения не найдены", Margin=new Thickness(4,16,0,0) });
    }
    static bool IsGame(string name) => new[] { "steam", "game", "forza", "counter", "discord", "игр", "xbox", "battle", "epic" }.Any(s => name.Contains(s,StringComparison.OrdinalIgnoreCase));
    void CreateFolders() { Panel("Папки", .815,.5,300,184,folders); RefreshFolders(); }
    void RefreshFolders()
    {
        folders.Children.Clear(); folders.Children.Add(Label("▱  Папки",20));
        foreach(var f in prefs.Folders.Take(3)) { var b=Button("▱    " + f.Name + "    ›", () => Launch(f.Path),f.Path); b.HorizontalContentAlignment=HorizontalAlignment.Left; folders.Children.Add(b); }
    }
    void CreateDock() { dock.Orientation=Orientation.Horizontal; dock.HorizontalAlignment=HorizontalAlignment.Center; Panel("Док", .325,.918,600,68,dock); RefreshDock(); }
    void RefreshDock()
    {
        dock.Children.Clear(); dock.Children.Add(Button("⊞", () => MediaKey(0x5B),"Пуск"));
        dock.Children.Add(Button("⌕", () => { var w=panels.First(p => p.Title.EndsWith("Приложения")); w.Show(); w.Activate(); search.Focus(); },"Найти приложение"));
        dock.Children.Add(Button("📁", () => Launch("explorer.exe"),"Проводник"));
        var pinned = prefs.Pinned.Count > 0 ? prefs.Pinned : apps.Where(a => new[] { "edge", "telegram", "steam", "obsidian" }.Any(n => a.Name.Contains(n,StringComparison.OrdinalIgnoreCase))).Take(5).ToList();
        foreach(var a in pinned.Take(7)) { var b=Button(a.Name, () => Launch(a.Path)); b.Content=Icon(a.Path,30); dock.Children.Add(b); }
        dock.Children.Add(Button("⚙",OpenSettings,"Настройки"));
    }
    void CreateTrayPanel()
    {
        var row=new StackPanel { Orientation=Orientation.Horizontal }; row.Children.Add(Button("Корзина", () => Launch("shell:RecycleBinFolder"))); row.Children.Add(status);
        Panel("Состояние", .825,.925,290,56,row);
    }
    void Tick()
    {
        UpdateMedia();
        var now=DateTime.Now; var culture=System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        clock.Text=now.ToString("HH:mm"); date.Text=now.ToString("dddd, d MMMM",culture); topDate.Text=date.Text;
        var battery=Forms.SystemInformation.PowerStatus; status.Text="  " + now.ToString("HH:mm") + (battery.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery) ? "" : "  ·  " + Math.Round(battery.BatteryLifePercent*100) + "%");
    }
    void Toggle() { bool show=panels.Any(w => !w.IsVisible); foreach(var w in panels) if(show) { w.Show(); Lower(w); } else w.Hide(); }
    void OpenSettings()
    {
        if(settings != null) { settings.Activate(); return; }
        var body=new StackPanel { Margin=new Thickness(24) };
        body.Children.Add(Label("Бета 1 · Настройки",24));
        body.Children.Add(new TextBlock { Text="Обои остаются без изменений. Перемещение панелей: Alt + перетаскивание. Esc скрывает выбранную панель; вернуть её можно через значок в трее.", TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,16,0,16) });
        body.Children.Add(Label("Плотность фона панелей"));
        var slider=new Slider { Minimum=.3, Maximum=1, Value=prefs.Opacity, Margin=new Thickness(0,12,0,12) };
        slider.ValueChanged += (_, _) => { prefs.Opacity=slider.Value; foreach(var w in panels) ((Border)w.Content).Background=new SolidColorBrush(Color.FromArgb((byte)(255*prefs.Opacity),29,33,29)); }; body.Children.Add(slider);
        var startup=new CheckBox { Content="Запускать при входе в Windows", Foreground=Brushes.White, Margin=new Thickness(0,12,0,12) };
        using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) startup.IsChecked=key?.GetValue("Beta1")!=null;
        startup.Click += (_, _) => { using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); if(startup.IsChecked==true) key.SetValue("Beta1", "\"" + Environment.ProcessPath + "\""); else key.DeleteValue("Beta1",false); }; body.Children.Add(startup);
        var clean=new CheckBox { Content="Скрыть штатные значки и панель задач на время работы", Foreground=Brushes.White, IsChecked=shellHidden, Margin=new Thickness(0,8,0,12) };
        clean.Click += (_, _) => SetCleanDesktop(clean.IsChecked==true); body.Children.Add(clean);
        for(int i=0;i<3;i++) { int index=i; body.Children.Add(Button("Изменить папку «" + prefs.Folders[i].Name + "»", () => { var dialog=new OpenFolderDialog(); if(dialog.ShowDialog()==true) { prefs.Folders[index]=new(Path.GetFileName(dialog.FolderName),dialog.FolderName); Save(); RefreshFolders(); } })); }
        body.Children.Add(Button("Обновить список приложений", () => { apps.Clear(); DiscoverApps(); RefreshApps(); RefreshDock(); }));
        body.Children.Add(Button("Сбросить расположение панелей", () => { prefs.Positions.Clear(); Save(); Reposition(); }));
        body.Children.Add(Button("Завершить Бета 1", () => Shutdown()));
        settings=new Window { Title="Бета 1 — Настройки", Width=490, Height=600, MinHeight=500, Background=Brush("#242923"), Content=new ScrollViewer { Content=body }, WindowStartupLocation=WindowStartupLocation.CenterScreen };
        settings.Closed += (_, _) => { Save(); settings=null; }; settings.Show();
    }
    void SetCleanDesktop(bool hide)
    {
        if(!hide) { foreach(var h in hiddenShellWindows) ShowWindow(h,5); hiddenShellWindows.Clear(); shellHidden=false; return; }
        EnumWindows((window, _) => {
            var view=FindWindowEx(window,IntPtr.Zero,"SHELLDLL_DefView",null);
            if(view!=IntPtr.Zero) { var list=FindWindowEx(view,IntPtr.Zero,"SysListView32",null); if(list!=IntPtr.Zero && IsWindowVisible(list)) { hiddenShellWindows.Add(list); ShowWindow(list,0); } }
            return true;
        },IntPtr.Zero);
        var taskbar=FindWindow("Shell_TrayWnd",null); if(taskbar!=IntPtr.Zero && IsWindowVisible(taskbar)) { hiddenShellWindows.Add(taskbar); ShowWindow(taskbar,0); }
        shellHidden=true;
    }
    void Reposition()
    {
        var a=SystemParameters.WorkArea;
        foreach(var w in panels) { var xy=(double[])w.Tag; var id=w.Title.Replace("Бета 1 · ",""); var pos=prefs.Positions.GetValueOrDefault(id); w.Left=a.Left+Math.Clamp(pos?[0]??xy[0]*a.Width,0,Math.Max(0,a.Width-w.Width)); w.Top=a.Top+Math.Clamp(pos?[1]??xy[1]*a.Height,0,Math.Max(0,a.Height-w.Height)); }
    }
    void DisplayChanged(object? sender,EventArgs e) => Dispatcher.BeginInvoke(Reposition);
    void DiscoverApps()
    {
        foreach(var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
            Scan(root);
        var sorted=apps.DistinctBy(a=>a.Name,StringComparer.OrdinalIgnoreCase).OrderBy(a=>a.Name).ToList(); apps.Clear(); apps.AddRange(sorted);
    }
    void Scan(string root)
    {
        if(!Directory.Exists(root)) return;
        try { foreach(var path in Directory.GetFiles(root)) if(new[]{".lnk",".url",".exe"}.Contains(Path.GetExtension(path).ToLowerInvariant())) apps.Add(new(Path.GetFileNameWithoutExtension(path),path)); foreach(var dir in Directory.GetDirectories(root)) Scan(dir); } catch(UnauthorizedAccessException) { } catch(IOException) { }
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
    protected override void OnExit(ExitEventArgs e) { SystemEvents.DisplaySettingsChanged-=DisplayChanged; timer.Stop(); SetCleanDesktop(false); tray?.Dispose(); if(ownsInstance) Save(); mutex?.Dispose(); base.OnExit(e); }
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
