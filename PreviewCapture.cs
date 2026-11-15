using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace Beta1;
internal static class PreviewCapture
{
    public static void Desktop(IEnumerable<Window> windows)
    {
        var scene=new DrawingVisual(); using(var context=scene.RenderOpen())
        {
            context.DrawRectangle(new LinearGradientBrush(Color.FromRgb(70,77,58),Color.FromRgb(21,27,24),40),null,new Rect(0,0,1920,1080));
            foreach(var window in windows)
            {
                window.UpdateLayout();
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.Width),(int)Math.Ceiling(window.Height),96,96,PixelFormats.Pbgra32);
                var content=(FrameworkElement)window.Content;
                content.Measure(new Size(window.Width,window.Height)); content.Arrange(new Rect(0,0,window.Width,window.Height)); content.UpdateLayout();
                bitmap.Render(content); context.DrawImage(bitmap,new Rect(window.Left,window.Top,window.Width,window.Height));
            }
        }
        var all=new RenderTargetBitmap(1920,1080,96,96,PixelFormats.Pbgra32); all.Render(scene); Save(all,"desktop-preview.png");
    }
    public static void Element(FrameworkElement element,string name)
    {
        element.UpdateLayout(); var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32); bitmap.Render(element); Save(bitmap,name);
    }
    static void Save(BitmapSource image,string name) { var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file=File.Create(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, name)); encoder.Save(file); }
}
