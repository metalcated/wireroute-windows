/* SPDX-License-Identifier: MIT
 *
 * Copyright (C) 2026 WireRoute contributors. All Rights Reserved.
 */

package tunnel

import (
	"encoding/json"
	"fmt"
	"log"
	"os"
	"sync"
	"time"

	"golang.zx2c4.com/wireguard/windows/driver"
)

type runtimeMetricsSnapshot struct {
	Version               int    `json:"version"`
	ReceivedBytes         uint64 `json:"receivedBytes"`
	SentBytes             uint64 `json:"sentBytes"`
	LastHandshakeFileTime uint64 `json:"lastHandshakeFileTime"`
}

type runtimeMetricsMonitor struct {
	adapter *driver.Adapter
	file    *os.File
	stop    chan struct{}
	done    chan struct{}
	once    sync.Once
}

func startRuntimeMetricsMonitor(adapter *driver.Adapter, path string) (*runtimeMetricsMonitor, error) {
	if path == "" {
		return nil, nil
	}
	file, err := os.OpenFile(path, os.O_WRONLY|os.O_TRUNC, 0600)
	if err != nil {
		return nil, err
	}
	monitor := &runtimeMetricsMonitor{
		adapter: adapter,
		file:    file,
		stop:    make(chan struct{}),
		done:    make(chan struct{}),
	}
	go monitor.run()
	return monitor, nil
}

func (monitor *runtimeMetricsMonitor) run() {
	defer close(monitor.done)
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	lastFailure := ""
	write := func() {
		if err := monitor.write(); err != nil {
			message := err.Error()
			if message != lastFailure {
				log.Printf("Unable to update WireRoute runtime metrics: %v", err)
				lastFailure = message
			}
		} else {
			lastFailure = ""
		}
	}
	write()
	for {
		select {
		case <-ticker.C:
			write()
		case <-monitor.stop:
			write()
			_ = monitor.file.Sync()
			_ = monitor.file.Close()
			return
		}
	}
}

func (monitor *runtimeMetricsMonitor) write() error {
	configuration, err := monitor.adapter.Configuration()
	if err != nil {
		return err
	}
	snapshot := runtimeMetricsSnapshot{Version: 1}
	peer := configuration.FirstPeer()
	for index := uint32(0); index < configuration.PeerCount; index++ {
		if ^uint64(0)-snapshot.ReceivedBytes < peer.RxBytes {
			snapshot.ReceivedBytes = ^uint64(0)
		} else {
			snapshot.ReceivedBytes += peer.RxBytes
		}
		if ^uint64(0)-snapshot.SentBytes < peer.TxBytes {
			snapshot.SentBytes = ^uint64(0)
		} else {
			snapshot.SentBytes += peer.TxBytes
		}
		if peer.LastHandshake > snapshot.LastHandshakeFileTime {
			snapshot.LastHandshakeFileTime = peer.LastHandshake
		}
		peer = peer.NextPeer()
	}
	payload, err := json.Marshal(snapshot)
	if err != nil {
		return err
	}
	if _, err = monitor.file.Seek(0, 0); err != nil {
		return err
	}
	if _, err = monitor.file.Write(payload); err != nil {
		return err
	}
	if err = monitor.file.Truncate(int64(len(payload))); err != nil {
		return err
	}
	if len(payload) > 4096 {
		return fmt.Errorf("runtime metrics payload exceeded its size limit")
	}
	return nil
}

func (monitor *runtimeMetricsMonitor) stopAndClose() {
	monitor.once.Do(func() {
		close(monitor.stop)
		<-monitor.done
	})
}
