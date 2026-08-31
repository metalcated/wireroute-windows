/* SPDX-License-Identifier: MIT
 *
 * Copyright (C) 2019-2026 WireGuard LLC. All Rights Reserved.
 */

package manager

import (
	"bytes"
	"encoding/base64"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"sort"
	"strings"
	"sync"
	"unicode"
	"unicode/utf8"

	"golang.org/x/sys/windows"

	"golang.zx2c4.com/wireguard/windows/conf"
	"golang.zx2c4.com/wireguard/windows/version"
)

const (
	managerV1Protocol            = "wireroute-manager"
	managerV1Version             = 1
	managerV1MaximumFrameLength  = 1024 * 1024
	managerV1DisplayComment      = "# WireRoute-Display-Name: "
	managerV1MaximumDisplayRunes = 128
)

type managerV1Request struct {
	Version    int             `json:"version"`
	RequestID  int64           `json:"requestId"`
	Method     string          `json:"method"`
	Parameters json.RawMessage `json:"parameters,omitempty"`
}

type managerV1Response struct {
	Version   int             `json:"version"`
	RequestID int64           `json:"requestId"`
	Result    any             `json:"result,omitempty"`
	Error     *managerV1Error `json:"error,omitempty"`
}

type managerV1Error struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}

type managerV1Event struct {
	Version  int    `json:"version"`
	Sequence uint64 `json:"sequence"`
	Event    string `json:"event"`
	Payload  any    `json:"payload,omitempty"`
}

type managerV1Service struct {
	events        *os.File
	eventLock     sync.Mutex
	eventSequence uint64
	manager       *ManagerService
}

type managerV1HelloRequest struct {
	Protocol       string `json:"protocol"`
	MinimumVersion int    `json:"minimumVersion"`
	MaximumVersion int    `json:"maximumVersion"`
	ClientVersion  string `json:"clientVersion"`
	Architecture   string `json:"architecture"`
}

type managerV1Capabilities struct {
	CanListProfiles       bool `json:"canListProfiles"`
	CanReadProfileDetails bool `json:"canReadProfileDetails"`
	CanReadTunnelState    bool `json:"canReadTunnelState"`
	CanImportProfiles     bool `json:"canImportProfiles"`
	CanStartTunnels       bool `json:"canStartTunnels"`
	CanStopTunnels        bool `json:"canStopTunnels"`
}

type managerV1HelloResponse struct {
	Protocol        string                `json:"protocol"`
	SelectedVersion int                   `json:"selectedVersion"`
	ManagerVersion  string                `json:"managerVersion"`
	Capabilities    managerV1Capabilities `json:"capabilities"`
}

type managerV1ProfileSummary struct {
	Name              string `json:"name"`
	DisplayName       string `json:"displayName"`
	State             string `json:"state"`
	DetectedRouteMode string `json:"detectedRouteMode"`
}

type managerV1ListProfilesResponse struct {
	Profiles []managerV1ProfileSummary `json:"profiles"`
}

type managerV1NameRequest struct {
	Name string `json:"name"`
}

type managerV1TunnelStateResponse struct {
	Name  string `json:"name"`
	State string `json:"state"`
}

type managerV1ImportProfileRequest struct {
	DisplayName          string `json:"displayName"`
	WgQuickConfiguration string `json:"wgQuickConfiguration"`
}

type managerV1ImportProfileResponse struct {
	Profile managerV1ProfileSummary `json:"profile"`
}

type managerV1DNS struct {
	Address string `json:"address"`
	Route   string `json:"route"`
}

type managerV1Peer struct {
	PublicKey           string   `json:"publicKey"`
	HasPresharedKey     bool     `json:"hasPresharedKey"`
	AllowedIPs          []string `json:"allowedIps"`
	Endpoint            *string  `json:"endpoint,omitempty"`
	PersistentKeepalive *uint16  `json:"persistentKeepalive,omitempty"`
}

type managerV1ProfileDetail struct {
	Name               string          `json:"name"`
	DisplayName        string          `json:"displayName"`
	InterfaceAddresses []string        `json:"interfaceAddresses"`
	DNSServers         []managerV1DNS  `json:"dnsServers"`
	DNSSearchDomains   []string        `json:"dnsSearchDomains"`
	Peers              []managerV1Peer `json:"peers"`
	DetectedRouteMode  string          `json:"detectedRouteMode"`
	HasHooks           bool            `json:"hasHooks"`
}

