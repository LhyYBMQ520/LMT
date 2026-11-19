import { test } from 'node:test';
import assert from 'node:assert/strict';
import { describeIcePath } from '../src/icePath.ts';

function report(local: Record<string, unknown> = {}, remote: Record<string, unknown> = {}, extra: Array<Record<string, unknown>> = []) {
  return new Map([
    ['local', { id: 'local', type: 'local-candidate', candidateType: 'host', protocol: 'udp', address: '192.0.2.1', ...local }],
    ['remote', { id: 'remote', type: 'remote-candidate', candidateType: 'host', protocol: 'udp', address: '192.0.2.2', ...remote }],
    ['pair', { id: 'pair', type: 'candidate-pair', state: 'succeeded', localCandidateId: 'local', remoteCandidateId: 'remote' }],
    ['transport', { id: 'transport', type: 'transport', selectedCandidatePairId: 'pair' }],
    ...extra.map(r => [r.id as string, r] as [string, Record<string, unknown>]),
  ]) as unknown as RTCStatsReport;
}

test('all 16 host/srflx/prflx/relay pair combinations use the selected pair, never the configured ICE list', () => {
  for (const local of ['host', 'srflx', 'prflx', 'relay']) for (const remote of ['host', 'srflx', 'prflx', 'relay']) {
    const result = describeIcePath(report({ candidateType: local }, { candidateType: remote }));
    const expected = local === 'relay' ? remote === 'relay' ? '双端 TURN 中继' : '本端 TURN 中继' : remote === 'relay' ? '对端 TURN 中继' : '直连';
    assert.ok(result.summary.startsWith(expected), `${local}/${remote}: ${result.summary}`);
    assert.ok(result.detail.includes(local) && result.detail.includes(remote));
  }
});

test('TURN TLS/TCP is distinct from the UDP relay candidate; no credentials, addresses or URLs are displayed', () => {
  const result = describeIcePath(report({ candidateType: 'relay', relayProtocol: 'tls', url: 'turns:example.test?secret=x' }, { candidateType: 'srflx' }));
  assert.match(result.summary, /TLS\/TCP/);
  assert.match(result.summary, /UDP/);
  assert.doesNotMatch(JSON.stringify(result), /192\.0\.2|example\.test|secret/);
  for (const transport of ['udp', 'tcp', 'tls', 'dtls']) assert.ok(describeIcePath(report({ candidateType: 'relay', relayProtocol: transport })).detail.includes(transport.toUpperCase()));
});

test('IPv6, mixed families, TCP types, hidden mDNS and omitted fields are explicit', () => {
  assert.match(describeIcePath(report({ address: '2001:db8::1' }, { address: '2001:db8::2' })).summary, /IPv6/);
  assert.match(describeIcePath(report({ address: '2001:db8::1' })).summary, /IPv6 ↔ IPv4/);
  assert.match(describeIcePath(report({ address: 'hidden.local', protocol: 'tcp', tcpType: 'passive' })).detail, /TCP\/passive · mDNS/);
  assert.match(describeIcePath(report({ address: undefined, protocol: undefined, candidateType: undefined })).summary, /路径类型未知.*协议未知.*地址族未知/);
  assert.match(describeIcePath(report({ candidateType: 'relay' })).detail, /TURN 接入 未知/);
});

test('unique nominated/selected fallback works; merely successful or ambiguous pairs are not claimed active', () => {
  const stats = report(); stats.delete('transport');
  assert.match(describeIcePath(stats).summary, /尚未获得/);
  stats.get('pair').nominated = true;
  assert.match(describeIcePath(stats).summary, /^直连/);
  stats.set('other', { id: 'other', type: 'candidate-pair', state: 'succeeded', nominated: true });
  assert.match(describeIcePath(stats).summary, /尚未获得/);
  stats.get('pair').selected = true;
  assert.match(describeIcePath(stats).summary, /^直连/);
  stats.get('pair').state = 'failed'; stats.delete('other');
  assert.match(describeIcePath(stats).summary, /尚未获得/);
  const stale = report(); stale.get('pair').state = 'failed';
  assert.match(describeIcePath(stale).summary, /尚未获得/);
});

test('selected audio transport wins over an unrelated transport and updates when pair changes', () => {
  const stats = report({}, { candidateType: 'relay' }, [
    { id: 'audio', type: 'inbound-rtp', kind: 'audio', transportId: 'audio-transport' },
    { id: 'audio-transport', type: 'transport', selectedCandidatePairId: 'audio-pair' },
    { id: 'audio-pair', type: 'candidate-pair', state: 'succeeded', localCandidateId: 'remote', remoteCandidateId: 'local' },
  ]);
  assert.match(describeIcePath(stats).summary, /^本端 TURN/);
  stats.get('audio-transport').selectedCandidatePairId = 'pair';
  assert.match(describeIcePath(stats).summary, /^对端 TURN/);
  stats.delete('local');
  assert.match(describeIcePath(stats).detail, /候选类型未知/);
});
