/* SPDX-License-Identifier: MIT
 *
 * Copyright (C) 2026 WireRoute contributors. All Rights Reserved.
 */

package tunnel

import (
	"os"
	"sync"
	"time"
)

const maximumRuntimeLogBytes int64 = 2 * 1024 * 1024

type runtimeLogWriter struct {
	file *os.File
	size int64
	mu   sync.Mutex
}

func openRuntimeLog(path string) (*runtimeLogWriter, error) {
	file, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY, 0600)
	if err != nil {
		return nil, err
	}
	info, err := file.Stat()
	if err != nil {
		_ = file.Close()
		return nil, err
	}
	size := info.Size()
	if size > maximumRuntimeLogBytes {
		if err = file.Truncate(0); err != nil {
			_ = file.Close()
			return nil, err
		}
		size = 0
	}
	if _, err = file.Seek(size, 0); err != nil {
		_ = file.Close()
		return nil, err
	}
	return &runtimeLogWriter{file: file, size: size}, nil
}

func (writer *runtimeLogWriter) Write(payload []byte) (int, error) {
	writer.mu.Lock()
	defer writer.mu.Unlock()
	originalLength := len(payload)
	timestamp := []byte(time.Now().Format("2006-01-02 15:04:05.000    "))
	line := make([]byte, 0, len(timestamp)+len(payload))
	line = append(line, timestamp...)
	line = append(line, payload...)
	if int64(len(line)) > maximumRuntimeLogBytes {
		line = line[len(line)-int(maximumRuntimeLogBytes):]
	}
	if writer.size+int64(len(line)) > maximumRuntimeLogBytes {
		if err := writer.file.Truncate(0); err != nil {
			return 0, err
		}
		if _, err := writer.file.Seek(0, 0); err != nil {
			return 0, err
		}
		writer.size = 0
	}
	written, err := writer.file.Write(line)
	writer.size += int64(written)
	if err != nil {
		return 0, err
	}
	return originalLength, nil
}

func (writer *runtimeLogWriter) Close() error {
	writer.mu.Lock()
	defer writer.mu.Unlock()
	_ = writer.file.Sync()
	return writer.file.Close()
}
