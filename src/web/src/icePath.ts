export type IcePath = { summary: string; detail: string };

type Candidate = {
  candidateType?: string; protocol?: string; address?: string; ip?: string;
  relayProtocol?: string; tcpType?: string;
};
type Pair = {
  id: string; state?: string; nominated?: boolean; selected?: boolean;
  localCandidateId?: string; remoteCandidateId?: string;
};
const candidateNames: Record<string, string> = {
  host: 'host（主机）', srflx: 'srflx（服务器反射）', prflx: 'prflx（对端反射）', relay: 'relay（TURN 中继）',
};
const knownType = (candidate?: Candidate) => !!candidate?.candidateType && Object.hasOwn(candidateNames, candidate.candidateType);
function family(candidate?: Candidate): string {
  const address = candidate?.address ?? candidate?.ip;
  if (!address) return '地址族未知';
  if (address === '0.0.0.0' || address === '::') return '地址族未报告';
  if (address.toLowerCase().endsWith('.local')) return 'mDNS（地址隐藏）';
  if (address.includes(':')) return 'IPv6';
  if (/^(\d{1,3}\.){3}\d{1,3}$/.test(address)) return 'IPv4';
  return '地址族未知';
}
function protocol(candidate?: Candidate): string {
  if (!candidate?.protocol) return '协议未知';
  const value = candidate.protocol.toUpperCase();
  return value === 'TCP' && candidate.tcpType ? `${value}/${candidate.tcpType}` : value;
}
function relayAccess(candidate?: Candidate): string {
  const transport = candidate?.relayProtocol?.toLowerCase();
  return transport === 'tls' ? 'TLS/TCP' : transport === 'dtls' ? 'DTLS/UDP' : transport?.toUpperCase() ?? '未知';
}
function describe(candidate?: Candidate): string {
  const type = knownType(candidate) ? candidateNames[candidate!.candidateType!] : '候选类型未知';
  let text = `${type} · ${protocol(candidate)} · ${family(candidate)}`;
  if (candidate?.candidateType === 'relay') {
    text += ` · TURN 接入 ${relayAccess(candidate)}${candidate.relayProtocol ? '' : '（浏览器未提供）'}`;
  }
  return text;
}

// Only an explicitly selected pair, or one unique nominated/succeeded pair, is evidence of the active route.
export function describeIcePath(stats: RTCStatsReport): IcePath {
  const reports: Array<Record<string, unknown>> = [];
  stats.forEach(report => reports.push(report));
  const audio = reports.find(r => r.type === 'inbound-rtp' && (r.kind === 'audio' || r.mediaType === 'audio'));
  const transports = reports.filter(r => r.type === 'transport');
  const transport = transports.find(r => r.id === audio?.transportId && r.selectedCandidatePairId)
    ?? transports.find(r => r.selectedCandidatePairId);
  let pair = transport ? stats.get(transport.selectedCandidatePairId as string) as Pair | undefined : undefined;
  if (pair?.state && pair.state !== 'succeeded') pair = undefined;
  if (!pair) {
    const pairs = reports.filter(r => r.type === 'candidate-pair') as unknown as Pair[];
    const selected = pairs.filter(p => p.selected && p.state === 'succeeded');
    const nominated = pairs.filter(p => p.nominated && p.state === 'succeeded');
    pair = selected.length === 1 ? selected[0] : nominated.length === 1 ? nominated[0] : undefined;
  }
  if (!pair) return { summary: '尚未获得选中路径', detail: '等待浏览器提供选中的 ICE 候选对；不会根据配置或仅成功的检查推测路径。' };
  const local = stats.get(pair.localCandidateId ?? '') as Candidate | undefined;
  const remote = stats.get(pair.remoteCandidateId ?? '') as Candidate | undefined;
  const localRelay = local?.candidateType === 'relay', remoteRelay = remote?.candidateType === 'relay';
  const route = localRelay && remoteRelay ? '双端 TURN 中继' : localRelay ? '本端 TURN 中继' : remoteRelay ? '对端 TURN 中继'
    : knownType(local) && knownType(remote) ? '直连' : '路径类型未知';
  const protocols = [...new Set([protocol(local), protocol(remote)])].join(' ↔ ');
  const families = [...new Set([family(local), family(remote)])].join(' ↔ ');
  return {
    summary: `${route}${localRelay ? `（本端 ${relayAccess(local)}）` : ''} · ${protocols} · ${families}`,
    detail: `本端（网页）：${describe(local)}\n对端（桌面）：${describe(remote)}\nTURN 接入协议仅在浏览器提供时显示；对端的接入协议通常不可见。STUN 只帮助发现地址，不转发音频。`,
  };
}
