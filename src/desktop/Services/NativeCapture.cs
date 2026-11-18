using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using System.Text;

namespace desktop.Services;

public record AudioProcess(uint Pid, uint Parent, string Name, bool HasAudio, string AppId, bool HasActiveAudio = false)
{
    public string Label => $"{Name}   ·   PID {Pid}";
    public string AudioStatus => HasActiveAudio ? "音频活动" : HasAudio ? "音频空闲" : "无音频会话";
}
public sealed class NativeCapture : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PcmCallback(IntPtr pcm, uint frames);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ErrorCallback(int error);
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct NativeProcess
    {
        public uint Pid, Parent;
        public int HasAudio;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string Name;
    }
    [DllImport("LmtCapture", CallingConvention=CallingConvention.Cdecl)] private static extern int lmt_start(uint pid, PcmCallback pcm, ErrorCallback error, out IntPtr capture);
    [DllImport("LmtCapture", CallingConvention=CallingConvention.Cdecl)] private static extern void lmt_stop(IntPtr capture);
    [DllImport("LmtCapture", CallingConvention=CallingConvention.Cdecl)] private static extern int lmt_processes([Out] NativeProcess[] processes, int capacity);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder? id);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    private IntPtr handle;
    private PcmCallback? pcmDelegate;
    private ErrorCallback? errorDelegate;
    public event Action<short[]>? Samples;
    public event Action<string>? Failed;
    public bool IsRunning => handle != IntPtr.Zero;
    public static string Explain(int code) => unchecked((uint)code) switch
    {
        0x80070005 => "访问被拒绝。目标程序可能以更高权限运行或受到保护，请以相同权限启动 LMT。",
        0x80070057 => "目标 PID 或音频参数无效。请确认系统支持 Process Loopback，并重新选择进程。",
        0x80004001 or 0x80004002 or 0x80040154 => "当前 Windows 或音频组件不支持进程回环捕获。需要 Windows Build 20348 或更高版本。",
        0x8007042B or 0x80070006 => "目标进程已退出，请重新选择音乐软件。",
        0x800705B4 => "初始化进程音频捕获超时，请重试或检查 Windows 音频服务。",
        0x88890008 => "音频服务不支持请求的 48 kHz 立体声格式。",
        0x88890004 => "音频设备已失效，请检查音频设备并重新开始捕获。",
        _ => $"进程音频捕获失败：{Marshal.GetExceptionForHR(code)?.Message ?? "未知错误"} (0x{code:X8})"
    };
    public void Start(uint pid)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10,0,20348)) throw new NotSupportedException("需要 Windows Build 20348 或更高版本。");
        Stop();
        pcmDelegate=(ptr,frames)=>{try{var samples=new short[checked((int)frames*2)];Marshal.Copy(ptr,samples,0,samples.Length);Samples?.Invoke(samples);}catch(Exception e){Failed?.Invoke("音频回调失败："+e.Message);}};
        errorDelegate=code=>Failed?.Invoke(Explain(code));
        int hr=lmt_start(pid,pcmDelegate,errorDelegate,out handle);
        if(hr<0){pcmDelegate=null;errorDelegate=null;throw new InvalidOperationException(Explain(hr));}
    }
    public void Stop(){if(handle!=IntPtr.Zero){lmt_stop(handle);handle=IntPtr.Zero;}pcmDelegate=null;errorDelegate=null;}
    public void Dispose()=>Stop();
    public static List<AudioProcess> Enumerate(bool showAll, bool activeOnly = false)
    {
        var entries=new NativeProcess[8192];int count=Math.Min(lmt_processes(entries,entries.Length),entries.Length);
        var map=entries.Take(count).ToDictionary(p=>p.Pid);
        var audioRoots=new HashSet<uint>();
        var activeRoots=new HashSet<uint>();
        var launchers=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"explorer.exe","services.exe","svchost.exe","wininit.exe","winlogon.exe","cmd.exe","powershell.exe","pwsh.exe","WindowsTerminal.exe","conhost.exe"};
        foreach(var p in map.Values.Where(p=>(p.HasAudio&1)!=0)){
            uint pid=p.Pid;var visited=new HashSet<uint>();
            while(pid!=0&&visited.Add(pid)&&map.TryGetValue(pid,out var parent)){if(launchers.Contains(parent.Name)&&pid!=p.Pid)break;audioRoots.Add(pid);if((p.HasAudio&2)!=0)activeRoots.Add(pid);pid=parent.Parent;}
        }
        return map.Values.Where(p=>p.Pid!=Environment.ProcessId&&(activeOnly?activeRoots.Contains(p.Pid):showAll||audioRoots.Contains(p.Pid)))
            .Select(p=>new AudioProcess(p.Pid,p.Parent,p.Name,audioRoots.Contains(p.Pid),AppId(p.Pid),activeRoots.Contains(p.Pid)))
            .OrderBy(p=>p.Name,StringComparer.OrdinalIgnoreCase).ThenBy(p=>p.Pid).ToList();
    }
    private static string AppId(uint pid)
    {
        try{using var handle=OpenProcess(0x1000,false,pid);if(handle.IsInvalid)return "";uint length=0;GetApplicationUserModelId(handle,ref length,null);if(length==0||length>4096)return "";var id=new StringBuilder((int)length);return GetApplicationUserModelId(handle,ref length,id)==0?id.ToString():"";}catch{return "";}
    }
}