type managerV1ProfilesChangedEvent struct {
	ProfileNames []string `json:"profileNames"`
}

type managerV1TunnelStateChangedEvent struct {
	Name      string  `json:"name"`
	State     string  `json:"state"`
	ErrorCode *string `json:"errorCode,omitempty"`
}

type managerV1ManagerStoppingEvent struct {
	Reason string `json:"reason"`
}

type managerV1RemoteError struct {
	code    string
	message string
}

func (e *managerV1RemoteError) Error() string { return e.message }

var (
	managerV1Services     = make(map[*managerV1Service]bool)
	managerV1ServicesLock sync.RWMutex
	managerV1ImportLock   sync.Mutex
)

func managerV1ServerListen(reader, writer, events *os.File, elevatedToken windows.Token) {
	service := &managerV1Service{
		events:  events,
		manager: &ManagerService{elevatedToken: elevatedToken},
	}
	go func() {
		managerV1ServicesLock.Lock()
		managerV1Services[service] = true
		managerV1ServicesLock.Unlock()
		defer func() {
			managerV1ServicesLock.Lock()
			service.eventLock.Lock()
			service.events = nil
			service.eventLock.Unlock()
			delete(managerV1Services, service)
			managerV1ServicesLock.Unlock()
		}()
		service.serve(reader, writer)
	}()
}

func (s *managerV1Service) serve(reader io.Reader, writer io.Writer) {
	helloCompleted := false
	for {
		var request managerV1Request
		if err := readManagerV1Frame(reader, &request); err != nil {
			return
		}
		response := managerV1Response{Version: managerV1Version, RequestID: request.RequestID}
		if request.Version != managerV1Version {
			response.Error = &managerV1Error{"unsupportedVersion", "The manager protocol version is not supported."}
		} else if request.RequestID <= 0 {
			response.Error = &managerV1Error{"invalidRequest", "The request identifier must be positive."}
		} else if helloCompleted && request.Method == "hello" {
			response.Error = &managerV1Error{"invalidRequest", "The manager hello request has already completed."}
		} else if !helloCompleted && request.Method != "hello" {
			response.Error = &managerV1Error{"helloRequired", "The manager hello request must complete first."}
		} else {
			result, err := s.dispatch(request.Method, request.Parameters)
			if err != nil {
				var remote *managerV1RemoteError
				if errors.As(err, &remote) {
					response.Error = &managerV1Error{remote.code, remote.message}
				} else {
					response.Error = &managerV1Error{"managerError", err.Error()}
				}
			} else {
				response.Result = result
				helloCompleted = helloCompleted || request.Method == "hello"
			}
		}
		if err := writeManagerV1Frame(writer, response); err != nil {
			return
		}
	}
}

func (s *managerV1Service) dispatch(method string, raw json.RawMessage) (any, error) {
	switch method {
	case "hello":
		var request managerV1HelloRequest
		if err := decodeManagerV1Parameters(raw, &request); err != nil {
			return nil, err
		}
		if request.Protocol != managerV1Protocol || request.MinimumVersion > managerV1Version || request.MaximumVersion < managerV1Version {
			return nil, remoteManagerV1Error("incompatibleProtocol", "The client and manager do not share a compatible protocol version.")
		}
		canWrite := s.manager.elevatedToken != 0
		return managerV1HelloResponse{
			managerV1Protocol,
			managerV1Version,
			version.Number,
			managerV1Capabilities{true, true, true, canWrite, canWrite, canWrite},
		}, nil
	case "profiles.list":
		var request struct{}
		if err := decodeManagerV1Parameters(raw, &request); err != nil {
			return nil, err
		}
		return s.listProfiles()
	case "profiles.get":
		var request managerV1NameRequest
		if err := decodeManagerV1Parameters(raw, &request); err != nil {
			return nil, err
		}
		return s.getProfile(request.Name)
	case "profiles.import":
		var request managerV1ImportProfileRequest
		if err := decodeManagerV1Parameters(raw, &request); err != nil {
			return nil, err
		}
		return s.importProfile(request)
	case "tunnel.state":
		var request managerV1NameRequest
		if err := decodeManagerV1Parameters(raw, &request); err != nil {
			return nil, err
		}
		state, err := s.manager.State(request.Name)
		if err != nil {
			return nil, err
		}
		return managerV1TunnelStateResponse{request.Name, managerV1State(state)}, nil
	case "tunnel.start":
		var request managerV1NameRequest
		if err := decodeManagerV1Parameters(raw, &request); err != nil {
			return nil, err
		}
		if s.manager.elevatedToken == 0 {
			return nil, remoteManagerV1Error("accessDenied", "This session cannot start tunnels.")
		}
		if err := s.manager.Start(request.Name); err != nil {
			return nil, err
		}
		state, _ := s.manager.State(request.Name)
		return managerV1TunnelStateResponse{request.Name, managerV1State(state)}, nil
	case "tunnel.stop":
		var request managerV1NameRequest
		if err := decodeManagerV1Parameters(raw, &request); err != nil {
			return nil, err
		}
		if s.manager.elevatedToken == 0 {
			return nil, remoteManagerV1Error("accessDenied", "This session cannot stop tunnels.")
		}
		if err := s.manager.Stop(request.Name); err != nil {
			return nil, err
		}
		state, _ := s.manager.State(request.Name)
		return managerV1TunnelStateResponse{request.Name, managerV1State(state)}, nil
	default:
		return nil, remoteManagerV1Error("methodNotFound", "The requested manager method is not available.")
	}
}

