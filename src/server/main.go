package main

import (
	"context"
	"crypto/rand"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/gorilla/websocket"
)

const maxMessage = 800 * 1024

type envelope struct {
	Type    string          `json:"type"`
	Payload json.RawMessage `json:"payload,omitempty"`
	PeerID  string          `json:"peerId,omitempty"`
}
type iceServer struct {
	URLs       []string `json:"urls"`
	Username   string   `json:"username,omitempty"`
	Credential string   `json:"credential,omitempty"`
}
type metadata struct {
	Title          string `json:"title"`
	Artist         string `json:"artist"`
	Album          string `json:"album"`
	PlaybackStatus string `json:"playbackStatus"`
	Cover          string `json:"cover"`
}
type peer struct {
	conn *websocket.Conn
	send chan envelope
	done chan struct{}
	once sync.Once
	id   string
}

func (p *peer) close() { p.once.Do(func() { close(p.done); _ = p.conn.Close() }) }
func (p *peer) push(e envelope) bool {
	select {
	case <-p.done:
		return false
	default:
	}
	select {
	case p.send <- e:
		return true
	default:
		p.close()
		return false
	}
}
func msg(t string, v any) envelope { b, _ := json.Marshal(v); return envelope{Type: t, Payload: b} }
func (p *peer) writeLoop() {
	ticker := time.NewTicker(15 * time.Second)
	defer ticker.Stop()
	defer p.close()
	for {
		select {
		case <-p.done:
			return
		case e := <-p.send:
			_ = p.conn.SetWriteDeadline(time.Now().Add(8 * time.Second))
			if p.conn.WriteJSON(e) != nil {
				return
			}
		case <-ticker.C:
			if p.conn.WriteControl(websocket.PingMessage, nil, time.Now().Add(5*time.Second)) != nil {
				return
			}
		}
	}
}

type room struct {
	id, hostToken, guestToken string
	created, expires          time.Time
	host, guest               *peer
	metadata                  json.RawMessage
	capture                   json.RawMessage
	closed                    bool
}
type server struct {
	mu          sync.Mutex
	rooms       map[string]*room
	publicURL   string
	origin      string
	ice         []iceServer
	ttl         time.Duration
	creationKey string
	lastCreate  map[string]time.Time
}

