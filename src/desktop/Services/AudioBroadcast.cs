using Concentus;
using Concentus.Enums;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Text.Json;
using System.Threading.Channels;

namespace desktop.Services;

public sealed class AudioBroadcast : IAsyncDisposable
{
    private readonly RoomConnection room;
    private readonly object gate=new();
    private readonly SemaphoreSlim negotiation=new(1,1);
    private readonly CancellationTokenSource cancel=new();
    private readonly Channel<short[]> frames=Channel.CreateBounded<short[]>(new BoundedChannelOptions(5){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=false});
    private readonly short[] assembling=new short[1920];
    private int used;
    private volatile bool capturing;
    private RTCPeerConnection? peer;
    private string? guestId;
    private List<RTCIceServer> iceServers=[];
    private readonly List<RTCIceCandidateInit> pendingIce=[];
    private bool remoteSet;
    private readonly Task audioTask;
    public event Action<string>? Status;
    public event Action<string>? Failed;
    public event Action<double>? Level;
    public AudioBroadcast(RoomConnection room){this.room=room;room.Message+=HandleMessageAsync;audioTask=Task.Run(AudioLoopAsync);}
    public void SetCapturing(bool active,string error="")
    {
        lock(gate){capturing=active;used=0;while(frames.Reader.TryRead(out _)){};}
        if(room.Connected)room.Send("capture_state",new{active,error});
    }
    public void Push(short[] samples)
    {
        lock(gate){if(!capturing)return;
            int offset=0;double peak=0;
            while(offset<samples.Length){int count=Math.Min(assembling.Length-used,samples.Length-offset);Array.Copy(samples,offset,assembling,used,count);for(int i=offset;i<offset+count;i++)peak=Math.Max(peak,Math.Abs((double)samples[i])/32768);used+=count;offset+=count;if(used==assembling.Length){frames.Writer.TryWrite((short[])assembling.Clone());used=0;}}
            Level?.Invoke(peak);
        }
    }
    private async Task AudioLoopAsync()
    {
        try{
            OpusCodecFactory.AttemptToUseNativeLibrary=false;
            using var encoder=OpusCodecFactory.CreateEncoder(48000,2,OpusApplication.OPUS_APPLICATION_AUDIO);
            encoder.Bitrate=128000;encoder.Complexity=5;encoder.UseInbandFEC=true;encoder.PacketLossPercent=5;encoder.UseDTX=false;
            var silence=new short[1920];var packet=new byte[4000];using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            while(await timer.WaitForNextTickAsync(cancel.Token)){
                short[] pcm=frames.Reader.TryRead(out var frame)?frame:silence;
                lock(gate){if(!capturing||peer?.connectionState!=RTCPeerConnectionState.connected)continue;int length=encoder.Encode(pcm.AsSpan(),960,packet.AsSpan(),packet.Length);peer.SendAudio(960,packet.AsSpan(0,length).ToArray());}
            }
        }catch(OperationCanceledException){}catch(Exception ex){Failed?.Invoke("Opus 音频传输失败："+ex.Message);}
    }
    private async Task HandleMessageAsync(JsonElement message)
    {
        await negotiation.WaitAsync(cancel.Token);
        try{
            string type=message.GetProperty("type").GetString()??"";
            var payload=message.TryGetProperty("payload",out var value)?value:default;
            string? id=message.TryGetProperty("peerId",out var p)?p.GetString():null;
            switch(type){
                case "ready":
                    iceServers=[];
                    foreach(var server in payload.GetProperty("iceServers").EnumerateArray())foreach(var url in server.GetProperty("urls").EnumerateArray())iceServers.Add(new RTCIceServer{urls=url.GetString(),username=server.TryGetProperty("username",out var u)?u.GetString():null,credential=server.TryGetProperty("credential",out var c)?c.GetString():null});
                    Status?.Invoke("房间已创建，等待访客");break;
                case "guest_joined":
                    ClosePeer();guestId=id;remoteSet=false;pendingIce.Clear();
                    var pc=new RTCPeerConnection(new RTCConfiguration{iceServers=iceServers});
                    lock(gate)peer=pc;
                    pc.addTrack(new MediaStreamTrack(new AudioFormat(111,"opus",48000,2,"minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1"),MediaStreamStatusEnum.SendOnly));
                    pc.onicecandidate+=candidate=>{if(candidate!=null&&room.Connected&&guestId==id)try{using var json=JsonDocument.Parse(candidate.toJSON());room.Send("ice",json.RootElement.Clone(),id);}catch(Exception ex){Failed?.Invoke(ex.Message);}};
                    pc.onconnectionstatechange+=state=>{if(guestId==id)Status?.Invoke(state switch{RTCPeerConnectionState.connected=>"访客已连接 · Opus 48 kHz",RTCPeerConnectionState.failed=>"WebRTC 连接失败，请检查网络或配置 TURN",RTCPeerConnectionState.disconnected=>"访客音频连接中断，可在网页重新连接",_=>"WebRTC："+state});};
                    var offer=pc.createOffer(null);await pc.setLocalDescription(offer);room.Send("offer",new{type="offer",sdp=offer.sdp},id);Status?.Invoke("访客已加入，正在建立音频连接");break;
                case "answer":
                    if(id!=guestId||peer==null)break;
                    var result=peer.setRemoteDescription(new RTCSessionDescriptionInit{type=RTCSdpType.answer,sdp=payload.GetProperty("sdp").GetString()});
                    if(result!=SetDescriptionResultEnum.OK)throw new InvalidOperationException("浏览器音频协商失败："+result);
                    remoteSet=true;foreach(var candidate in pendingIce)peer.addIceCandidate(candidate);pendingIce.Clear();break;
                case "ice":
                    if(id!=guestId||peer==null)break;
                    var ice=new RTCIceCandidateInit{candidate=payload.GetProperty("candidate").GetString(),sdpMid=payload.TryGetProperty("sdpMid",out var mid)?mid.GetString():"0",sdpMLineIndex=payload.TryGetProperty("sdpMLineIndex",out var index)&&index.ValueKind==JsonValueKind.Number?index.GetUInt16():(ushort)0};
                    if(remoteSet)peer.addIceCandidate(ice);else if(pendingIce.Count<256)pendingIce.Add(ice);break;
                case "guest_left":if(id==guestId){ClosePeer();Status?.Invoke("访客已离开，等待下一位访客");}break;
            }
        }catch(Exception ex){Failed?.Invoke("WebRTC 信令失败："+ex.Message);}finally{negotiation.Release();}
    }
    private void ClosePeer(){lock(gate){guestId=null;peer?.Close("guest disconnected");peer?.Dispose();peer=null;remoteSet=false;pendingIce.Clear();}}
    public async ValueTask DisposeAsync(){room.Message-=HandleMessageAsync;cancel.Cancel();try{await audioTask;}catch{}await negotiation.WaitAsync();try{ClosePeer();}finally{negotiation.Release();}cancel.Dispose();negotiation.Dispose();}
}
