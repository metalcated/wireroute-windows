# Automatic profiles

Open **Profiles → Automatic profiles** in the sidebar. This Windows feature follows the behavior in WireRoute Android's `feature/network-profile-switching` implementation, using native Windows controls and the existing Blue Nordic/System light/System dark palettes. No Android code or dependencies are embedded in the Windows app.

## On-Demand entry points

Automatic profiles is an enhancement of On-Demand, not a separate Settings-only feature:

- The **Automatic profiles** button above the sidebar profile list is always available and shows whether the feature is on or off.
- Each locally saved profile has an **On-Demand** action below DNS Protection. While automatic mode is enabled, it shows **Automatic profile switching** and opens that configuration directly.
- With automatic mode off, the action opens the profile's single-profile Ethernet/Wi-Fi rules. Choose **Switch profiles by network…** to open Automatic profiles.
- **Edit configuration → On-Demand → Configure…** uses the same flow instead of the old inline checkboxes. Returning from a child screen restores the existing editor, including its unsaved name, configuration, and rule draft. Saving single-profile rules here updates only the editor draft until the configuration is saved; Cancel/Discard on the configuration discards those edits.
- **Settings → Automatic profiles → Configure automatic profiles** remains a secondary entry point.

Automatic profiles is global: **Save** in that screen saves its settings independently, even when opened from a profile editor. Canceling the surrounding profile editor does not undo an explicit automatic-settings save. Rule evaluation waits until all open dialogs close. An unsaved new profile must be saved before it appears in automatic profile pickers.

## Set up rules

1. Choose **Enable automatic profiles**. It is off by default for new and existing installations.
2. Select a **Default profile**, or **VPN off**.
3. Set **Other Wi-Fi**, **Cellular**, and **Ethernet** to the default, VPN off, or a specific saved local profile.
4. Optionally enter **Trusted Wi-Fi** names, one per line, or add the current connected Wi-Fi name.
5. Add **Wi-Fi profile assignments** to use particular profiles on named networks. Click an assignment to edit or remove it from the draft.
6. Save. Cancel discards the draft. In an action picker or assignment editor, Cancel returns to the main draft without applying that subpage's changes. Escape dismisses the whole dialog without saving.

Only locally stored, non-manager profiles can be selected. References use stable profile IDs, so renaming a profile does not break its rules. If a referenced profile is removed, its rule displays **Unavailable profile** and holds the existing connection instead of falling back to a different VPN.

## Which rule wins?

When multiple physical networks are connected, Windows WireRoute consistently prefers Ethernet, then Wi-Fi, then cellular. It does not use changes in the VPN's default route or Internet-connectivity probe result to change this priority. Virtual adapters are not treated as physical Ethernet.

For Wi-Fi, the priority is:

1. Exact trusted name → VPN off for an automatically owned connection.
2. Exact Wi-Fi assignment → its selected profile.
3. Other Wi-Fi action → its selected action or the default profile.

SSID matching is case-sensitive and preserves leading/trailing spaces. Up to 64 named rules are supported, each name limited to 32 UTF-8 bytes. A name cannot be both trusted and assigned. Wi-Fi names are identifiers, not authentication: another access point can advertise the same name.

With named Wi-Fi rules configured, an unavailable or restricted SSID causes **Hold**, not a fallback to Other Wi-Fi. No physical network or an unsupported transport also holds the existing connection. The dialog includes **Wi-Fi name access** help and a user-initiated link to Windows Location settings. WireRoute reads the connected name only; it does not scan nearby networks. See Microsoft's [Wi-Fi access and location guidance](https://learn.microsoft.com/en-us/windows/win32/nativewifi/wi-fi-access-location-changes).

## Manual control and switching safety

- WireRoute only switches or disconnects a profile it automatically connected during this running app session. It never adopts a manual connection, a pre-existing persistent tunnel, or a connection that survived an app restart.
- Manual connection/disconnection invalidates queued work and pauses automatic switching on the current physical network. A different network clears that pause, but a manually active VPN still blocks automation.
- Changing a persistent-service setting is also manual control. Saving appearance or peer defaults does not erase the automatic rules.
- Turning automation off leaves the current VPN unchanged and relinquishes ownership. Saving rules clears a pause but never claims a manual connection.
- Physical-network changes settle for at least two seconds. A two-second poll supplements Windows network events, including recovery after sleep and delayed network information.
- The destination is parsed and checked for blocked hooks, backend availability, and incompatible persistent/encrypted-DNS settings before stopping a working connection. Runtime failures can still occur after a disconnect.
- Handover operations are serialized with manual controls. Policy revision, physical-network identity, active connections, and ownership are rechecked before both halves of a switch. An already-submitted Windows elevation request can still finish; the next evaluation reconciles a later network change. Manual intent prevents late completion from claiming ownership.
- A denied elevation request or failed operation pauses retries until the network changes or the user saves rules again. It does not repeatedly open error dialogs or elevation prompts. Details appear in Settings and local activity logging.
- Unknown active non-hardware adapters with IP addresses conservatively block automatic switching. This includes some host-only, Hyper-V, Docker, and WSL adapters, not just other VPN software. WireRoute leaves those networks unchanged; it does not disable adapters to make rules run. Virtual-machine guests without a detectable physical adapter can likewise report no supported network.

