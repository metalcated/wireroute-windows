// SPDX-License-Identifier: MIT

package manager

import (
	"testing"

	"golang.zx2c4.com/wireguard/windows/conf"
)

func TestManagerV1SummaryIncludesInterfacePublicKey(t *testing.T) {
	privateKey, err := conf.NewPrivateKey()
	if err != nil {
		t.Fatal(err)
	}
	config := &conf.Config{
		Name: "office",
		Interface: conf.Interface{
			PrivateKey: *privateKey,
		},
	}

	summary := managerV1Summary(config, TunnelStopped)
	want := privateKey.Public().String()
	if summary.InterfacePublicKey != want {
		t.Fatalf("expected interface public key %q, got %q", want, summary.InterfacePublicKey)
	}
}
