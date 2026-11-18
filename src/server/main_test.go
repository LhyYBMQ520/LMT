package main

import (
	"context"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/gorilla/websocket"
)

func TestMultipleListenersShareRoomsAndShutdown(t *testing.T) {
	listeners, err := listenAddresses("127.0.0.1:0, 127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	s := &server{rooms: map[string]*room{}, lastCreate: map[string]time.Time{}, publicURL: "https://listen.test", origin: "https://listen.test", ttl: time.Hour}
	h := &http.Server{Handler: s.handler(t.TempDir())}
	results := make(chan error, len(listeners))
	t.Cleanup(func() { _ = h.Close() })
	for _, listener := range listeners {
		go func(listener net.Listener) { results <- h.Serve(listener) }(listener)
	}
	response, err := http.Post("http://"+listeners[0].Addr().String()+"/api/rooms", "application/json", nil)
	if err != nil {
		t.Fatal(err)
	}
	var created struct {
		RoomID    string `json:"roomId"`
		HostToken string `json:"hostToken"`
	}
	err = json.NewDecoder(response.Body).Decode(&created)
	response.Body.Close()
	if err != nil || response.StatusCode != http.StatusCreated {
		t.Fatalf("create on first listener: status=%d err=%v", response.StatusCode, err)
	}
	host, _, err := websocket.DefaultDialer.Dial("ws://"+listeners[1].Addr().String()+"/api/rooms/"+created.RoomID+"/ws", nil)
	if err != nil {
		t.Fatal(err)
	}
	defer host.Close()
	_ = host.SetReadDeadline(time.Now().Add(3 * time.Second))
	if err := host.WriteJSON(map[string]string{"type": "auth", "role": "host", "token": created.HostToken}); err != nil {
		t.Fatal(err)
	}
	readType(t, host, "ready")
	host.Close()
	for _, listener := range listeners {
		response, err := http.Get("http://" + listener.Addr().String() + "/healthz")
		if err != nil {
			t.Fatal(err)
		}
		_, _ = io.Copy(io.Discard, response.Body)
		response.Body.Close()
		if response.StatusCode != http.StatusOK {
			t.Fatalf("health status=%d", response.StatusCode)
		}
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	if err := h.Shutdown(ctx); err != nil {
		t.Fatal(err)
	}
	for range listeners {
		if err := <-results; err != http.ErrServerClosed {
			t.Fatalf("shutdown result=%v", err)
		}
	}
}

func TestListenFailureReleasesPreviousAddresses(t *testing.T) {
	reserved, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	address := reserved.Addr().String()
	reserved.Close()
	if _, err := listenAddresses(address + ",invalid"); err == nil {
		t.Fatal("invalid address admitted")
	}
	listener, err := net.Listen("tcp", address)
	if err != nil {
		t.Fatalf("failed startup left previous listener open: %v", err)
	}
	listener.Close()
	if _, err := listenAddresses(""); err == nil {
		t.Fatal("empty address admitted")
	}
}

func fixture(t *testing.T) (*server, *httptest.Server, *room) {
	t.Helper()
	s := &server{rooms: map[string]*room{}, lastCreate: map[string]time.Time{}, publicURL: "https://listen.test", origin: "https://listen.test", ttl: time.Hour, ice: []iceServer{}}
	h := httptest.NewServer(s.handler(t.TempDir()))
	t.Cleanup(h.Close)
	r, err := http.Post(h.URL+"/api/rooms", "application/json", nil)
	if err != nil {
		t.Fatal(err)
	}
	defer r.Body.Close()
	if r.StatusCode != 201 {
		t.Fatalf("create: %d", r.StatusCode)
	}
	var result struct {
		RoomID   string `json:"roomId"`
		GuestURL string `json:"guestUrl"`
	}
	if err = json.NewDecoder(r.Body).Decode(&result); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(result.GuestURL, "#token=") {
		t.Fatal("token must stay in fragment")
	}
	rm := s.rooms[result.RoomID]
	t.Cleanup(func() { s.mu.Lock(); s.closeRoomLocked(rm, "test ended"); s.mu.Unlock() })
	return s, h, rm
}
func join(t *testing.T, h *httptest.Server, rm *room, role, token string) *websocket.Conn {
	t.Helper()
	c, _, err := websocket.DefaultDialer.Dial("ws"+strings.TrimPrefix(h.URL, "http")+"/api/rooms/"+rm.id+"/ws", nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { c.Close() })
	c.SetReadDeadline(time.Now().Add(3 * time.Second))
	if err = c.WriteJSON(map[string]string{"type": "auth", "role": role, "token": token}); err != nil {
		t.Fatal(err)
	}
	return c
}
func readType(t *testing.T, c *websocket.Conn, expected string) envelope {
	t.Helper()
	var e envelope
	if err := c.ReadJSON(&e); err != nil {
		t.Fatal(err)
	}
	if e.Type != expected {
		t.Fatalf("wanted %s, got %s", expected, e.Type)
	}
	return e
}
func TestRoomLifecycleAndIsolation(t *testing.T) {
	s, h, rm := fixture(t)
	host := join(t, h, rm, "host", rm.hostToken)
	readType(t, host, "ready")
	guest := join(t, h, rm, "guest", rm.guestToken)
	readType(t, guest, "ready")
	joined := readType(t, host, "guest_joined")
	second := join(t, h, rm, "guest", rm.guestToken)
	var rejected envelope
	if second.ReadJSON(&rejected) == nil {
		t.Fatal("second guest admitted")
	}
	offer := msg("offer", map[string]string{"type": "offer", "sdp": "test"})
	offer.PeerID = joined.PeerID
	host.WriteJSON(offer)
	readType(t, guest, "offer")
	guest.WriteJSON(msg("answer", map[string]string{"type": "answer", "sdp": "test"}))
	answer := readType(t, host, "answer")
	if answer.PeerID != joined.PeerID {
		t.Fatal("guest identity missing")
	}
	host.WriteJSON(msg("metadata", metadata{Title: "歌曲", PlaybackStatus: "paused"}))
	readType(t, guest, "metadata")
	host.Close()
	var e envelope
	if guest.ReadJSON(&e) == nil {
		t.Fatal("host disconnect did not close guest")
	}
	s.mu.Lock()
	_, exists := s.rooms[rm.id]
	s.mu.Unlock()
	if exists {
		t.Fatal("host disconnect retained room")
	}
}
func TestAuthAndOrigin(t *testing.T) {
	_, h, rm := fixture(t)
	bad := join(t, h, rm, "host", "wrong")
	var e envelope
	if bad.ReadJSON(&e) == nil {
		t.Fatal("bad token admitted")
	}
	header := http.Header{"Origin": []string{"https://evil.test"}}
	c, r, err := websocket.DefaultDialer.Dial("ws"+strings.TrimPrefix(h.URL, "http")+"/api/rooms/"+rm.id+"/ws", header)
	if c != nil {
		c.Close()
	}
	if err == nil || r.StatusCode != 403 {
		t.Fatal("cross-origin socket admitted")
	}
}
func TestNoGuestControlAndNoForeignMetadata(t *testing.T) {
	s, _, rm := fixture(t)
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, kind := range []string{"metadata", "capture_state", "play", "pause", "next", "offer", "delete_room"} {
		if s.routeLocked(rm, nil, "guest", msg(kind, map[string]string{"title": "fake"})) {
			t.Fatalf("guest %s accepted", kind)
		}
	}
	if s.routeLocked(rm, nil, "host", msg("metadata", metadata{Cover: "https://evil.test/track", PlaybackStatus: "playing"})) {
		t.Fatal("remote artwork accepted")
	}
}
func TestRoomExpiryAndHostAdmissionDeadline(t *testing.T) {
	s, _, rm := fixture(t)
	s.sweep(rm.expires.Add(time.Second))
	if len(s.rooms) != 0 || !rm.closed {
		t.Fatal("expiry failed")
	}
	rm2 := &room{id: "pending", created: time.Now().Add(-time.Minute), expires: time.Now().Add(time.Hour)}
	s.rooms[rm2.id] = rm2
	s.sweep(time.Now())
	if !rm2.closed {
		t.Fatal("unclaimed room retained")
	}
}
func TestCreateKeyRateLimitAndTokenEntropy(t *testing.T) {
	s, h, rm := fixture(t)
	if len(rm.id) != 24 || len(rm.hostToken) != 43 || len(rm.guestToken) != 43 || rm.hostToken == rm.guestToken {
		t.Fatal("token generation invalid")
	}
	s.mu.Lock()
	s.creationKey = "secret"
	s.mu.Unlock()
	r, err := http.Post(h.URL+"/api/rooms", "application/json", nil)
	if err != nil {
		t.Fatal(err)
	}
	r.Body.Close()
	if r.StatusCode != 401 {
		t.Fatal("missing creation key admitted")
	}
	req, _ := http.NewRequest("POST", h.URL+"/api/rooms", nil)
	req.Header.Set("Authorization", "Bearer secret")
	r, err = http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	r.Body.Close()
	if r.StatusCode != 429 {
		t.Fatal("creation burst not limited")
	}
}
func TestGuestReconnectGetsSnapshotAndStaleSignalsAreDropped(t *testing.T) {
	_, h, rm := fixture(t)
	host := join(t, h, rm, "host", rm.hostToken)
	readType(t, host, "ready")
	host.WriteJSON(msg("metadata", metadata{Title: "current", PlaybackStatus: "playing"}))
	host.WriteJSON(msg("capture_state", map[string]any{"active": true, "error": ""}))
	guest := join(t, h, rm, "guest", rm.guestToken)
	readType(t, guest, "ready")
	readType(t, guest, "metadata")
	readType(t, guest, "capture_state")
	old := readType(t, host, "guest_joined")
	guest.Close()
	readType(t, host, "guest_left")
	newGuest := join(t, h, rm, "guest", rm.guestToken)
	readType(t, newGuest, "ready")
	readType(t, newGuest, "metadata")
	readType(t, newGuest, "capture_state")
	current := readType(t, host, "guest_joined")
	if old.PeerID == current.PeerID {
		t.Fatal("guest id reused")
	}
	stale := msg("offer", map[string]string{"sdp": "stale"})
	stale.PeerID = old.PeerID
	host.WriteJSON(stale)
	fresh := msg("offer", map[string]string{"sdp": "fresh"})
	fresh.PeerID = current.PeerID
	host.WriteJSON(fresh)
	received := readType(t, newGuest, "offer")
	if string(received.Payload) != string(fresh.Payload) {
		t.Fatal("stale negotiation leaked into new connection")
	}
}
