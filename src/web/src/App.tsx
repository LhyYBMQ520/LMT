import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { describeIcePath } from './icePath';
import type { IcePath } from './icePath';

type Song = { title: string; artist: string; album: string; playbackStatus: string; cover: string };
type Phase = 'idle' | 'connecting' | 'connected' | 'closed' | 'error';
type Message = { type: string; payload?: unknown; peerId?: string };
const unknown: Song = { title: '未知歌曲', artist: '未知艺术家', album: '等待房主分享歌曲信息', playbackStatus: 'unknown', cover: '' };
const playback: Record<string, string> = { playing: '正在播放', paused: '房主已暂停', stopped: '房主已停止', unknown: '等待歌曲信息' };
function Icon({ name, size = 20 }: { name: string; size?: number }) {
  const paths: Record<string, ReactNode> = {
    play: <path d="m9 5 11 7-11 7Z" fill="currentColor" stroke="none" />,
    pause: <><path d="M8 5v14M16 5v14" strokeWidth="4" /></>,
    volume: <><path d="m11 5-6 4H2v6h3l6 4Z" /><path d="M15 8a6 6 0 0 1 0 8m3-11a10 10 0 0 1 0 14" /></>,
    refresh: <><path d="M20 7v5h-5M4 17v-5h5" /><path d="M6.1 7a7 7 0 0 1 11.6-1L20 9M4 15l2.3 3A7 7 0 0 0 18 17" /></>,
    arrow: <path d="M5 12h14m-6-6 6 6-6 6" />,
    headphones: <><path d="M4 14v-3a8 8 0 0 1 16 0v3" /><rect x="3" y="12" width="4" height="8" rx="2" /><rect x="17" y="12" width="4" height="8" rx="2" /></>,
    shield: <><path d="m12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6Z" /><path d="m8 12 3 3 5-6" /></>,
    music: <><path d="M10 17V5l10-2v12M10 8l10-2" /><ellipse cx="6.5" cy="18" rx="3.5" ry="3" /><ellipse cx="16.5" cy="16" rx="3.5" ry="3" /></>,
  };
  return <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{paths[name]}</svg>;
}
function Brand() { return <a className="brand" href="/" aria-label="LMT 首页"><img className="brand-icon" src="/favicon.svg" alt="" width="36" height="36" /><span>LMT<span className="brand-sub">LISTEN MUSIC TOGETHER</span></span></a>; }

