import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createMediaSessionAnchor, createSilentWave } from '../src/mediaSessionAnchor.ts';

test('media session WAV has ten seconds of valid 16-bit PCM and every audio sample is exactly zero', () => {
  const buffer = createSilentWave(), view = new DataView(buffer);
  const text = (start: number, end: number) => new TextDecoder().decode(buffer.slice(start, end));
  assert.equal(text(0, 4), 'RIFF'); assert.equal(text(8, 12), 'WAVE');
  assert.equal(text(12, 16), 'fmt '); assert.equal(text(36, 40), 'data');
  assert.equal(view.getUint32(4, true), buffer.byteLength - 8);
  assert.equal(view.getUint16(20, true), 1); assert.equal(view.getUint16(22, true), 1);
  assert.equal(view.getUint16(34, true), 16); assert.equal(view.getUint16(32, true), 2);
  assert.equal(view.getUint32(28, true), view.getUint32(24, true) * 2);
  assert.equal(view.getUint32(40, true), buffer.byteLength - 44);
  assert.equal((buffer.byteLength - 44) / view.getUint32(28, true), 10);
  assert.ok(new Uint8Array(buffer, 44).every(byte => byte === 0));
});

class AudioElement extends EventTarget {
  paused = true; loop = false; preload = ''; disableRemotePlayback = false; src = '';
  playResult: Promise<void> = Promise.resolve();
  loadCount = 0;
  play() { this.paused = false; return this.playResult; }
  pause() { this.paused = true; queueMicrotask(() => this.dispatchEvent(new Event('pause'))); }
  removeAttribute(name: string) { assert.equal(name, 'src'); this.src = ''; }
  load() { this.loadCount++; }
}
function fixture() {
  const element = new AudioElement();
  const revoked: string[] = [];
  let blob: Blob | undefined, pausedBySystem = 0;
  const urls = { createObjectURL(value: Blob) { blob = value; return 'blob:lmt-test'; }, revokeObjectURL(url: string) { revoked.push(url); } };
  const anchor = createMediaSessionAnchor(() => pausedBySystem++, element as unknown as HTMLAudioElement, urls as typeof URL);
  return { element, revoked, anchor, get blob() { return blob; }, get pausedBySystem() { return pausedBySystem; } };
}

test('starts a looping normal media source, respects local pause and releases its blob once', async () => {
  const f = fixture();
  assert.equal(f.blob?.type, 'audio/wav'); assert.equal(f.element.src, 'blob:lmt-test');
  assert.ok(f.element.loop); assert.ok(f.element.disableRemotePlayback);
  await f.anchor.play(); assert.equal(f.element.paused, false);
  f.anchor.pause(); await Promise.resolve(); assert.equal(f.pausedBySystem, 0);
  await f.anchor.play();
  f.anchor.dispose(); f.anchor.dispose(); await Promise.resolve();
  assert.equal(f.element.paused, true); assert.equal(f.element.src, '');
  assert.equal(f.element.loadCount, 1); assert.deepEqual(f.revoked, ['blob:lmt-test']);
  await f.anchor.play(); assert.equal(f.element.paused, true); assert.equal(f.pausedBySystem, 0);
});

test('propagates a browser/system pause once without treating programmatic pauses as user intent', async () => {
  const f = fixture(); await f.anchor.play();
  f.element.pause(); await Promise.resolve(); assert.equal(f.pausedBySystem, 1);
  f.element.pause(); await Promise.resolve(); assert.equal(f.pausedBySystem, 1);
  await f.anchor.play(); f.anchor.pause(); await Promise.resolve(); assert.equal(f.pausedBySystem, 1);
  f.anchor.dispose();
});

test('autoplay rejection is observable and a later play can retry', async () => {
  const f = fixture();
  f.element.playResult = Promise.reject(new Error('autoplay denied'));
  await assert.rejects(f.anchor.play(), /autoplay denied/);
  f.element.pause(); await Promise.resolve(); assert.equal(f.pausedBySystem, 0);
  f.element.playResult = Promise.resolve(); await f.anchor.play();
  f.element.pause(); await Promise.resolve(); assert.equal(f.pausedBySystem, 1);
  f.anchor.dispose();
});
