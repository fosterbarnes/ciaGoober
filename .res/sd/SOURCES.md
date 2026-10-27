# Bundled TWiLight Menu++ receivers

Copied 2026-10-27 from `nds\publish\prototype\_nds\TWiLightMenu`. They go to
`<SD>:\_nds\TWiLightMenu\` on the card. The app copies this folder to `sd\` beside
`ciaGoober.exe`.

| File | SHA-256 | Build output |
| --- | --- | --- |
| `main.srldr` | `9488FB55873D725BBC06438975E2E80EDB441F06F15E926376784CB021FAA777` | `nds\TWiLightMenu\title\title.nds` |
| `dsimenu.srldr` | `926241800F4565838671D51B8A86AD7A28B6BA0D08AA0A05DE0E8DCEB4C413CC` | `nds\TWiLightMenu\romsel_dsimenutheme\romsel_dsimenutheme.nds` |

Both match their build outputs. Source: TWiLight Menu++
(https://github.com/DS-Homebrew/TWiLightMenu) at `8526b9c1a47af715118e8b4dde9835cc639e595f` plus
the local forwarder-request changes in the `nds\TWiLightMenu` checkout
(`universal/include/common/forwarderRequest.h`). Build steps are in the nds README.

License: GPL-3.0 (https://github.com/DS-Homebrew/TWiLightMenu). TODO: both files are modified
builds; publish the forwarder-request changes (patch or fork) before shipping a public release.
