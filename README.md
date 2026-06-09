# Hasselblad X2D CIM Firmware — Reverse Engineering Notes

> **Status: Negative result.** Documenting what was tried, what was learned, and what didn't work, so the next researcher doesn't repeat the same dead ends.

## TL;DR

The Hasselblad X2D 100C uses a `.cim` firmware file format that is **AES-128 ECB encrypted** with a key residing in the camera SoC (not in Phocus software). No software-only attack from a PC is feasible.

**The hardware difference between X2D and X2D II is real** — different PDAF design (294 vs 425 zones), added LiDAR, added AI subject-detection accelerator, added AF illuminator, faster IBIS. X2D will never match X2D II's autofocus performance, and Hasselblad's silicon investments are visible. (Note: both bodies have PDAF + CDAF hybrid autofocus — that part is shared. LiDAR and the AI accelerator are what's new.)

**The remaining open question** is whether the X2D's image processor pipeline has the throughput to support even a *basic* AF-C mode, given the camera's 3.3 fps burst rate (substantially slower than 2014-era APS-C cameras that offered AF-C). The X2D's 294-point PDAF count and V-series lens motors are sufficient on paper, but continuous AF additionally requires the sensor + ISP to read PDAF data and drive the lens motor 30–60 times per second while maintaining live view. The X2D's pipeline may not be fast enough for this even though its PDAF count is adequate. Whether Hasselblad's decision not to ship AF-C is a *silicon-cannot* judgment or a *policy-will-not* choice is not externally determinable.

**Practical conclusion:** Hasselblad would have to ship AF-C — or transparently explain why they will not — via official firmware or formal communication. There is no externally-available software path to enable AF-C; the firmware encryption is intact, and the camera enforces capability gating on its own side.

---

## Hardware: X2D 100C vs X2D II 100C

Verified from Hasselblad's published specifications and DPReview / Capture Integration reviews:

| Component             | X2D 100C        | X2D II 100C                  |
| --------------------- | --------------- | ---------------------------- |
| Sensor                | 100MP BSI CMOS  | 100MP BSI CMOS (same)        |
| Sensor size           | 43.8 × 32.9mm   | 43.8 × 32.9mm (same)         |
| Color depth           | 16-bit          | 16-bit (same)                |
| Dynamic range         | 15 stops        | 15.3 stops                   |
| Native ISO            | 64–25600        | 50–25600                     |
| HNCS color science    | yes             | HNCS HDR                     |
| **PDAF zones**        | **294**         | **425** (+44%)               |
| **CDAF**              | **yes**         | **yes** (same)               |
| **LiDAR module**      | none            | added                        |
| **AI subject detect** | none            | human/vehicle/cat/dog        |
| **AF illuminator**    | none            | added                        |
| IBIS                  | 7 stops         | 10 stops (~8× improvement)   |
| **AF-C**              | not exposed     | yes, with V/P/E lenses       |
| Body firmware (latest)| 4.2.0           | 1.2.7.x                      |

> Source: Hasselblad's own published specification comparison.

Both bodies have **PDAF + CDAF hybrid autofocus**. The X2D II's AF improvements over X2D are:
LiDAR module, AI subject detection accelerator, AF illuminator, and a higher-zone-count PDAF
sensor. These add up to faster, more accurate, more situationally-robust autofocus — but
the **fundamental AF detection capability (PDAF + CDAF)** is the same on both bodies.

This is significant because PDAF + CDAF is the autofocus architecture that enables AF-C on
essentially every modern mirrorless camera that has it. LiDAR is not required for AF-C:

| Camera                       | AF detection                     | LiDAR | Burst rate | AF-C  |
| ---------------------------- | -------------------------------- | ----- | ---------- | ----- |
| Sony A7 III (2018)           | PDAF (693) + CDAF                | no    | 10 fps     | yes   |
| Canon EOS R5 (2020)          | Dual Pixel (PDAF + CDAF)         | no    | 12 fps     | yes   |
| Fujifilm X-T4 (2020)         | PDAF + CDAF                      | no    | 15 fps     | yes   |
| **X2D 100C (2022)**          | **PDAF (294) + CDAF**            | **no**  | **3.3 fps** | **no**  |
| **Fujifilm GFX 100 II (2023)** | **PDAF + CDAF + subject detect** | **no**  | **8 fps**   | **yes** |
| **X2D II 100C (2025)**       | **PDAF (425) + CDAF + LiDAR**    | **yes** | **4.5 fps** | **yes** |

The X2D's AF detection architecture (PDAF + CDAF) is identical in kind to every modern
mirrorless camera that supports AF-C. It is not architecturally limited by the absence of
LiDAR — the cameras above achieve AF-C without LiDAR.

The most directly comparable entry in the table above is the **Fujifilm GFX 100 II (2023)**.
Both it and the X2D 100C use a 100MP, 43.8 × 32.9 mm sensor. Both lack LiDAR. They were
released roughly a year apart. The GFX 100 II ships AF-C with subject detection (including
Eye AF) at 8 fps burst, and reviews at launch described it as "the best autofocus system
ever seen in a medium format camera."

This is external observation from a competitor's published specifications, not internal
Hasselblad confirmation, but the implications are concrete:

- **A 100 MP sensor in a 43.8 × 32.9 mm format is not a barrier to AF-C.** Fujifilm
  demonstrates this with the GFX 100 II.
- **LiDAR is not a prerequisite for AF-C in medium format.** Fujifilm again demonstrates this.
- The difference between the X2D 100C and the GFX 100 II appears to be **AF algorithm
  engineering investment**, not a sensor or format limitation.

This does not prove the X2D's specific silicon can do AF-C. The X2D's image processor and
Fujifilm's are different parts, and the 3.3 fps vs 8 fps burst-rate gap suggests Fujifilm
allocated more pipeline budget. But the GFX 100 II eliminates the simplest possible
deflection — *"medium format at 100 MP just can't do AF-C"* — from the conversation.
Whatever the binding constraint on the X2D 100C turns out to be, it is not the format and
it is not the resolution.

**The remaining open question** is whether the X2D's specific image processor pipeline has
the throughput to do AF-C at all. The strongest external evidence that pipeline throughput,
not AF detection capability, may be the binding constraint is the burst rate:

The X2D's 3.3 fps burst rate is several times slower than full-frame mirrorless competitors
with comparable PDAF + CDAF systems — entirely consistent with a sensor-readout / ISP
throughput limit imposed by handling 100 MP per frame. The X2D II's combined upgrade
(425 PDAF + LiDAR + 4.5 fps + AF-C) plausibly reflects a meaningful pipeline-throughput
improvement. If so, the X2D's silicon may genuinely lack the throughput to support continuous
AF at acceptable refresh rates, regardless of having PDAF + CDAF AF detection.

This means the situation may be one of the following, and **we cannot tell from outside which**:

1. **Silicon-cannot.** The X2D's processor pipeline simply cannot read PDAF + drive lens fast
   enough for usable AF-C, and Hasselblad correctly chose not to ship a degraded experience.
2. **Policy-will-not.** The pipeline could support a basic AF-C with some compromises (lower
   refresh, slower burst), but Hasselblad has chosen not to enable it to preserve X2D II
   differentiation.

The presence of `kAutoContinousFocusMode = 2` in the firmware enum is consistent with either:
the code path could be a complete-but-disabled implementation, or it could be a partial stub
that would not work if forced on.

This is genuinely the most important open question. Customers outside Hasselblad cannot answer
it without access to the camera's image processor specifications, which Hasselblad has not
published. The right party to clarify is Hasselblad themselves.

### Note on Phocus's AF-C symbols across versions

A subsequent comparison of **Phocus 3.8.5** (a pre-X2D II release) and **Phocus 3.8.8** (the
first X2D II-aware release) found AF-C-related symbols, enum values, and SWIG-binding
declarations present in both binaries: the `kAutoContinousFocusMode = 2` enum value, the
`AfC9` wire-protocol code as an ASCII string, the `ipcFocusMode` / `ipcFocusModeList`
IPC command names, the `GetSelectableFocusModes()` and `SetFocusMode()` SWIG bindings,
the `focusModeRange` capability field on `sCameraInterface` and `sControlCapabilities`.
Phocus 3.8.5 does **not** know about the X2D II (no `HASSLX30` identifier).

**What this evidence shows.** The AF-C *interface surface* — enum slot, IPC command name,
SWIG binding, capability field accessor — was declared in the Phocus codebase before the
X2D II launched.

**What this evidence does NOT show.** That the C++ implementation behind those declared
interfaces is functional, complete, or tested in 3.8.5. Symbol presence in a binary is
consistent with several possibilities: code that works end-to-end, code that is gated
and never reaches the interface, an empty stub returning success codes, or unused
declarations inherited from a shared camera SDK across Hasselblad product lines.
Distinguishing these requires connecting an X2D to Phocus 3.8.5 and observing the IPC
behaviour directly — work that is planned but had not been completed at the time of
writing this note.

**The minimum claim the symbol-level evidence supports.** Enabling AF-C on the X2D 100C
does not require writing the Phocus-side declarations from scratch — they already exist
in the codebase. Whether anything behind those declarations is functional is a separate
question that this string-and-reflection analysis cannot answer.

Full evidence and cross-check method: [notes/phocus_3_8_5_vs_3_8_8.md](notes/phocus_3_8_5_vs_3_8_8.md).

## What was tested

### 1. Phocus IPC layer

`Phocus.dll` exposes a SWIG-bound .NET interface to its native C++ camera controller:

```
Phocus.Native.eFocusMode:
  kManualFocusMode       = 0
  kAutoSingleFocusMode   = 1
  kAutoContinousFocusMode = 2  ← AFC
  kTrueFocusMode         = 3
  kUndefinedFocusMode    = 255
```

Also accessible via Phocus's named pipe `\\.\pipe\Phocus-7DAF5ECD-9ADE-49f4-8B7C-59183189FD68`
using plist XML protocol. Sending `ipcFocusMode` with `Value=2` returns `IPCReply=0` (Phocus
accepts the command), but the camera body silently rejects it. The `focusModeRange` bitmask
reported by the camera does not include bit 2 (AFC) on X2D firmware 4.2.0.

### 2. Phocus process memory scan

Scanned all 3.8 GB of `Phocus64.exe` working set across multiple states. Searched for:
- `VHABCIM` plaintext header in heap
- ELF magic in heap
- Known ciphertext block from CIM file
- AES key schedule patterns

**Result:** Zero heap hits. The only `VHABCIM` string is a format-string literal in
`PhocusApi64.dll`'s code section. Phocus is a thin transport — it streams the encrypted CIM
bytes directly to the camera without decryption.

### 3. CIM file format

```
0x00  56 48 41 42 43 49 4D 0D 0A      "VHABCIM\r\n"     Magic
0x09  31 38 30 30 30 30 30 5F 50 56 46 "1800000_PVF"    Module ID
0x14  31 30 2E 30 30 2E 32 35 2E 32 31 "10.00.25.21"    Internal version
0x22  32 30 32 35 2D 31 30 2D 33 31    "2025-10-31"     Build date
0x2E  31 33 3A 35 39 3A 33 33          "13:59:33"       Build time
0x38  0D 1A                            EOF marker
0x3A  ...                              Binary header
0x40  ...                              Section table
0x58  ...                              Encrypted payload starts
```

Encryption analysis on 175 MB payload:
- Shannon entropy: 7.997 bits/byte (effectively maximum)
- Repeated 16-byte ciphertext blocks at offsets `0x200, 0x300, …, 0x900` — consistent with
  AES-128 ECB mode encrypting a repeated padding region
- Ruled out XOR with rolling key

### 4. DJI key cross-reference test

DJI acquired Hasselblad in 2017. Tested whether X2D shares cryptographic infrastructure with
DJI products. Loaded all 26 publicly known DJI firmware keys from
[`o-gs/dji-firmware-tools`](https://github.com/o-gs/dji-firmware-tools) and tested each in
AES-128 ECB / CBC / CTR modes against multiple offsets in the CIM payload.

**Result:** No match. All decryptions produced output with entropy >7.93 (statistically
indistinguishable from random). High-score keys were false positives from random 2-byte gzip
magic matches.

Interpretation: Hasselblad maintains independent crypto infrastructure post-DJI-acquisition.

## Conclusion of the software investigation

Software-only attack from a PC is **not viable**. The encryption key resides inside the
X2D's SoC, not in any PC-side software. Phocus is a pure transport. The camera enforces
its own capability gating. This is a well-designed security architecture, and this
investigation cannot proceed further along software-only lines.

## What didn't work (and why future researchers shouldn't repeat these)

- ❌ Sending `ipcFocusMode=2` via Phocus IPC (camera ignores)
- ❌ Direct `CCameraToolController.SetFocusMode(2)` via .NET reflection (same result)
- ❌ Renaming CIM file to fake a newer version (Phocus reads version from inside the file)
- ❌ DJI key library (26 keys, all modes, zero matches)
- ❌ Full Phocus process memory scan (~3.8 GB scanned, no key material)
- ❌ XOR-with-rolling-key hypothesis (output remained high-entropy)

## The only path forward, in this researcher's opinion

The most realistic path for X2D owners who want AF-C is **direct engagement with Hasselblad**:

1. **File a formal feature request with Hasselblad.** Gather V-lens X2D owners, present the
   evidence that PDAF + CDAF hybrid AF systems support continuous AF on every comparable
   camera in the market, and respectfully request basic AF-C as a firmware update — paid
   or free.

2. **Ask Hasselblad to clarify the architectural question publicly.** Whether the X2D's
   image processor pipeline can sustain continuous AF or not is something only Hasselblad
   knows. Owners would benefit from a direct answer.

Other research paths that future investigators might consider (none involving any attempt
to circumvent Hasselblad's security):

- **Cross-version diff.** Byte-level comparison of CIMs across firmware versions can
  localize which regions of the firmware changed in each release, without decrypting them.
  Useful as a structural mapping exercise.
- **Cross-product diff.** Comparison of X2D vs X2D II CIMs (both freely distributed by
  Hasselblad) may yield insight into how their firmware structures differ.

This investigation does not pursue, recommend, or suggest any attack on Hasselblad's
hardware or security infrastructure. The author considers Hasselblad's firmware protection
appropriate for a premium camera platform, and this repo is closed on the technical side.

## Repository structure

```
.
├── README.md
├── LICENSE
├── notes/
│   ├── cim_header_format.md       # CIM format details
│   ├── phocus_ipc_protocol.md     # IPC command reference
│   ├── memory_scan_results.md     # Phocus process memory analysis
│   ├── network_observations.md    # WiFi endpoint port-scan findings
│   └── phocus_3_8_5_vs_3_8_8.md   # AF-C plumbing already present in 3.8.5
└── tools/                     # Diagnostic tool source code (no keys, no decryption)
    ├── cim_dji_key_test.py
    ├── phocus_memdump.cs
    ├── phocus_watcher.cs
    └── x2d_afc_ipc_test.cs
```

## Disclaimer

This repository contains:
- ✅ Factual observations about file structure (any hex editor reveals the same)
- ✅ Documentation of API surfaces already exposed by `Phocus.dll`
- ✅ Negative results from cryptographic tests
- ✅ Source code that performs read-only analysis

This repository does **NOT** contain:
- ❌ Any Hasselblad firmware file (`.cim`)
- ❌ Any decryption keys
- ❌ Any working circumvention method
- ❌ Any tool that bypasses Hasselblad's security

All work was performed on equipment owned by the researcher. No firmware was modified,
distributed, or flashed to any camera. Phocus software was used in its intended capacity.
No copyright-protected material is republished here.

If Hasselblad believes anything in this repository infringes on their rights, please open an
issue and we will discuss. The intent is research and consumer-rights documentation, not piracy.

## License

Documentation under CC BY-SA 4.0. Diagnostic tool source code under MIT License.

## Acknowledgments

- DJI firmware tools community ([o-gs/dji-firmware-tools](https://github.com/o-gs/dji-firmware-tools))
  for prior art that made the key cross-reference test possible
- Hasselblad themselves for clear specification publishing that made the hardware comparison
  possible — and the request reasonable
- Claude (Anthropic) — used as a sounding board during analysis. All experimental setups,
  decisions to pursue or abandon each line of investigation, the choice of negative-result
  framing, and the editorial structure of this repo are original work by the author.
  Tool source code, observed measurements, file format observations, and the conclusions
  drawn from them are mine.