func (s *managerV1Service) listProfiles() (managerV1ListProfilesResponse, error) {
	names, err := conf.ListConfigNames()
	if err != nil {
		return managerV1ListProfilesResponse{}, err
	}
	sort.Slice(names, func(i, j int) bool { return conf.TunnelNameIsLess(names[i], names[j]) })
	profiles := make([]managerV1ProfileSummary, 0, len(names))
	for _, name := range names {
		config, err := conf.LoadFromName(name)
		if err != nil {
			continue
		}
		state, _ := s.manager.State(name)
		profiles = append(profiles, managerV1Summary(config, state))
	}
	return managerV1ListProfilesResponse{profiles}, nil
}

func (s *managerV1Service) getProfile(name string) (managerV1ProfileDetail, error) {
	config, err := conf.LoadFromName(name)
	if err != nil {
		return managerV1ProfileDetail{}, remoteManagerV1Error("profileNotFound", "The profile does not exist.")
	}
	detail := managerV1ProfileDetail{
		Name:               config.Name,
		DisplayName:        managerV1DisplayName(config),
		InterfaceAddresses: make([]string, 0, len(config.Interface.Addresses)),
		DNSServers:         make([]managerV1DNS, 0, len(config.Interface.DNS)),
		DNSSearchDomains:   append([]string(nil), config.Interface.DNSSearch...),
		Peers:              make([]managerV1Peer, 0, len(config.Peers)),
		DetectedRouteMode:  managerV1RouteMode(config),
		HasHooks: config.Interface.PreUp != "" || config.Interface.PostUp != "" ||
			config.Interface.PreDown != "" || config.Interface.PostDown != "",
	}
	for _, address := range config.Interface.Addresses {
		detail.InterfaceAddresses = append(detail.InterfaceAddresses, address.String())
	}
	for _, dns := range config.Interface.DNS {
		route := "outsideTunnel"
		for _, peer := range config.Peers {
			for _, allowedIP := range peer.AllowedIPs {
				if allowedIP.Contains(dns) {
					route = "throughTunnel"
					break
				}
			}
		}
		detail.DNSServers = append(detail.DNSServers, managerV1DNS{dns.String(), route})
	}
	for _, peer := range config.Peers {
		item := managerV1Peer{
			PublicKey:       peer.PublicKey.String(),
			HasPresharedKey: !peer.PresharedKey.IsZero(),
			AllowedIPs:      make([]string, 0, len(peer.AllowedIPs)),
		}
		for _, allowedIP := range peer.AllowedIPs {
			item.AllowedIPs = append(item.AllowedIPs, allowedIP.String())
		}
		if !peer.Endpoint.IsEmpty() {
			endpoint := peer.Endpoint.String()
			item.Endpoint = &endpoint
		}
		if peer.PersistentKeepalive != 0 {
			keepalive := peer.PersistentKeepalive
			item.PersistentKeepalive = &keepalive
		}
		detail.Peers = append(detail.Peers, item)
	}
	return detail, nil
}

