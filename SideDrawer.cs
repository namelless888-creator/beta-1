using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
namespace Beta1;
internal sealed class SideDrawer
{
    readonly Window panel,tab;
    readonly TextBlock arrow;
    readonly DispatcherTimer closeTimer=new() { Interval=TimeSpan.FromMilliseconds(190) };
    bool open,positioning;
    double destination;
    public SideDrawer(Window panel,string title,double unused)
    {
        this.panel=panel; destination=panel.Left;
        var content=new StackPanel { HorizontalAlignment=HorizontalAlignment.Center };
        content.Children.Add(new TextBlock { Text=title=="Папки"?"\uE8B7":"\uE71D",FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=20,Foreground=new SolidColorBrush(Color.FromRgb(100,228,218)),Margin=new Thickness(0,0,0,10),HorizontalAlignment=HorizontalAlignment.Center });
        arrow=new TextBlock { Text="\uE76B",FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=16,HorizontalAlignment=HorizontalAlignment.Center }; content.Children.Add(arrow);
        var button=new Button { Content=content,Padding=new Thickness(8),ToolTip=title };
        tab=new Window { Width=40,Height=76,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,ShowActivated=false,AllowsTransparency=true,Background=Brushes.Transparent,Content=new Border { CornerRadius=new CornerRadius(12,0,0,12),Background=new SolidColorBrush(Color.FromRgb(32,48,42)),BorderBrush=new SolidColorBrush(Color.FromRgb(87,114,98)),BorderThickness=new Thickness(1),Child=button } };
        button.Click+=(_,_)=>Toggle();
        closeTimer.Tick+=(_,_)=> { closeTimer.Stop(); if(!open) panel.Hide(); };
        panel.LocationChanged+=(_,_)=>Reposition(); panel.SizeChanged+=(_,_)=>Reposition();
        panel.Hide(); tab.Show(); Reposition();
    }
    internal double TabTop=>tab.Top;
    internal double TabLeft=>tab.Left;
    internal void Toggle()
    {
        closeTimer.Stop(); open=!open; arrow.Text=open?"\uE76C":"\uE76B";
        if(open) { panel.Show(); panel.Topmost=true; tab.Topmost=true; }
        double from=panel.IsVisible?panel.Left:SystemParameters.PrimaryScreenWidth;
        panel.BeginAnimation(Window.LeftProperty,null); panel.Left=open?destination:SystemParameters.PrimaryScreenWidth;
        panel.BeginAnimation(Window.LeftProperty,new DoubleAnimation(from,panel.Left,TimeSpan.FromMilliseconds(160)) { EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut } });
        if(!open) closeTimer.Start(); Reposition();
    }
    void Reposition()
    {
        if(positioning) return; positioning=true;
        try
        {
            if(open && panel.Left<SystemParameters.PrimaryScreenWidth-panel.Width/2) destination=SystemParameters.PrimaryScreenWidth-panel.Width-24;
            tab.Top=Math.Clamp(panel.Top+(panel.Height-tab.Height)/2,0,SystemParameters.PrimaryScreenHeight-tab.Height);
            tab.Left=open?Math.Max(0,panel.Left-tab.Width+1):SystemParameters.PrimaryScreenWidth-tab.Width;
        }
        finally { positioning=false; }
    }
    public void Close() { closeTimer.Stop(); tab.Close(); }
}
