/* SPDX-License-Identifier: MIT
 *
 * Copyright (C) 2026 WireRoute contributors. All Rights Reserved.
 */

package tunnel

import (
	"bytes"
	"os"
	"regexp"
	"testing"
)

func TestRuntimeLogWriterAddsTimestampAndCapsFile(t *testing.T) {
	path := t.TempDir() + "\\tunnel.log"
	writer, err := openRuntimeLog(path)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = writer.Write([]byte("[profile] Startup complete\n")); err != nil {
		t.Fatal(err)
	}
	if err = writer.Close(); err != nil {
		t.Fatal(err)
	}
	content, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if !regexp.MustCompile(`^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}    \[profile\] Startup complete`).Match(content) {
		t.Fatalf("runtime log did not contain the expected timestamped row: %q", content)
	}

	writer, err = openRuntimeLog(path)
	if err != nil {
		t.Fatal(err)
	}
	oversized := bytes.Repeat([]byte{'x'}, int(maximumRuntimeLogBytes)+128)
	if written, writeErr := writer.Write(oversized); writeErr != nil || written != len(oversized) {
		t.Fatalf("oversized write result was (%d, %v)", written, writeErr)
	}
	if err = writer.Close(); err != nil {
		t.Fatal(err)
	}
	info, err := os.Stat(path)
	if err != nil {
		t.Fatal(err)
	}
	if info.Size() > maximumRuntimeLogBytes {
		t.Fatalf("runtime log exceeded its cap: %d", info.Size())
	}
}