func (s *managerV1Service) importProfile(request managerV1ImportProfileRequest) (managerV1ImportProfileResponse, error) {
	if s.manager.elevatedToken == 0 {
		return managerV1ImportProfileResponse{}, remoteManagerV1Error("accessDenied", "This session cannot import profiles.")
	}
	displayName, err := managerV1ValidateDisplayName(request.DisplayName)
	if err != nil {
		return managerV1ImportProfileResponse{}, err
	}
	if len(request.WgQuickConfiguration) == 0 || len(request.WgQuickConfiguration) > managerV1MaximumFrameLength {
		return managerV1ImportProfileResponse{}, remoteManagerV1Error("invalidProfile", "The WireGuard configuration is empty or too large.")
	}

	managerV1ImportLock.Lock()
	defer managerV1ImportLock.Unlock()
	name, err := managerV1AvailableName(displayName)
	if err != nil {
		return managerV1ImportProfileResponse{}, err
	}
	config, err := conf.FromWgQuick(request.WgQuickConfiguration, name)
	if err != nil {
		return managerV1ImportProfileResponse{}, remoteManagerV1Error("invalidProfile", err.Error())
	}
	if config.Interface.PreUp != "" || config.Interface.PostUp != "" || config.Interface.PreDown != "" || config.Interface.PostDown != "" {
		return managerV1ImportProfileResponse{}, remoteManagerV1Error("hooksNotAllowed", "Configuration hooks require a separate privileged review and cannot be imported here.")
	}
	config.TrailingComments = append(
		config.TrailingComments,
		managerV1DisplayComment+base64.StdEncoding.EncodeToString([]byte(displayName)))
	if err := config.Save(false); err != nil {
		return managerV1ImportProfileResponse{}, err
	}
	return managerV1ImportProfileResponse{managerV1Summary(config, TunnelStopped)}, nil
}

func managerV1Summary(config *conf.Config, state TunnelState) managerV1ProfileSummary {
	return managerV1ProfileSummary{
		config.Name,
		managerV1DisplayName(config),
		managerV1State(state),
		managerV1RouteMode(config),
	}
}

func managerV1DisplayName(config *conf.Config) string {
	for i := len(config.TrailingComments) - 1; i >= 0; i-- {
		line := strings.TrimSpace(config.TrailingComments[i])
		if !strings.HasPrefix(line, managerV1DisplayComment) {
			continue
		}
		decoded, err := base64.StdEncoding.DecodeString(strings.TrimSpace(strings.TrimPrefix(line, managerV1DisplayComment)))
		if err == nil {
			if value, err := managerV1ValidateDisplayName(string(decoded)); err == nil {
				return value
			}
		}
	}
	return config.Name
}

func managerV1ValidateDisplayName(value string) (string, error) {
	value = strings.TrimSpace(value)
	if value == "" || !utf8.ValidString(value) || utf8.RuneCountInString(value) > managerV1MaximumDisplayRunes {
		return "", remoteManagerV1Error("invalidProfileName", "Enter a profile name between 1 and 128 characters.")
	}
	for _, r := range value {
		if unicode.IsControl(r) {
			return "", remoteManagerV1Error("invalidProfileName", "The profile name cannot contain control characters.")
		}
	}
	return value, nil
}

func managerV1AvailableName(displayName string) (string, error) {
	var builder strings.Builder
	lastSeparator := false
	for _, r := range displayName {
		allowed := r >= 'a' && r <= 'z' || r >= 'A' && r <= 'Z' || r >= '0' && r <= '9' || strings.ContainsRune("_=+.-", r)
		if allowed {
			builder.WriteRune(r)
			lastSeparator = false
		} else if unicode.IsSpace(r) || unicode.IsPunct(r) {
			if builder.Len() > 0 && !lastSeparator {
				builder.WriteByte('-')
				lastSeparator = true
			}
		}
	}
	base := strings.Trim(builder.String(), ".-")
	if len(base) > 32 {
		base = strings.TrimRight(base[:32], ".-")
	}
	if base == "" {
		base = "WireRoute"
	}
	if !conf.TunnelNameIsValid(base) {
		base = "WireRoute-" + base
		if len(base) > 32 {
			base = strings.TrimRight(base[:32], ".-")
		}
	}
	if !conf.TunnelNameIsValid(base) {
		return "", remoteManagerV1Error("invalidProfileName", "A Windows tunnel name could not be derived from the profile name.")
	}

	existing, err := conf.ListConfigNames()
	if err != nil {
		return "", err
	}
	contains := func(candidate string) bool {
		for _, name := range existing {
			if strings.EqualFold(name, candidate) {
				return true
			}
		}
		return false
	}
	if !contains(base) {
		return base, nil
	}
	for index := 2; index < 100000; index++ {
		suffix := fmt.Sprintf("-%d", index)
		prefix := base
		if len(prefix)+len(suffix) > 32 {
			prefix = strings.TrimRight(prefix[:32-len(suffix)], ".-")
		}
		candidate := prefix + suffix
		if conf.TunnelNameIsValid(candidate) && !contains(candidate) {
			return candidate, nil
		}
	}
	return "", remoteManagerV1Error("profileNameConflict", "No available Windows tunnel name could be allocated.")
}