Switching briefly disconnects the VPN and **is not a kill switch**. Full-tunnel filtering may stop when the old tunnel is removed; traffic can use the underlying network during handover or after a failed start.

## Existing On-Demand and Persistent VPN

Enabling automatic profiles pauses the saved Ethernet/Wi-Fi single-profile On-Demand rules without deleting them. The profile detail and editor On-Demand buttons then display **Automatic profile switching** and open the automatic settings directly. If automatic mode is enabled from an already-open single-profile dialog, its saved checkboxes are disabled and labeled as paused when you return.

Disabling automatic mode does **not** silently reactivate the old rules. Select **Resume saved single-profile On-Demand rules when automatic profiles are off** in Automatic profiles, or **Resume saved single-profile On-Demand rules** in a profile's On-Demand screen, and save. Resuming applies to all saved single-profile rules. A normal profile edit or changing a network checkbox alone does not resume paused rules. A later automatic-settings save clears an earlier, unsaved request to resume, so an old editor draft cannot override the newer mode choice.

This feature retains the existing Windows administrator-approval model for every tunnel start and stop. A switch may require approval to stop one tunnel and again to start its replacement. There is no new always-running manager service, new privilege bypass, driver change, RouterOS write, or packaging-capability change. Truly unattended switching requires a separately designed and approved privileged control channel.

Rules execute only while the signed-in WireRoute tray app is running. With Persistent VPN enabled, a tunnel can survive sign-out or reboot, but these network rules do not execute while signed out. A surviving tunnel is treated as an existing/manual connection on the next app launch.

## Storage and privacy

The additive `AutomaticProfiles` and `SingleProfileOnDemandSuspended` fields are saved in the existing current-user DPAPI-protected settings document. No private keys are copied into the policy. Network names and assignments stay on the PC. Ownership and manual pauses are intentionally not persisted or restored as authority after a restart.

## Verification

Automated checks cover transport actions, trusted/assigned/default priority, exact SSID matching, unknown names, unavailable profile references, validation, JSON/DPAPI compatibility, ownership, manual overrides, canceled operations, stale handovers, and native Windows adapter interop. The network-adapter integration test is read-only and does not request Wi-Fi name/location access.

Before release, test on native x64 and ARM64 Windows with safe test profiles:

- Blue Nordic and System light/dark: labels, radio choices, assignment dropdown, text entry, scrolling, keyboard navigation, Cancel/Save, and live theme updates.
- Sidebar access with and without saved profiles; direct profile On-Demand access; Settings access; automatic-mode labels updating immediately without navigating away.
- Edit a name/configuration without saving, open On-Demand and then Automatic profiles, cancel a picker, save/cancel the child screens, and verify the editor draft, scroll position, and working buttons return. Escape closes only the current dialog (or the whole Automatic profiles draft from its picker) and restores its parent. Resize while inside a child screen and check the restored parent layout.
- Cancel the outer configuration editor after saving Automatic profiles: the configuration must remain unchanged while the explicitly saved global settings remain. Confirm a new unsaved profile is absent from the global pickers. Verify that unrelated dialogs still reject accidental overlapping opens.
- Ethernet/Wi-Fi/cellular handovers, simultaneous physical adapters, sleep/resume, and changes while an elevation prompt is open.
- Trusted Wi-Fi, exact-name assignments, unavailable SSID access, deleted/renamed profiles, and no network.
- Manual activation and disconnection from both the app and tray; a manual VPN must survive every automatic rule, including trusted Wi-Fi.
- Cancel each elevation prompt; confirm no repeated prompts on the same network. Check successful session logging and failed-start behavior.
- Existing persistent tunnels and third-party/virtual adapters must not be taken over. Disabling automation must leave the current VPN intact and keep legacy On-Demand paused unless explicitly resumed.

Compiling and policy tests do not establish that physical handovers, location access, or UI rendering have been verified on every device.
