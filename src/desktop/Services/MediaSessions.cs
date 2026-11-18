using Windows.Media.Control;
using Windows.Storage.Streams;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace desktop.Services;

public record Song(string Title="未知歌曲", string Artist="未知艺术家", string Album="未知专辑", string PlaybackStatus="unknown", string Cover="");
public record MediaChoice(GlobalSystemMediaTransportControlsSession Session,string AppId)
{
    public string Label=>AppId;
}
public sealed class MediaSessions : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private GlobalSystemMediaTransportControlsSession? selected;
    private int revision;
    private bool disposed;
    public event Action<IReadOnlyList<MediaChoice>>? SessionsChanged;
    public event Action<Song>? SongChanged;
    public event Action<string>? Warning;
    public async Task InitializeAsync()
    {
        try{var available=await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();if(disposed)return;manager=available;manager.SessionsChanged+=OnSessionsChanged;Refresh();}
        catch(Exception ex){Warning?.Invoke("SMTC 不可用，仍可传输音频："+ex.Message);SongChanged?.Invoke(new());}
    }
    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender,SessionsChangedEventArgs args)=>Refresh();
    public void Refresh()
    {
        if(disposed)return;
        try{SessionsChanged?.Invoke(manager?.GetSessions().Select(s=>new MediaChoice(s,s.SourceAppUserModelId)).ToList()??[]);}
        catch(Exception ex){Warning?.Invoke("读取媒体会话失败："+ex.Message);}
    }
    // A session is chosen independently from the capture PID. Never use CurrentSession as a fallback.
    public static MediaChoice? Match(AudioProcess process,IReadOnlyList<MediaChoice> choices)
    {
        var exact=choices.Where(s=>!string.IsNullOrEmpty(process.AppId)&&string.Equals(s.AppId,process.AppId,StringComparison.OrdinalIgnoreCase)).ToList();
        if(exact.Count==1)return exact[0];
        var name=Path.GetFileNameWithoutExtension(process.Name);
        var byName=choices.Where(s=>string.Equals(Path.GetFileNameWithoutExtension(s.AppId),name,StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count==1?byName[0]:null;
    }
    public void Select(MediaChoice? choice)
    {
        if(disposed&&choice!=null)return;
        if(selected!=null){selected.MediaPropertiesChanged-=OnProperties;selected.PlaybackInfoChanged-=OnPlayback;}
        selected=choice?.Session;Interlocked.Increment(ref revision);
        if(selected!=null){selected.MediaPropertiesChanged+=OnProperties;selected.PlaybackInfoChanged+=OnPlayback;}
        _=ReadAsync();
    }
    private void OnProperties(GlobalSystemMediaTransportControlsSession sender,MediaPropertiesChangedEventArgs args)=>_ = ReadAsync();
    private void OnPlayback(GlobalSystemMediaTransportControlsSession sender,PlaybackInfoChangedEventArgs args)=>_ = ReadAsync();
    private async Task ReadAsync()
    {
        var session=selected;int current=Interlocked.Increment(ref revision);
        if(session==null){SongChanged?.Invoke(new());return;}
        try{
            var properties=await session.TryGetMediaPropertiesAsync();
            string status=session.GetPlaybackInfo().PlaybackStatus switch{GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing=>"playing",GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused=>"paused",GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed=>"stopped",_=>"unknown"};
            string cover="";
            if(properties.Thumbnail!=null)try{
                using var stream=await properties.Thumbnail.OpenReadAsync();
                if(stream.Size<=12*1024*1024){using var reader=new DataReader(stream);uint length=(uint)stream.Size;await reader.LoadAsync(length);var bytes=new byte[length];reader.ReadBytes(bytes);
                    using var input=new MemoryStream(bytes);var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=384;image.StreamSource=input;image.EndInit();image.Freeze();
                    var encoder=new JpegBitmapEncoder{QualityLevel=75};encoder.Frames.Add(BitmapFrame.Create(image));using var encoded=new MemoryStream();encoder.Save(encoded);if(encoded.Length<=500*1024)cover="data:image/jpeg;base64,"+Convert.ToBase64String(encoded.ToArray());}
            }catch{/* A bad thumbnail must not prevent song and playback updates. */}
            if(current==revision)SongChanged?.Invoke(new(string.IsNullOrWhiteSpace(properties.Title)?"未知歌曲":properties.Title,string.IsNullOrWhiteSpace(properties.Artist)?"未知艺术家":properties.Artist,string.IsNullOrWhiteSpace(properties.AlbumTitle)?"未知专辑":properties.AlbumTitle,status,cover));
        }catch(Exception ex){if(current==revision){SongChanged?.Invoke(new());Warning?.Invoke("媒体信息获取失败："+ex.Message);}}
    }
    public void Dispose(){if(disposed)return;disposed=true;if(manager!=null)manager.SessionsChanged-=OnSessionsChanged;Select(null);}
}