func randomToken(n int) string {
	b := make([]byte, n)
	if _, err := rand.Read(b); err != nil {
		panic(err)
	}
	return base64.RawURLEncoding.EncodeToString(b)
}
func equal(a, b string) bool { return subtle.ConstantTimeCompare([]byte(a), []byte(b)) == 1 }
func jsonReply(w http.ResponseWriter, status int, body any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(body)
}
func apiError(w http.ResponseWriter, status int, text string) {
	jsonReply(w, status, map[string]string{"error": text})
}
func (s *server) create(w http.ResponseWriter, r *http.Request) {
	if r.Method != "POST" {
		w.Header().Set("Allow", "POST")
		apiError(w, 405, "请使用 POST")
		return
	}
	if s.creationKey != "" && !equal(r.Header.Get("Authorization"), "Bearer "+s.creationKey) {
		apiError(w, 401, "创建房间密钥无效")
		return
	}
	ip, _, _ := net.SplitHostPort(r.RemoteAddr)
	s.mu.Lock()
	defer s.mu.Unlock()
	if time.Since(s.lastCreate[ip]) < 2*time.Second {
		apiError(w, 429, "创建过于频繁，请稍后重试")
		return
	}
	if len(s.rooms) >= 1000 {
		apiError(w, 503, "房间已满")
		return
	}
	if len(s.lastCreate) >= 4096 {
		for k, v := range s.lastCreate {
			if time.Since(v) > time.Minute {
				delete(s.lastCreate, k)
			}
		}
		if len(s.lastCreate) >= 4096 {
			apiError(w, 503, "请稍后重试")
			return
		}
	}
	s.lastCreate[ip] = time.Now()
	rm := &room{id: randomToken(18), hostToken: randomToken(32), guestToken: randomToken(32), created: time.Now(), expires: time.Now().Add(s.ttl)}
	s.rooms[rm.id] = rm
	jsonReply(w, 201, map[string]any{"roomId": rm.id, "hostToken": rm.hostToken, "guestToken": rm.guestToken, "guestUrl": s.publicURL + "/r/" + rm.id + "#token=" + rm.guestToken, "expiresAt": rm.expires})
}
func (s *server) closeRoomLocked(rm *room, reason string) {
	if rm.closed {
		return
	}
	rm.closed = true
	delete(s.rooms, rm.id)
	// Queue a reason before closing; the browser also handles an abrupt transport close.
	for _, p := range []*peer{rm.host, rm.guest} {
		if p != nil {
			_ = p.conn.WriteControl(websocket.CloseMessage, websocket.FormatCloseMessage(4000, reason), time.Now().Add(time.Second))
			p.close()
		}
	}
	rm.metadata = nil
	rm.capture = nil
}
func (s *server) sweep(now time.Time) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, rm := range s.rooms {
		if now.After(rm.expires) {
			s.closeRoomLocked(rm, "房间已过期")
		} else if rm.host == nil && now.Sub(rm.created) > 45*time.Second {
			s.closeRoomLocked(rm, "房主未连接")
		}
	}
}
func (s *server) ws(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	s.mu.Lock()
	rm, ok := s.rooms[id]
	s.mu.Unlock()
	if !ok {
		apiError(w, 404, "房间不存在或已关闭")
		return
	}
	up := websocket.Upgrader{ReadBufferSize: 4096, WriteBufferSize: 4096, HandshakeTimeout: 8 * time.Second, CheckOrigin: func(r *http.Request) bool { o := r.Header.Get("Origin"); return o == "" || o == s.origin }}
	c, err := up.Upgrade(w, r, nil)
	if err != nil {
		return
	}
	c.SetReadLimit(maxMessage)
	_ = c.SetReadDeadline(time.Now().Add(8 * time.Second))
	var auth struct {
		Type  string `json:"type"`
		Role  string `json:"role"`
		Token string `json:"token"`
	}
	if err = c.ReadJSON(&auth); err != nil {
		_ = c.Close()
		return
	}
	p := &peer{conn: c, send: make(chan envelope, 24), done: make(chan struct{}), id: randomToken(12)}
	s.mu.Lock()
	deny := ""
	if rm.closed || time.Now().After(rm.expires) {
		deny = "房间已关闭"
	} else if auth.Type != "auth" {
		deny = "需要身份验证"
	} else if auth.Role == "host" && equal(auth.Token, rm.hostToken) {
		if rm.host != nil {
			deny = "房主已连接"
		} else {
			rm.host = p
		}
	} else if auth.Role == "guest" && equal(auth.Token, rm.guestToken) {
		if rm.host == nil {
			deny = "房主尚未连接"
		} else if rm.guest != nil {
			deny = "房间已有一位访客"
		} else {
			rm.guest = p
		}
	} else {
		deny = "访问令牌无效"
	}
	if deny != "" {
		s.mu.Unlock()
		_ = c.WriteControl(websocket.CloseMessage, websocket.FormatCloseMessage(4003, deny), time.Now().Add(time.Second))
		p.close()
		return
	}
	go p.writeLoop()
	p.push(msg("ready", map[string]any{"iceServers": s.ice, "expiresAt": rm.expires, "peerId": p.id}))
	if auth.Role == "guest" {
		if rm.metadata != nil {
			p.push(envelope{Type: "metadata", Payload: rm.metadata})
		}
		if rm.capture != nil {
			p.push(envelope{Type: "capture_state", Payload: rm.capture})
		}
		e := msg("guest_joined", nil)
		e.PeerID = p.id
		rm.host.push(e)
	}
	s.mu.Unlock()
	defer func() {
		s.mu.Lock()
		defer s.mu.Unlock()
		p.close()
		if auth.Role == "host" && rm.host == p {
			s.closeRoomLocked(rm, "房主已离开，房间已关闭")
		} else if rm.guest == p {
			rm.guest = nil
			if rm.host != nil {
				e := msg("guest_left", nil)
				e.PeerID = p.id
				rm.host.push(e)
			}
		}
	}()
	_ = c.SetReadDeadline(time.Now().Add(45 * time.Second))
	c.SetPongHandler(func(string) error { return c.SetReadDeadline(time.Now().Add(45 * time.Second)) })
	window := time.Now()
	count := 0
	for {
		var e envelope
		if c.ReadJSON(&e) != nil {
			return
		}
		if time.Since(window) > time.Second {
			window = time.Now()
			count = 0
		}
		count++
		if count > 100 {
			return
		}
		s.mu.Lock()
		if rm.closed {
			s.mu.Unlock()
			return
		}
		valid := s.routeLocked(rm, p, auth.Role, e)
		s.mu.Unlock()
		if !valid {
			return
		}
	}
}
func (s *server) routeLocked(rm *room, p *peer, role string, e envelope) bool {
	switch e.Type {
	case "metadata":
		if role != "host" {
			return false
		}
		var m metadata
		if json.Unmarshal(e.Payload, &m) != nil || len(m.Title) > 4096 || len(m.Artist) > 4096 || len(m.Album) > 4096 || len(m.Cover) > 700*1024 {
			return false
		}
		if m.Cover != "" {
			valid := false
			for _, prefix := range []string{"data:image/jpeg;base64,", "data:image/png;base64,", "data:image/webp;base64,"} {
				if strings.HasPrefix(m.Cover, prefix) {
					_, err := base64.StdEncoding.DecodeString(strings.TrimPrefix(m.Cover, prefix))
					valid = err == nil
					break
				}
			}
			if !valid {
				return false
			}
		}
		switch m.PlaybackStatus {
		case "playing", "paused", "stopped", "unknown":
		default:
			return false
		}
		rm.metadata, _ = json.Marshal(m)
		if rm.guest != nil {
			rm.guest.push(envelope{Type: "metadata", Payload: rm.metadata})
		}
		return true
	case "capture_state":
		if role != "host" {
			return false
		}
		var state struct {
			Active bool   `json:"active"`
			Error  string `json:"error"`
		}
		if json.Unmarshal(e.Payload, &state) != nil || len(state.Error) > 4096 {
			return false
		}
		rm.capture, _ = json.Marshal(state)
		if rm.guest != nil {
			rm.guest.push(envelope{Type: e.Type, Payload: rm.capture})
		}
		return true
	case "offer", "answer", "ice":
		if len(e.Payload) > 128*1024 || !json.Valid(e.Payload) {
			return false
		}
		if role == "host" {
			if e.Type == "answer" {
				return false
			}
			if rm.guest != nil && e.PeerID == rm.guest.id {
				rm.guest.push(e)
			}
		} else {
			if e.Type == "offer" {
				return false
			}
			e.PeerID = p.id
			if rm.host != nil {
				rm.host.push(e)
			}
		}
		return true
	default:
		return false // No remote player-control or arbitrary forwarding protocol.
	}
}
func (s *server) handler(webDir string) http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/api/rooms", s.create)
	mux.HandleFunc("GET /api/rooms/{id}/ws", s.ws)
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, r *http.Request) { jsonReply(w, 200, map[string]string{"status": "ok"}) })
	files := http.FileServer(http.Dir(webDir))
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "GET" && r.Method != "HEAD" {
			w.WriteHeader(http.StatusMethodNotAllowed)
			return
		}
		if strings.HasPrefix(r.URL.Path, "/api/") {
			http.NotFound(w, r)
			return
		}
		path := filepath.Join(webDir, filepath.Clean("/"+r.URL.Path))
		if info, err := os.Stat(path); err == nil && !info.IsDir() {
			files.ServeHTTP(w, r)
			return
		}
		if r.URL.Path != "/" && !strings.HasPrefix(r.URL.Path, "/r/") {
			http.NotFound(w, r)
			return
		}
		http.ServeFile(w, r, filepath.Join(webDir, "index.html"))
	})
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("Referrer-Policy", "no-referrer")
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("X-Frame-Options", "DENY")
		w.Header().Set("Permissions-Policy", "microphone=(), camera=(), display-capture=()")
		w.Header().Set("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; media-src 'self' blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'")
		if strings.HasPrefix(s.publicURL, "https:") {
			w.Header().Set("Strict-Transport-Security", "max-age=31536000")
		}
		mux.ServeHTTP(w, r)
	})
}
func env(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}
func listenAddresses(addresses string) ([]net.Listener, error) {
	var listeners []net.Listener
	for _, address := range strings.Split(addresses, ",") {
		address = strings.TrimSpace(address)
		var listener net.Listener
		var err error
		if address == "" {
			err = errors.New("监听地址不能为空")
		} else {
			listener, err = net.Listen("tcp", address)
		}
		if err != nil {
			for _, opened := range listeners {
				_ = opened.Close()
			}
			return nil, fmt.Errorf("监听 %q 失败: %w", address, err)
		}
		listeners = append(listeners, listener)
	}
	return listeners, nil
}
func main() {
	public := strings.TrimRight(env("LMT_PUBLIC_URL", "http://localhost:18080"), "/")
	u, err := url.Parse(public)
	if err != nil || u.Host == "" || u.User != nil || u.RawQuery != "" || u.Fragment != "" || (u.Path != "" && u.Path != "/") || (u.Scheme != "https" && u.Scheme != "http") {
		log.Fatal("LMT_PUBLIC_URL 必须是 http(s) 源站 URL")
	}
	local := u.Hostname() == "localhost" || u.Hostname() == "127.0.0.1" || u.Hostname() == "::1"
	if u.Scheme != "https" && !local {
		log.Fatal("外部访问必须使用 HTTPS")
	}
	ttl, err := time.ParseDuration(env("LMT_ROOM_TTL", "4h"))
	if err != nil || ttl < time.Minute || ttl > 24*time.Hour {
		log.Fatal("LMT_ROOM_TTL 必须介于 1m 和 24h")
	}
	ice := []iceServer{}
	if v := os.Getenv("LMT_ICE_SERVERS"); v != "" {
		if json.Unmarshal([]byte(v), &ice) != nil {
			log.Fatal("LMT_ICE_SERVERS JSON 无效")
		}
	}
	s := &server{rooms: map[string]*room{}, lastCreate: map[string]time.Time{}, publicURL: public, origin: u.Scheme + "://" + u.Host, ice: ice, ttl: ttl, creationKey: os.Getenv("LMT_CREATE_KEY")}
	addr := env("LMT_ADDR", "127.0.0.1:18080")
	cert, key := os.Getenv("LMT_TLS_CERT"), os.Getenv("LMT_TLS_KEY")
	if (cert == "") != (key == "") {
		log.Fatal("TLS 证书和私钥必须同时配置")
	}
	if u.Scheme == "https" && cert == "" && os.Getenv("LMT_BEHIND_PROXY") != "1" {
		log.Fatal("HTTPS 需要 TLS 证书，或显式配置 LMT_BEHIND_PROXY=1")
	}
	listeners, err := listenAddresses(addr)
	if err != nil {
		log.Fatal(err)
	}
	defer func() {
		for _, listener := range listeners {
			_ = listener.Close()
		}
	}()
	httpServer := &http.Server{Addr: addr, Handler: s.handler(env("LMT_WEB_DIR", "../web/dist")), ReadHeaderTimeout: 8 * time.Second, IdleTimeout: 60 * time.Second, MaxHeaderBytes: 16 * 1024}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	go func() {
		t := time.NewTicker(time.Second)
		defer t.Stop()
		for {
			select {
			case now := <-t.C:
				s.sweep(now)
			case <-ctx.Done():
				return
			}
		}
	}()
	shutdownDone := make(chan struct{})
	go func() {
		defer close(shutdownDone)
		<-ctx.Done()
		s.mu.Lock()
		for _, rm := range s.rooms {
			s.closeRoomLocked(rm, "服务正在关闭")
		}
		s.mu.Unlock()
		c, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = httpServer.Shutdown(c)
	}()
	fmt.Printf("LMT listening on %s; public origin %s\n", addr, public)
	serveErrors := make(chan error, len(listeners))
	for _, listener := range listeners {
		go func(listener net.Listener) {
			if cert != "" {
				serveErrors <- httpServer.ServeTLS(listener, cert, key)
			} else {
				serveErrors <- httpServer.Serve(listener)
			}
		}(listener)
	}
	var serveErr error
	for range listeners {
		err = <-serveErrors
		if err != nil && !errors.Is(err, http.ErrServerClosed) && serveErr == nil {
			serveErr = err
			stop()
		}
	}
	<-shutdownDone
	if serveErr != nil {
		log.Fatal(serveErr)
	}
}
