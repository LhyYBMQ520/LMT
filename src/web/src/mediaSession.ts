export type MediaSong = { title: string; artist: string; album: string; cover: string };
type Actions = { play: () => void; pause: () => void; stop: () => void };
type AudioSession = { type: string; state?: string };
export type AudioNavigator = Navigator & { audioSession?: AudioSession };

// This declares the audio's purpose; it does not grant background execution privileges.
export function requestPlaybackSession(platform: AudioNavigator = navigator): void {
  try { if (platform.audioSession) platform.audioSession.type = 'playback'; } catch { /* Optional API. */ }
}

export function bindMediaSession(actions: Actions, platform: AudioNavigator = navigator,
  Metadata: typeof MediaMetadata | undefined = globalThis.MediaMetadata,
  fallbackArtwork = new URL('/media-cover.png', location.origin).href) {
  const session = platform.mediaSession;
  if (!session) return { update: (_song: MediaSong, _playing: boolean) => {}, dispose: () => {} };
  const supported = new Set<MediaSessionAction>();
  const handlers: Array<[MediaSessionAction, MediaSessionActionHandler | null]> = [
    ['play', () => actions.play()], ['pause', () => actions.pause()], ['stop', () => actions.stop()],
    // A live listener must not seek or change the host's song.
    ['seekbackward', null], ['seekforward', null], ['seekto', null], ['previoustrack', null], ['nexttrack', null],
  ];
  for (const [action, handler] of handlers) {
    try { session.setActionHandler(action, handler); supported.add(action); } catch { /* Some browsers expose only a subset. */ }
  }
  let metadataKey = '';
  return {
    update(song: MediaSong, playing: boolean) {
      const key = JSON.stringify(song);
      if (Metadata && key !== metadataKey) {
        const match = /^data:image\/(jpeg|png|webp);base64,/.exec(song.cover);
        const artwork: MediaImage[] = match
          ? [{ src: song.cover, type: `image/${match[1]}` }]
          : [{ src: fallbackArtwork, sizes: '512x512', type: 'image/png' }];
        try {
          session.metadata = new Metadata({ title: song.title || '未知歌曲', artist: song.artist || '未知艺术家', album: song.album || 'LMT 实时收听', artwork });
          metadataKey = key;
        } catch { /* Invalid artwork or partial implementation must not stop audio. */ }
      }
      try { session.playbackState = playing ? 'playing' : 'paused'; } catch { /* Optional API. */ }
    },
    dispose() {
      for (const action of supported) try { session.setActionHandler(action, null); } catch { /* Optional API. */ }
      try { session.playbackState = 'none'; session.metadata = null; } catch { /* Optional API. */ }
    },
  };
}
