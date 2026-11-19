import { test } from 'node:test';
import assert from 'node:assert/strict';
import { bindMediaSession, requestPlaybackSession } from '../src/mediaSession.ts';
import type { AudioNavigator } from '../src/mediaSession.ts';

const fallback = 'https://listen.example.com/media-cover.png';
const song = { title: '歌曲', artist: '艺术家', album: '专辑', cover: '' };
const noop = () => {};
class Metadata {
  constructor(init: MediaMetadataInit) { Object.assign(this, init); }
}
function fixture() {
  const handlers = new Map<MediaSessionAction, MediaSessionActionHandler | null>();
  const session = { metadata: null as MediaMetadata | null, playbackState: 'none',
    setActionHandler(action: MediaSessionAction, handler: MediaSessionActionHandler | null) { handlers.set(action, handler); } };
  return { session, handlers, platform: { mediaSession: session, audioSession: { type: 'auto' } } as unknown as AudioNavigator };
}

test('unsupported media and audio session APIs leave playback usable', () => {
  const platform = {} as AudioNavigator;
  requestPlaybackSession(platform);
  const binding = bindMediaSession({ play: noop, pause: noop, stop: noop }, platform, undefined, fallback);
  binding.update(song, true); binding.dispose();
  const throwing = { get audioSession() { throw new Error('unavailable'); } } as AudioNavigator;
  assert.doesNotThrow(() => requestPlaybackSession(throwing));
});

test('declares playback purpose, publishes song and fallback art, updates playback state and cleans up', () => {
  const { platform, session, handlers } = fixture();
  requestPlaybackSession(platform); assert.equal(platform.audioSession?.type, 'playback');
  const called: string[] = [];
  const binding = bindMediaSession({ play: () => called.push('play'), pause: () => called.push('pause'), stop: () => called.push('stop') }, platform, Metadata as typeof MediaMetadata, fallback);
  binding.update(song, true);
  assert.equal(session.playbackState, 'playing');
  assert.deepEqual(session.metadata, new Metadata({ title: song.title, artist: song.artist, album: song.album,
    artwork: [{ src: fallback, sizes: '512x512', type: 'image/png' }] }));
  const first = session.metadata;
  binding.update(song, false); assert.equal(session.metadata, first); assert.equal(session.playbackState, 'paused');
  for (const action of ['play', 'pause', 'stop'] as const) handlers.get(action)?.({ action });
  assert.deepEqual(called, ['play', 'pause', 'stop']);
  for (const action of ['seekbackward', 'seekforward', 'seekto', 'nexttrack', 'previoustrack'] as const) assert.equal(handlers.get(action), null);
  binding.update({ ...song, title: '下一首', cover: 'data:image/png;base64,AAAA' }, true);
  assert.equal(session.metadata?.title, '下一首');
  assert.deepEqual(session.metadata?.artwork, [{ src: 'data:image/png;base64,AAAA', type: 'image/png' }]);
  binding.dispose(); assert.equal(session.metadata, null); assert.equal(session.playbackState, 'none');
  assert.ok([...handlers.values()].every(handler => handler === null));
});

test('partial media session implementations and invalid metadata cannot break audio', () => {
  const { platform, session } = fixture();
  session.setActionHandler = () => { throw new Error('unsupported'); };
  class BrokenMetadata { constructor() { throw new Error('unsupported artwork'); } }
  const binding = bindMediaSession({ play: noop, pause: noop, stop: noop }, platform, BrokenMetadata as unknown as typeof MediaMetadata, fallback);
  assert.doesNotThrow(() => binding.update(song, true));
  assert.equal(session.playbackState, 'playing');
  assert.doesNotThrow(() => binding.dispose());
});