func managerV1RouteMode(config *conf.Config) string {
	for _, peer := range config.Peers {
		for _, prefix := range peer.AllowedIPs {
			if prefix.Bits() == 0 {
				return "full"
			}
		}
	}
	return "split"
}

func managerV1State(state TunnelState) string {
	switch state {
	case TunnelStopped:
		return "stopped"
	case TunnelStarting:
		return "starting"
	case TunnelStarted:
		return "started"
	case TunnelStopping:
		return "stopping"
	default:
		return "unknown"
	}
}

func remoteManagerV1Error(code, message string) error {
	return &managerV1RemoteError{code, message}
}

func decodeManagerV1Parameters(raw json.RawMessage, value any) error {
	if len(raw) == 0 {
		return remoteManagerV1Error("invalidRequest", "The request parameters are missing.")
	}
	decoder := json.NewDecoder(bytes.NewReader(raw))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(value); err != nil {
		return remoteManagerV1Error("invalidRequest", "The request parameters are invalid.")
	}
	if decoder.Decode(&struct{}{}) != io.EOF {
		return remoteManagerV1Error("invalidRequest", "The request parameters contain extra data.")
	}
	return nil
}

func readManagerV1Frame(reader io.Reader, value any) error {
	header := make([]byte, 4)
	if _, err := io.ReadFull(reader, header); err != nil {
		return err
	}
	length := binary.LittleEndian.Uint32(header)
	if length == 0 || length > managerV1MaximumFrameLength {
		return errors.New("manager frame length is invalid")
	}
	payload := make([]byte, length)
	if _, err := io.ReadFull(reader, payload); err != nil {
		return err
	}
	decoder := json.NewDecoder(bytes.NewReader(payload))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(value); err != nil {
		return err
	}
	if decoder.Decode(&struct{}{}) != io.EOF {
		return errors.New("manager frame contains extra JSON data")
	}
	return nil
}

func writeManagerV1Frame(writer io.Writer, value any) error {
	payload, err := json.Marshal(value)
	if err != nil {
		return err
	}
	if len(payload) == 0 || len(payload) > managerV1MaximumFrameLength {
		return errors.New("manager frame length is invalid")
	}
	header := make([]byte, 4)
	binary.LittleEndian.PutUint32(header, uint32(len(payload)))
	if err := writeManagerV1Bytes(writer, header); err != nil {
		return err
	}
	return writeManagerV1Bytes(writer, payload)
}

func writeManagerV1Bytes(writer io.Writer, data []byte) error {
	for len(data) != 0 {
		written, err := writer.Write(data)
		if err != nil {
			return err
		}
		if written == 0 {
			return io.ErrShortWrite
		}
		data = data[written:]
	}
	return nil
}

func managerV1NotifyProfilesChanged() {
	names, err := conf.ListConfigNames()
	if err != nil {
		return
	}
	sort.Slice(names, func(i, j int) bool { return conf.TunnelNameIsLess(names[i], names[j]) })
	managerV1Notify("profiles.changed", managerV1ProfilesChangedEvent{names})
}

func managerV1NotifyTunnelChanged(name string, state TunnelState, tunnelError error) {
	var errorCode *string
	if tunnelError != nil {
		value := "tunnelError"
		errorCode = &value
	}
	managerV1Notify(
		"tunnel.stateChanged",
		managerV1TunnelStateChangedEvent{name, managerV1State(state), errorCode})
}

func managerV1NotifyManagerStopping() {
	managerV1Notify("manager.stopping", managerV1ManagerStoppingEvent{"serviceStopping"})
}

func managerV1Notify(event string, payload any) {
	managerV1ServicesLock.RLock()
	defer managerV1ServicesLock.RUnlock()
	for service := range managerV1Services {
		service := service
		go func() {
			service.eventLock.Lock()
			defer service.eventLock.Unlock()
			if service.events == nil {
				return
			}
			service.eventSequence++
			_ = writeManagerV1Frame(service.events, managerV1Event{
				managerV1Version,
				service.eventSequence,
				event,
				payload,
			})
		}()
	}
}
