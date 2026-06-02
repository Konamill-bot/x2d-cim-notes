# Hasselblad X2D CIM Firmware — Reverse Engineering Notes

> **Status: Negative result.** Documenting what was tried, what was learned, and what didn't work, so the next researcher doesn't repeat the same dead ends.

## TL;DR

The Hasselblad X2D 100C uses a `.cim` firmware file format that is **AES-128 ECB encrypted** with a key residing in the camera SoC (not in Phocus software). No software-only attack from a PC is feasible.

**The hardware difference between X2D and X2D II is real** — different PDAF design (294 vs 425 zones), added LiDAR, added CDAF, new AI processor, faster IBIS. X2D will never match X2D II's autofocus performance, and Hasselblad's silicon investments are visible.

**The remaining open question** is whether the X2D's image processor pipeline has the throughput to support even a *basic* AF-C mode, given the camera's 3.3 fps burst rate (substantially slower than 2014-era APS-C cameras that offered AF-C). The X2D's 294-point PDAF count and V-series lens motors are sufficient on paper, but continuous AF additionally requires the sensor + ISP to read PDAF data and drive the lens motor 30–60 times per second while maintaining live view. The X2D's pipeline may not be fast enough for this even though its PDAF count is adequate. Whether Hasselblad's decision not to ship AF-C is a *silicon-cannot* judgment or a *policy-will-not* choice is not externally determinable.

**Practical conclusion:** Hasselblad would have to ship AF-C — or transparently explain why they will not — via official firmware or formal communication. There is no community jailbreak path without hardware attack on the SoC.

---

## Hardware: X2D 100C vs X2D II 100C

Verified from Hasselblad's published specifications and DPReview / Capture Integration reviews:

| Component             | X2D 100C        | X2D II 100C                  |
| --------------------- | --------------- | ---------------------------- |
| Sensor                | 100MP BSI CMOS  | 100MP BSI CMOS (same)        |
| Sensor size           | 43.8 × 32.9mm   | 43.8 × 32.9mm (same)         |
| Dynamic range         | 15 stops        | 15.3 stops                   |
| Native ISO            | 64–25600        | 50–25600                     |
| **PDAF zones**        | **294**         | **425** (+44%)               |
| **CDAF**              | none            | added                        |
| **LiDAR module**      | none            | added                        |
| **AI subject detect** | none            | human/vehicle/cat/dog        |
| **AF illuminator**    | none            | added                        |
| IBIS                  | 7 stops         | 10 stops (~8× improvement)   |
| **AF-C**              | not exposed     | yes, with V/P/E lenses       |
| Body firmware (latest)| 4.2.0           | 1.2.7.x                      |

The X2D II's AF improvements are real hardware: a new PDAF sensor with more zones, a physical
LiDAR module, an AI inference accelerator. These are not unlockable on X2D — the silicon isn't
there.

**The harder question** is whether the X2D body has sufficient image processing pipeline
bandwidth to do even a basic AF-C at all. The strongest external evidence that pipeline
throughput, not PDAF count, may be the binding constraint:

| Camera                | Sensor       | PDAF | Burst rate | AF-C  |
| --------------------- | ------------ | ---- | ---------- | ----- |
| Sony A6000 (2014)     | APS-C 24MP   | 179  | 11 fps     | yes   |
| Sony A7 III (2018)    | FF 24MP      | 693  | 10 fps     | yes   |
| **X2D 100C (2022)**   | **MF 100MP** | **294** | **3.3 fps** | **no** |
| **X2D II 100C (2025)**| **MF 100MP** | **425** | **4.5 fps** | **yes** |

The X2D's 3.3 fps burst rate is several times slower than 2014-era APS-C — entirely consistent
with a sensor-readout / ISP throughput limit imposed by handling 100 MP per frame. The X2D II's
combined upgrade (425 PDAF + 4.5 fps + AF-C) plausibly reflects a single underlying improvement:
faster sensor readout and ISP. If so, the X2D's silicon may genuinely lack the throughput to
support continuous AF at acceptable refresh rates, regardless of PDAF zone count.

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

## What you would need to actually break this

Software-only attack from a PC: **not viable**. The key is in the X2D's SoC secure storage.

Hardware attack surface:
1. JTAG / UART (likely fused off in production)
2. Voltage / clock glitching of bootloader signature check (~$1k–$3k, weeks of effort)
3. Side-channel power analysis (academic instrumentation)
4. Chip-off + decapping + electron microscope (lab-grade, $50k+)

Estimated effort for success: 6–18 months of focused work, significant chance of failure.

## What didn't work (don't waste your time)

- ❌ Sending `ipcFocusMode=2` via Phocus IPC (camera ignores)
- ❌ Direct `CCameraToolController.SetFocusMode(2)` via .NET reflection (same result)
- ❌ Renaming CIM file to fake a newer version (Phocus reads version from inside the file)
- ❌ DJI key library (26 keys, all modes, zero matches)
- ❌ Full Phocus process memory scan (~3.8 GB scanned, no key material)
- ❌ XOR-with-rolling-key hypothesis (output remained high-entropy)

## Suggested approaches for future research

1. **Cross-version diff.** Obtain CIMs from multiple X2D firmware versions. Byte-level diff
   localizes regions that changed — useful for prioritizing future hardware attacks.

2. **Cross-product diff.** Compare X2D vs X2D II CIM if obtainable. If the SoC platform is
   shared, the difference may localize the AFC enablement bytes.

3. **Cooperate with academic side-channel researchers.** Korea's KAERI published the
   PUEK-2017-09 DJI key in November 2025 and might be interested in adjacent camera platforms.

4. **File a formal feature request with Hasselblad.** The highest-probability path: gather
   V-lens X2D owners, present the evidence, and request basic AF-C as a firmware update.

## Repository structure

```
.
├── README.md
├── LICENSE
├── notes/
│   ├── cim_header_format.md   # CIM format details
│   ├── phocus_ipc_protocol.md # IPC command reference
│   └── memory_scan_results.md # Phocus process memory analysis
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
- Anthropic Claude for collaborative research assistance
- Hasselblad themselves for clear specification publishing that made the hardware comparison
  possible — and the request reasonable
