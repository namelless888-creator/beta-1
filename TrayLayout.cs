using System;
using System.Windows;
namespace Beta1;
internal static class TrayLayout
{
    // Physical tray coordinates are converted once at the window's DPI boundary.
    public static Rect Frame(Rect nativeDip,Size screen,double scale)
    {
        double right=Math.Clamp(nativeDip.Right,1,screen.Width);
        double left=Math.Max(0,nativeDip.Left-60*scale);
        double bottom=Math.Clamp(nativeDip.Bottom,1,screen.Height);
        double height=Math.Min(bottom,Math.Max(nativeDip.Height+12*scale,58*scale));
        return new Rect(left,Math.Max(0,bottom-height),Math.Max(1,right-left),height);
    }
}
