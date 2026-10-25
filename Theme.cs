using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
namespace ClaudeLocalBridge;
public static class Theme
{
    public static void Apply(Window window,bool dark)
    {
        var palette=dark?new[]{"#11151B","#1A2029","#121820","#EDF1F7","#A8B4C4","#394556","#283548","#354966","#8DAEFF"}:new[]{"#F4F4F1","#FFFFFF","#FAFAF8","#202833","#596575","#BCC5D0","#E5EBF3","#D3E1F5","#3159A5"};
        var keys=new[]{"WindowBrush","PanelBrush","InputBrush","TextBrush","MutedBrush","BorderBrush","ButtonBrush","HoverBrush","AccentBrush"};
        for(var i=0;i<keys.Length;i++)Application.Current.Resources[keys[i]]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[i]));
        var handle=new WindowInteropHelper(window).Handle;
        if(handle!=IntPtr.Zero){int enabled=dark?1:0;try{DwmSetWindowAttribute(handle,20,ref enabled,sizeof(int));}catch(DllNotFoundException){}}
    }
    [DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
}
