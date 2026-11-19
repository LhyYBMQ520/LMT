using System.Net;
using System.Net.Sockets;
using SIPSorcery.Net;

namespace desktop.Services;

public sealed record IceConnectionPath(string Summary, string Detail)
{
    public static IceConnectionPath Waiting(string text) => new(text, "等待 WebRTC 选定 ICE 候选对。STUN 只帮助发现地址，不转发音频。");

    public static IceConnectionPath Read(RTCPeerConnection peer)
    {
        if (peer.connectionState is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected)
            return Waiting("WebRTC " + (peer.connectionState == RTCPeerConnectionState.failed ? "连接失败" : peer.connectionState == RTCPeerConnectionState.closed ? "已关闭" : "连接中断"));
        string state = peer.iceConnectionState.ToString();
        if (state is not ("connected" or "completed")) return Waiting(state switch
        {
            "checking" => "ICE 检查中", "disconnected" => "ICE 连接中断", "failed" => "ICE 连接失败",
            "closed" => "ICE 已关闭", _ => "等待 ICE 连接"
        });
        var pair = peer.GetRtpChannel().NominatedEntry;
        if (pair == null || !pair.Nominated) return Waiting("尚未获得选中路径");
        return FromCandidates(pair.LocalCandidate, pair.RemoteCandidate);
    }

    internal static IceConnectionPath FromCandidates(RTCIceCandidate? local, RTCIceCandidate? remote)
    {
        bool localRelay = local?.type == RTCIceCandidateType.relay, remoteRelay = remote?.type == RTCIceCandidateType.relay;
        string route = (localRelay, remoteRelay) switch
        {
            (true, true) => "双端 TURN 中继", (true, false) => "本端 TURN 中继", (false, true) => "对端 TURN 中继",
            _ => KnownType(local) && KnownType(remote) ? "直连" : "路径类型未知"
        };
        string protocols = string.Join(" ↔ ", new[] { Protocol(local), Protocol(remote) }.Distinct());
        string families = string.Join(" ↔ ", new[] { Family(local), Family(remote) }.Distinct());
        return new($"{route}{(localRelay ? $"（本端 {RelayAccess(local)}）" : "")} · {protocols} · {families}",
            $"本端（桌面）：{Describe(local, true)}\n对端（网页）：{Describe(remote, false)}\n" +
            "候选类型由各端 ICE 实现报告，双方视角可能不同。对端的 TURN 接入协议不包含在 ICE 候选中。STUN 不转发音频。");
    }

    private static bool KnownType(RTCIceCandidate? candidate) => candidate?.type is RTCIceCandidateType.host or RTCIceCandidateType.srflx or RTCIceCandidateType.prflx or RTCIceCandidateType.relay;

    private static string RelayAccess(RTCIceCandidate? candidate)
    {
        var server = candidate?.IceServer;
        return server == null ? "未知" : server.Uri.Scheme.ToString() is "turns" or "stuns"
            ? server.Protocol == ProtocolType.Tcp ? "TLS/TCP" : "DTLS/UDP" : server.Protocol.ToString().ToUpperInvariant();
    }

    private static string Protocol(RTCIceCandidate? candidate) => candidate == null ? "协议未知" :
        candidate.protocol == RTCIceProtocol.tcp ? $"TCP/{candidate.tcpType}" : candidate.protocol.ToString().ToUpperInvariant();

    private static string Family(RTCIceCandidate? candidate)
    {
        string? address = candidate?.address;
        if (address?.EndsWith(".local", StringComparison.OrdinalIgnoreCase) == true) return "mDNS（地址隐藏）";
        if (IPAddress.TryParse(address, out var ip))
        {
            if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return "地址族未报告";
            if (ip.IsIPv4MappedToIPv6) return "IPv4";
            return ip.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";
        }
        return "地址族未知";
    }

    private static string Describe(RTCIceCandidate? candidate, bool local)
    {
        string type = candidate?.type switch
        {
            RTCIceCandidateType.host => "host（主机）", RTCIceCandidateType.srflx => "srflx（服务器反射）",
            RTCIceCandidateType.prflx => "prflx（对端反射）", RTCIceCandidateType.relay => "relay（TURN 中继）", _ => "候选类型未知"
        };
        string text = $"{type} · {Protocol(candidate)} · {Family(candidate)}";
        if (candidate?.type == RTCIceCandidateType.relay)
        {
            string access = local ? RelayAccess(candidate) : "未知（未提供）";
            text += $" · TURN 接入 {access}";
        }
        return text;
    }
}