export default function App() {
  const roomId = /^\/r\/([A-Za-z0-9_-]{24})\/?$/.exec(location.pathname)?.[1] ?? '';
  const token = new URLSearchParams(location.hash.slice(1)).get('token') ?? '';
  const isRoom = location.pathname.startsWith('/r/');
  const validInvite = !!roomId && /^[A-Za-z0-9_-]{43}$/.test(token);
  const [phase, setPhase] = useState<Phase>('idle');
  const [song, setSong] = useState<Song>(unknown);
  const [error, setError] = useState('');
  const [audioError, setAudioError] = useState('');
  const [captureError, setCaptureError] = useState('');
  const [active, setActive] = useState(false);
  const [playing, setPlaying] = useState(false);
  const [volume, setVolume] = useState(80);
  const [failedCover, setFailedCover] = useState('');
  const [expiry, setExpiry] = useState('');
  const [network, setNetwork] = useState('等待连接');
  const [icePath, setIcePath] = useState<IcePath>({ summary: '尚未连接', detail: '连接后显示实际选中的 ICE 路径。' });
  const audio = useRef<HTMLAudioElement>(null);
  const socket = useRef<WebSocket | null>(null);
  const peer = useRef<RTCPeerConnection | null>(null);
  const generation = useRef(0);
  const timers = useRef<ReturnType<typeof setTimeout>[]>([]);
  const stop = () => {
    generation.current++;
    timers.current.forEach(clearTimeout); timers.current = [];
    const ws = socket.current; socket.current = null; if (ws) { ws.onclose = null; ws.close(); }
    const pc = peer.current; peer.current = null; pc?.close();
    if (audio.current) { audio.current.pause(); audio.current.srcObject = null; }
    setPlaying(false); setActive(false); setCaptureError('');
    setIcePath({ summary: '尚未连接', detail: '连接后显示实际选中的 ICE 路径。' });
  };
  useEffect(() => () => { stop(); }, []);
  useEffect(() => { if (audio.current) { audio.current.volume = volume / 100; audio.current.disableRemotePlayback = true; } }, [volume]);
  useEffect(() => {
    if (phase !== 'connected' && phase !== 'connecting') return;
    let disposed = false;
    let pending = false;
    const update = () => {
      const pc = peer.current; if (!pc) return;
      if (pending) return;
      pending = true;
      void pc.getStats().then(stats => {
        if (disposed || pc !== peer.current) return;
        if ((pc.iceConnectionState === 'connected' || pc.iceConnectionState === 'completed') && pc.connectionState === 'connected') {
          setIcePath(describeIcePath(stats));
          stats.forEach(report => { if (report.type === 'inbound-rtp' && report.kind === 'audio') setNetwork(`抖动 ${Math.round((report.jitter ?? 0) * 1000)} ms`); });
        }
      }).catch(() => { if (!disposed && pc === peer.current && pc.connectionState === 'connected') setIcePath({ summary: 'ICE 路径信息暂不可用', detail: '浏览器未能提供连接统计，音频仍可正常传输。' }); })
        .finally(() => { pending = false; });
    };
    update();
    const interval = setInterval(update, 2500);
    return () => { disposed = true; clearInterval(interval); };
  }, [phase]);

  const start = () => {
    if (!validInvite) { setError('邀请链接不完整，请向房主索取包含访问令牌的完整链接。'); return; }
    if (!window.RTCPeerConnection || !window.isSecureContext) { setError('此浏览器无法建立安全音频连接。请使用 HTTPS，并更新浏览器。'); return; }
    stop(); const gen = generation.current;
    setError(''); setAudioError(''); setSong(unknown); setFailedCover(''); setExpiry(''); setNetwork('正在连接'); setPhase('connecting');
    // The user gesture initiates playback. ontrack retries; blocked browsers get an explicit button.
    if (audio.current) { audio.current.srcObject = new MediaStream(); void audio.current.play().catch(() => {}); }
    const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/api/rooms/${roomId}/ws`);
    socket.current = ws;
    const live = () => generation.current === gen && socket.current === ws;
    const send = (type: string, payload: unknown) => { if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type, payload })); };
    const connectionTimer = setTimeout(() => { if (live() && ws.readyState !== WebSocket.OPEN) { stop(); setPhase('error'); setError('服务器连接超时，请检查网络后重新连接。'); } }, 12000);
    timers.current.push(connectionTimer);
    let pendingIce: RTCIceCandidateInit[] = [];
    let chain = Promise.resolve();
    ws.onopen = () => { if (live()) ws.send(JSON.stringify({ type: 'auth', role: 'guest', token })); };
    ws.onmessage = event => {
      chain = chain.then(async () => {
        if (!live()) return;
        const message = JSON.parse(event.data as string) as Message;
        switch (message.type) {
          case 'ready': {
            const data = message.payload as { iceServers: RTCIceServer[]; expiresAt: string };
            setExpiry(new Date(data.expiresAt).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' }));
            const pc = new RTCPeerConnection({ iceServers: data.iceServers }); peer.current = pc;
            const updateIceState = () => {
              if (!live() || peer.current !== pc) return;
              const state = pc.iceConnectionState;
              const labels: Record<string, string> = { new: '等待 ICE 连接', checking: 'ICE 检查中', disconnected: 'ICE 连接中断', failed: 'ICE 连接失败', closed: 'ICE 已关闭' };
              const summary = labels[state];
              if (summary) setIcePath({ summary, detail: '当前没有可确认的活动 ICE 路径。' });
            };
            pc.oniceconnectionstatechange = updateIceState;
            setIcePath({ summary: 'ICE 检查中', detail: '正在收集候选并检查可用路径。' });
            pc.onicecandidate = event => { if (live() && event.candidate) send('ice', event.candidate.toJSON()); };
            pc.ontrack = event => { if (!live() || !audio.current) return; audio.current.srcObject = event.streams[0] ?? new MediaStream([event.track]); void audio.current.play().then(() => { if(live())setAudioError(''); }).catch(() => { if(live())setAudioError('浏览器已暂停自动播放，请点击「播放声音」。'); }); };
            pc.onconnectionstatechange = () => {
              if (!live()) return;
              if (pc.connectionState === 'connected') { setPhase('connected'); setNetwork('连接正常'); setError(''); }
              else if (pc.connectionState === 'failed') { setPhase('error'); setIcePath({ summary: 'WebRTC 连接失败', detail: 'ICE 或 DTLS 连接失败，当前没有可用的音频路径。' }); setError('音频连接失败。请重连；跨网络仍失败时，房主需要检查 TURN 配置。'); audio.current?.pause(); }
              else if (pc.connectionState === 'disconnected') { setNetwork('连接中断'); setIcePath({ summary: 'WebRTC 连接中断', detail: '等待网络恢复后重新确认选中的 ICE 路径。' }); setError('音频连接中断，正在等待网络恢复，也可以点击重新连接。'); }
            };
            timers.current.push(setTimeout(() => { if (live() && pc.connectionState !== 'connected') { setPhase('error'); setError('音频连接超时。请重新连接，或请房主检查 TURN 配置。'); } }, 30000));
            break;
          }
          case 'offer': {
            const pc = peer.current; if (!pc) throw new Error('尚未准备音频连接');
            await pc.setRemoteDescription(message.payload as RTCSessionDescriptionInit); if (!live()) return;
            for (const candidate of pendingIce) await pc.addIceCandidate(candidate); pendingIce = [];
            const answer = await pc.createAnswer(); if (!live()) return; await pc.setLocalDescription(answer); send('answer', { type: answer.type, sdp: answer.sdp }); break;
          }
          case 'ice': { const candidate = message.payload as RTCIceCandidateInit; if (peer.current?.remoteDescription) await peer.current.addIceCandidate(candidate); else if (pendingIce.length < 256) pendingIce.push(candidate); break; }
          case 'metadata': { const data = message.payload as Song; setSong({ title: data.title || unknown.title, artist: data.artist || unknown.artist, album: data.album || '未知专辑', cover: /^data:image\/(jpeg|png|webp);base64,/.test(data.cover) ? data.cover : '', playbackStatus: data.playbackStatus in playback ? data.playbackStatus : 'unknown' }); break; }
          case 'capture_state': { const data = message.payload as { active: boolean; error: string }; setActive(data.active); setCaptureError(data.error || (data.active ? '' : '房主尚未开始捕获，或已停止分享音频。')); break; }
        }
      }).catch((e: unknown) => { if(live()){setPhase('error');setError(`音频连接失败：${e instanceof Error ? e.message : '无法处理信令'}`);} });
    };
    ws.onerror = () => { if (live()) setError('无法连接房间，请检查链接、服务器和网络。'); };
    ws.onclose = event => { if (!live()) return; stop(); setPhase(event.code === 4000 ? 'closed' : 'error'); setNetwork('已断开'); setError(event.reason || '房间连接已断开。房主离线或房间过期后，原链接会失效。'); };
  };
  const togglePlayback = () => { const element = audio.current; if (!element) return; if (playing) element.pause(); else void element.play().then(() => setAudioError('')).catch(() => setAudioError('暂时没有可播放的音频，请等待房主开始分享或重新连接。')); };
  const phaseLabel = { idle: '等待你加入', connecting: '正在连接房间', connected: '已连接房间', closed: '房间已关闭', error: '连接不可用' }[phase];
  const listening = playing && active && phase === 'connected';
  const actionLabel = phase === 'connecting' ? '正在连接…' : phase === 'connected' ? playing ? '暂停收听' : '播放声音' : phase === 'closed' ? '房间已关闭' : '开始收听';

  return <div className="app-shell">
    <header><Brand /><span className="header-note">A LITTLE CLOSER, THROUGH MUSIC.</span><span className="version">一起听，此刻的音乐</span></header>
    <main>
      <div className="intro"><h1>同一首歌，同一个此刻<span>。</span></h1><p>{isRoom ? '你的音乐收听房间' : '等待一份音乐邀请'}</p></div>
      <section className="room-layout" aria-label="音乐收听房间">
        <div className="player-card">
          <div className="card-top"><span className="live-pill"><span className={listening ? 'dot live' : 'dot'} />{listening ? '正在一起听' : '正在分享'}</span><span className="stereo-label">STEREO / 48 kHz</span></div>
          <div className="now-playing">
            <div className="record-area"><div className="record-disc" aria-hidden="true"><div className="record-label"><span>LMT</span><i /></div></div><div className="album-cover">{song.cover && song.cover !== failedCover ? <img src={song.cover} alt={`${song.title} 的封面`} onError={() => setFailedCover(song.cover)} /> : <div className="cover-placeholder"><Icon name="music" size={40} /><span>LMT</span></div>}</div></div>
            <div className="song-info"><span className="small-label">NOW SHARING</span><h2 title={song.title}>{song.title}</h2><p className="artist" title={song.artist}>{song.artist}</p><p className="album" title={song.album}>{song.album}</p><span className="playback-status"><span className="dot" />{playback[song.playbackStatus]}</span></div>
          </div>
          <div className="local-player"><span className={`bars ${listening ? 'active' : ''}`} aria-hidden="true"><i /><i /><i /><i /><i /></span><span>{listening ? '实时音频 · 与房主一起听' : phase === 'connected' ? '音频已连接' : '等待音乐连接'}</span></div>
          <div className="volume-row"><Icon name="volume" size={17} /><label htmlFor="volume">音量</label><input id="volume" type="range" min="0" max="100" value={volume} onChange={e => setVolume(Number(e.target.value))} /><output htmlFor="volume">{volume}%</output></div>
          <audio ref={audio} hidden onPlay={() => setPlaying(true)} onPause={() => setPlaying(false)} onVolumeChange={() => { if(audio.current)setVolume(Math.round(audio.current.volume * 100)); }} onError={() => setAudioError('音频暂时不可用，请尝试重新连接。')} />
        </div>
        <aside className="room-sidebar"><div className="join-card"><div className="connection-heading"><h2>收听房间</h2><div className={`connection-status ${phase}`} role="status"><span className="dot" />{phaseLabel}</div></div>
          {!isRoom && <p className="empty-room">尚未收到邀请链接</p>}
          {isRoom && !validInvite && <div className="notice error" role="alert">邀请链接不完整。请向房主索取完整链接。</div>}
          {error && <div className="notice error" role="alert">{error}</div>}
          <div className="listening-actions"><button className="primary-button" onClick={phase === 'connected' ? togglePlayback : start} disabled={!validInvite || phase === 'connecting' || phase === 'closed'}><Icon name={phase === 'connected' ? playing ? 'pause' : 'play' : 'headphones'} /><span>{actionLabel}</span></button>
          {(phase === 'connected' || phase === 'error' || phase === 'connecting') && <button className="reconnect-button" onClick={start} aria-label="重新连接" title="重新连接"><Icon name="refresh" size={19} /></button>}</div>
          <div className="room-details"><div><span>房间</span><strong>{roomId ? `# ${roomId.slice(0, 8)}` : '等待邀请'}</strong></div><div><span>人数上限</span><strong>房主 + 1 位听众</strong></div><div><span>音频连接</span><strong>{network}</strong></div><div><span>有效期至</span><strong>{expiry || '—'}</strong></div></div>
          <details className="ice-path"><summary><span>ICE 路径</span><strong>{icePath.summary}</strong></summary><p>{icePath.detail}</p></details>
          {(audioError || captureError) && <div className="notice" role="status">{audioError || captureError}</div>}
        </div><p className="privacy-note"><Icon name="shield" size={16} /><span>实时收听 · 不保存音频和歌曲历史</span></p></aside>
      </section>
    </main>
    <footer><span>© {new Date().getFullYear()} LMT</span><span>从你的桌面，到对方的耳边。</span></footer>
  </div>;
}
