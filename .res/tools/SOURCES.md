# Bundled tools

Copied 2026-10-27 from the `nds` workspace (`C:\Users\Foster\Documents\GitHub\nds`). The app copies
this folder to `tools\` beside `ciaGoober.exe`.

| File | SHA-256 | Origin |
| --- | --- | --- |
| `bannertool.exe` | `4AE03C7D6ED6649582288281BCF06F6587E5A60DA49A0B4B78544DDF6D7568E6` | bannertool v1.2.2 release, from `nds\YANBF\generator` |
| `makerom.exe` | `EC19388004052689564103CE2339AD852A6182B6BD2A814937428C67DE163E34` | makerom v0.18 release, from `nds\YANBF\generator` |
| `forwarder.elf` | `E2AFBB8A7C4C5D356ECD2D48D9AB147A2C37EF05D20181D7068908B117C34E9A` | Modified YANBF forwarder, built from `nds\YANBF\forwarder` |
| `build-cia.rsf` | `72E97E56D41C846BD8E1012AB9C8959A189329EF755798253CB6D47970CC21FB` | `nds\YANBF\generator\data` |
| `dsboot.wav` | `10A43DF52A0DB77EA06B92162B88B630B178D2BFFA8C09F8BBB7FBD3971D02F0` | `nds\YANBF\generator\data` (default DS banner sound; used only when `dsboot.ogg` is missing) |
| `dsboot.ogg` | `65C8FC7EE5D5B752BFC9DCEAE082CDF639458DF77531BFDCBFFB4F71B490F725` | User-supplied 2026-10-27, OGG of `dsboot.wav` (default DS banner sound) |
| `gbaboot.ogg` | `0DB3BFED78EB5AA0DA5AC5C7288CDB098238C12BFAA5ECF250F1F701DCA99584` | User-supplied 2026-10-27, GBA SP intro, 3.0 s (default GBA banner sound) |
| `gbaboot.wav` | `B8579165BDB3D228BCB042270F957E9574600B398A0E500117646CCCD8F7E385` | User-supplied 2026-10-27, WAV of `gbaboot.ogg`; used only when `gbaboot.ogg` is missing |
| `gbcboot.ogg` | `DD2DE14E05A0E1800A8DD8346E9C3A61DDAE6D687970EB653F5D6D0B2E4CBCEC` | User-supplied 2026-10-27, Game Boy Color boot sound, 1.35 s (default GB/GBC banner sound) |
| `gbcboot.wav` | `115AD813959A49A0C74346BFFE6FC670804E1100D19AAEEDCB3ADC5119FB90D3` | User-supplied 2026-10-27, WAV of `gbcboot.ogg`; used only when `gbcboot.ogg` is missing |

`forwarder.elf` is identical in `YANBF\forwarder`, `YANBF\generator\data` and the
`publish\yanbfWindowsThemed` package. Its source is YANBF
(https://github.com/YANBForwarder/YANBF) at `bcd13c73469fab5777b60b94bd46ece5dba6ce7a` plus the
local changes in `nds\YANBF\forwarder\source\main.c` (`aptSetChainloader` launch of TWiLight).
Rebuild it with the MSYS2 command in the nds README, then update this table.

## Licenses

| File | License | Source |
| --- | --- | --- |
| `bannertool.exe` | MIT | https://github.com/Epicpkmn11/bannertool (v1.2.2 release) |
| `makerom.exe` | MIT | https://github.com/3DSGuy/Project_CTR (`makerom/LICENSE`) |
| `forwarder.elf`, `build-cia.rsf` | GPL-2.0 | https://github.com/YANBForwarder/YANBF |

TODO: `forwarder.elf` is a modified GPL-2.0 build. Publish the `main.c` changes (patch or fork)
before shipping a public release.
