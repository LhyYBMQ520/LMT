// Chromium treats MediaStream players as one-shot audio and omits their system
// controls. A normal, looping media element supplies the controllable session;
// the music itself continues through the original WebRTC element without delay.
// Every PCM sample is zero: no tone, microphone capture or music recording.
export function createSilentWave(): ArrayBuffer {
  const sampleRate = 8000, sampleCount = sampleRate * 10, dataLength = sampleCount * 2;
  const buffer = new ArrayBuffer(44 + dataLength);
  const view = new DataView(buffer);
  const text = (offset: number, value: string) => {
    for (let i = 0; i < value.length; i++) view.setUint8(offset + i, value.charCodeAt(i));
  };
  text(0, 'RIFF'); view.setUint32(4, 36 + dataLength, true); text(8, 'WAVE');
  text(12, 'fmt '); view.setUint32(16, 16, true); view.setUint16(20, 1, true);
  view.setUint16(22, 1, true); view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * 2, true); view.setUint16(32, 2, true); view.setUint16(34, 16, true);
  text(36, 'data'); view.setUint32(40, dataLength, true);
  return buffer;
}

export function createMediaSessionAnchor(onSystemPause: () => void, element = new Audio(), urls = URL) {
  const source = urls.createObjectURL(new Blob([createSilentWave()], { type: 'audio/wav' }));
  let disposed = false, shouldPlay = false, revision = 0;
  element.loop = true; element.preload = 'auto'; element.disableRemotePlayback = true;
  // Muting the element (or setting its volume to zero) can suppress audio focus.
  // Silence comes from the PCM samples, independent of the listener's volume.
  element.src = source;
  const onPause = () => { if (!disposed && shouldPlay && element.paused) { shouldPlay = false; onSystemPause(); } };
  element.addEventListener('pause', onPause);
  return {
    play(): Promise<void> {
      if (disposed) return Promise.resolve();
      const current = ++revision;
      shouldPlay = true;
      return element.play().catch(error => { if (current === revision) shouldPlay = false; throw error; });
    },
    pause() { ++revision; shouldPlay = false; element.pause(); },
    dispose() {
      if (disposed) return;
      disposed = true; ++revision; shouldPlay = false;
      element.removeEventListener('pause', onPause);
      element.pause(); element.removeAttribute('src'); element.load(); urls.revokeObjectURL(source);
    },
  };
}
