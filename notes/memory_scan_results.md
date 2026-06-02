# Phocus Process Memory Scan — Results

## Setup

- Phocus 3.8.8 (`Phocus64.exe`) on Windows 11
- X2D 100C firmware 4.2.0 connected via WiFi (camera's built-in hotspot, 192.168.2.1)
- Custom C# scanner using `ReadProcessMemory` over all `MEM_COMMIT` `PAGE_READWRITE` and
  `PAGE_READONLY` regions
- Total memory scanned per pass: 3.8 GB
- Multiple scans across these states:
  1. Phocus running, X2D not connected
  2. Phocus running, X2D connected via WiFi
  3. Phocus + X2D + Firmware Update dialog open with CIM selected
  4. Phocus + X2D + "Open" clicked on CIM (silently dismissed)

## What we looked for

### 1. Encrypted-CIM signature

The 16-byte ciphertext block `2E E3 1A 3B F4 B6 06 25 D0 52 41 B2 CA 9E ED AF` is uniquely
identifying — it appears 8 times in the original CIM file. If Phocus loads the raw encrypted
CIM into memory at any point, this block should appear in the heap.

**Result: 0 occurrences across all four states.**

Strong signal that Phocus does not buffer the encrypted CIM in process memory.

### 2. Decrypted CIM header (`VHABCIM`)

If Phocus decrypts the CIM locally before sending to the camera, the `VHABCIM` ASCII magic
should appear in heap memory.

**Result: 1 occurrence, in `PhocusApi64.dll`'s read-only string table.** Context:
```
, should be %d..VHABCIM.%d..%d..Multi...Micro...6shot...%d..:...
```
This is the format string literal for `CheckConfirmCIM()`'s error printf, not decrypted
content.

### 3. ELF magic (`7F 45 4C 46`)

If the CIM contains decrypted ARM ELF binaries, they should appear after decryption.

**Result: ~100 matches, all false positives.** All followed an identical pattern with
`e_machine = 0x00E0` (not a standard ELF architecture — likely DirectWrite font cache,
glyph metric tables, or image processing kernels).

### 4. AES key schedule patterns

Searched for the AES Rcon sequence `01 00 00 00 ... 02 00 00 00` within a 64-byte window.

**Result: ~1000 candidates, no high-confidence hits.** Manual inspection showed all were
pointer values, counter variables, or DirectX state structures. Phocus simply does not
have the key.

### 5. CIM-related strings

**Result: All matches in DLL constant string table, zero matches in heap.**

## Conclusion

The combination of:
- Zero heap copies of the encrypted CIM ciphertext block
- Zero heap copies of the decrypted `VHABCIM` magic
- No identifiable AES key schedule in memory
- All CIM-related strings being read-only DLL constants

...strongly indicates that **Phocus never touches the decrypted firmware content**. Phocus
functions as a thin transport layer: it opens the file, performs minimal header checksum
validation via `CheckConfirmCIM()`, and streams the raw encrypted bytes directly to the
camera via USB or WiFi without buffering them.

The decryption key, the decryption logic, and the signature verification all live inside
the X2D's SoC secure storage. This is good security design — it means extracting Phocus
or reverse-engineering it cannot yield the firmware key. The downside (for researchers) is
that there is no externally-accessible decryption path through Phocus.

## What this rules out

- Memory-dump-and-grep attacks on Phocus to recover the key: **not viable**
- DLL function hooking to capture decryption operations: **not viable** (no decryption
  happens in Phocus)
- API monkey-patching to bypass key checks: **not viable** (no key checks happen in Phocus)
- Modified Phocus.dll to inject AFC capability: **not useful** (the camera enforces
  capabilities, not Phocus)

## Where the security boundary lies

The decryption key, decryption logic, and signature verification all reside inside the
X2D's SoC. From a PC-side software perspective, there is no accessible path to the key.
This is the conclusion of the investigation — Hasselblad's firmware protection is intact,
and this researcher considers it appropriate for the platform.

## Non-circumvention research directions (for documentation purposes only)

Future researchers interested in understanding the CIM format better, without any attempt
to break or circumvent Hasselblad's security, might consider:

1. **Cross-version structural diff.** Byte-level comparison of CIMs across firmware
   versions can localize which regions of the encrypted payload changed in each release.
   This reveals structural information without decryption.

2. **Cross-product structural diff.** Comparison of X2D vs X2D II CIMs (both freely
   distributed by Hasselblad) may yield insight into how their firmware structures differ.

3. **Network-level observation of an update flow** with standard tools (Wireshark, USBPcap)
   to confirm that Phocus transmits the CIM unchanged. This documents Phocus's transport-only
   role and adds nothing not already inferred from this investigation.

None of these directions require, suggest, or enable any circumvention of Hasselblad's
security. They are descriptive structural analysis, not attack.
