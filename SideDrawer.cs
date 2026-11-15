using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace Beta1;
internal sealed class SideDrawer
{
    readonly Window panel,tab;
    bool open;
    double destination;
    public SideDrawer(Window panel,string title,double y)
    {
        this.panel=panel; destination=panel.Left;
        var button=new Button { Content="‹  "+title,Padding=new Thickness(8,12,8,12),Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromRgb(32,48,42)) };
        tab=new Window { Width=42,Height=118,Left=SystemParameters.PrimaryScreenWidth-42,Top=y,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,ShowActivated=false,AllowsTransparency=true,Background=Brushes.Transparent,Content=new Border { CornerRadius=new CornerRadius(12,0,0,12),Background=new SolidColorBrush(Color.FromRgb(32,48,42)),Child=button } };
        button.Content=new TextBlock { Text="‹\n"+title,TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap,Width=24 };
        button.Click+=(_,_)=> { open=!open; if(open) { panel.Show(); panel.Topmost=true; } panel.BeginAnimation(Window.LeftProperty,new DoubleAnimation(open?SystemParameters.PrimaryScreenWidth:destination,open?destination:SystemParameters.PrimaryScreenWidth,TimeSpan.FromMilliseconds(180)) { EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut } }); if(!open) { var timer=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(200) }; timer.Tick+=(_,_)=> { timer.Stop(); if(!open) panel.Hide(); }; timer.Start(); } };
        panel.Hide(); tab.Show();
    }
    public void Close() => tab.Close();
}
