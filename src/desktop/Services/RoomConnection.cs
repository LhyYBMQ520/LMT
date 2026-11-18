using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text;
using System.IO;
using System.Threading.Channels;

namespace desktop.Services;

public record RoomInfo(string RoomId,string HostToken,string GuestToken,string GuestUrl,DateTimeOffset ExpiresAt);
public sealed class RoomConnection : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ClientWebSocket socket=new();
    private readonly CancellationTokenSource cancel=new();
    private readonly Channel<string> outgoing=Channel.CreateBounded<string>(new BoundedChannelOptions(64){FullMode=BoundedChannelFullMode.Wait,SingleReader=true});
    private Task? sendTask,receiveTask,connectTask,disposeTask;
    private readonly object lifecycle=new();
    private readonly TaskCompletionSource authenticated=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public RoomInfo? Room {get;private set;}
    public event Func<JsonElement,Task>? Message;
    public event Action<string>? Closed;
    public bool Connected=>socket.State==WebSocketState.Open;
    public Task ConnectAsync(string serverURL,string creationKey)
    {
        lock(lifecycle){
            ObjectDisposedException.ThrowIf(disposeTask!=null,this);
            if(connectTask!=null)throw new InvalidOperationException("房间连接已经启动。");
            return connectTask=ConnectCoreAsync(serverURL,creationKey);
        }
    }
    private async Task ConnectCoreAsync(string serverURL,string creationKey)
    {
        var uri=new Uri(serverURL.TrimEnd('/'));
        if((uri.Scheme!="https"&&uri.Scheme!="http")||!string.IsNullOrEmpty(uri.UserInfo)||uri.AbsolutePath!="/"||!string.IsNullOrEmpty(uri.Query)||!string.IsNullOrEmpty(uri.Fragment))throw new ArgumentException("请填写服务器源站地址，例如 https://listen.example.com");
        if(uri.Scheme!="https"&&!uri.IsLoopback)throw new ArgumentException("远程服务器必须使用 HTTPS。");
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(15)};
        using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(uri,"/api/rooms"));
        if(!string.IsNullOrWhiteSpace(creationKey))request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",creationKey);
        using var response=await http.SendAsync(request,cancel.Token);
        if(!response.IsSuccessStatusCode){var text=await response.Content.ReadAsStringAsync(cancel.Token);throw new InvalidOperationException($"创建房间失败 ({(int)response.StatusCode})：{text}");}
        Room=await response.Content.ReadFromJsonAsync<RoomInfo>(Json,cancel.Token)??throw new InvalidOperationException("服务器返回了空房间。");
        socket.Options.KeepAliveInterval=TimeSpan.FromSeconds(15);socket.Options.KeepAliveTimeout=TimeSpan.FromSeconds(15);
        var ws=new UriBuilder(uri){Scheme=uri.Scheme=="https"?"wss":"ws",Path=$"/api/rooms/{Room.RoomId}/ws"};
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);timeout.CancelAfter(TimeSpan.FromSeconds(12));
        await socket.ConnectAsync(ws.Uri,timeout.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{type="auth",role="host",token=Room.HostToken},Json)),WebSocketMessageType.Text,true,timeout.Token);
        sendTask=SendLoopAsync();receiveTask=ReceiveLoopAsync();
        await authenticated.Task.WaitAsync(timeout.Token);
    }
    public void Send(string type,object payload,string? peerId=null)
    {
        if(!outgoing.Writer.TryWrite(JsonSerializer.Serialize(new{type,payload,peerId},Json)))throw new InvalidOperationException("信令队列已满，请重新创建房间。");
    }
    private async Task SendLoopAsync()
    {
        try{await foreach(string item in outgoing.Reader.ReadAllAsync(cancel.Token))await socket.SendAsync(Encoding.UTF8.GetBytes(item),WebSocketMessageType.Text,true,cancel.Token);}
        catch(OperationCanceledException){}catch(Exception ex){Closed?.Invoke("信令发送失败："+ex.Message);cancel.Cancel();socket.Abort();}
    }
    private async Task ReceiveLoopAsync()
    {
        byte[] buffer=new byte[16*1024];
        try{while(!cancel.IsCancellationRequested){using var message=new MemoryStream();WebSocketReceiveResult result;
            do{result=await socket.ReceiveAsync(buffer,cancel.Token);if(result.MessageType==WebSocketMessageType.Close){var reason=socket.CloseStatusDescription??"服务器已关闭房间";authenticated.TrySetException(new InvalidOperationException(reason));Closed?.Invoke(reason);return;}if(message.Length+result.Count>800*1024)throw new InvalidOperationException("信令消息过大");message.Write(buffer,0,result.Count);}while(!result.EndOfMessage);
            using var doc=JsonDocument.Parse(message.ToArray());if(Message!=null)await Message(doc.RootElement.Clone());if(doc.RootElement.GetProperty("type").GetString()=="ready")authenticated.TrySetResult();
        }}catch(OperationCanceledException){authenticated.TrySetCanceled();}catch(Exception ex){authenticated.TrySetException(ex);Closed?.Invoke("房间连接已断开："+ex.Message);}finally{cancel.Cancel();socket.Abort();}
    }
    public ValueTask DisposeAsync()
    {
        lock(lifecycle)return new(disposeTask??=DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        outgoing.Writer.TryComplete();cancel.Cancel();socket.Abort();
        if(connectTask!=null)try{await connectTask.ConfigureAwait(false);}catch{}
        if(sendTask!=null)try{await sendTask.ConfigureAwait(false);}catch{}if(receiveTask!=null)try{await receiveTask.ConfigureAwait(false);}catch{}
        socket.Dispose();cancel.Dispose();
    }
}
