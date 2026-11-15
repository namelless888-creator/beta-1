using System;
using System.Runtime.InteropServices;
namespace Beta1;
internal sealed class AudioLevel : IDisposable
{
    readonly IAudioEndpointVolume volume;
    public AudioLevel()
    {
        var enumerator=(IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
        IMMDevice? device=null;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0,1,out device)); var iid=typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid,23,IntPtr.Zero,out var endpoint)); volume=(IAudioEndpointVolume)endpoint;
        }
        finally { if(device!=null) Marshal.FinalReleaseComObject(device); Marshal.FinalReleaseComObject(enumerator); }
    }
    public double Level { get { Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out var value)); return Math.Round(value*100); } set { var context=Guid.Empty; Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar((float)Math.Clamp(value/100,0,1),ref context)); } }
    public bool Muted { get { Marshal.ThrowExceptionForHR(volume.GetMute(out var mute)); return mute; } set { var context=Guid.Empty; Marshal.ThrowExceptionForHR(volume.SetMute(value,ref context)); } }
    public void Dispose() => Marshal.FinalReleaseComObject(volume);
    [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMMDeviceEnumerator
    { [PreserveSig] int EnumAudioEndpoints(int flow,int mask,out IntPtr devices); [PreserveSig] int GetDefaultAudioEndpoint(int flow,int role,out IMMDevice device); }
    [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMMDevice
    { [PreserveSig] int Activate(ref Guid iid,int context,IntPtr parameters,[MarshalAs(UnmanagedType.IUnknown)] out object result); }
    [ComImport,Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify); [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint channels); [PreserveSig] int SetMasterVolumeLevel(float value,ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float value,ref Guid context); [PreserveSig] int GetMasterVolumeLevel(out float value);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float value); [PreserveSig] int SetChannelVolumeLevel(uint channel,float value,ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel,float value,ref Guid context); [PreserveSig] int GetChannelVolumeLevel(uint channel,out float value);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel,out float value); [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute,ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
