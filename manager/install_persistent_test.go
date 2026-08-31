// SPDX-License-Identifier: MIT

package manager

import (
	"testing"

	"golang.zx2c4.com/wireguard/windows/conf"
)

func TestNormalizeWireRouteProfileID(t *testing.T) {
	const expected = "0123456789abcdef0123456789abcdef"
	actual, err := normalizeWireRouteProfileID("  0123456789ABCDEF0123456789ABCDEF  ")
	if err != nil {
		t.Fatal(err)
	}
	if actual != expected {
		t.Fatalf("expected %q, got %q", expected, actual)
	}
	if _, err := normalizeWireRouteProfileID("not-a-profile-id"); err == nil {
		t.Fatal("expected an invalid profile identifier to be rejected")
	}
}

func TestWireRoutePersistentProfileMatches(t *testing.T) {
	const profileID = "0123456789abcdef0123456789abcdef"
	config := &conf.Config{
		TrailingComments: []string{
			"# another comment",
			wireRoutePersistentProfileComment + profileID,
		},
	}
	if !wireRoutePersistentProfileMatches(config, profileID) {
		t.Fatal("expected the matching WireRoute profile marker")
	}
	if wireRoutePersistentProfileMatches(config, "fedcba9876543210fedcba9876543210") {
		t.Fatal("did not expect a different WireRoute profile marker to match")
	}
}
