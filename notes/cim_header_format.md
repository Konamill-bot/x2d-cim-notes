# CIM File Format — Notes

## Plaintext Header (0x00 – 0x39)

ASCII text, line-terminated with `\r\n`, ending with `0x1A` EOF marker:

```
Offset  Bytes                            ASCII              Meaning
------  -------------------------------  -----------------  ----------------------------
0x00    56 48 41 42 43 49 4D 0D 0A       "VHABCIM\r\n"     Magic (Victor Hasselblad AB CIM)
0x09    31 38 30 30 30 30 30 5F 50 56 46 "1800000_PVF"     Module identifier
0x17    31 30 2E 30 30 2E 32 35 2E 32 31 "10.00.25.21"     Internal version
0x25    32 30 32 35 2D 31 30 2D 33 31    "2025-10-31"      Build date
0x31    31 33 3A 35 39 3A 33 33          "13:59:33"        Build time
0x39    0D 0A
0x3A    1A                                                  EOF marker
```

Module identifiers (`1800000_PVF` family) appear to correlate with the sensor / image processing board.

## Binary Header (0x3A – 0x57)

```
0x3B  00 00 00 00       Reserved / padding
0x3F  03                Section count
0x40  41 88 6A 34       Section[0].type   = 0x346A8841  (little-endian uint32)
0x44  0A 72 08 00       Section[0].size   = 0x0008720A  (553,482 bytes)
0x48  00 00 00 00       Section[1].type   = 0
0x4C  00 00 00 00       Section[1].size   = 0
0x50  00 00 00 00       Section[2].type   = 0
0x54  00 00 00 00       Section[2].size   = 0
```

Caveats:
- Total file size is 175,245,312 bytes, but section[0] reports only 553 KB. The remaining
  ~167 MB must be tracked via some structure not yet identified. Possible explanations:
  the section count interpretation is wrong, multiple CIM substreams are concatenated, or
  the bytes at 0x40 are not actually a section table.
- The `CheckConfirmCIM()` function in `PhocusApi64.dll` formats:
  `"CheckConfirmCIM(): code %d is %d, should be %d"`.
  This is a checksum comparison, not encryption verification.

## Encrypted Payload (0x58 – EOF)

- Shannon entropy: **7.997 bits/byte** measured across consecutive 64 KB blocks throughout
  the file (effectively maximum, confirming strong encryption).
- **AES-128 ECB confirmed** by the presence of identical 16-byte ciphertext blocks at
  offsets `0x200, 0x300, 0x400, …, 0x900` (8 occurrences of `2E E3 1A 3B F4 B6 06 25 D0 52
  41 B2 CA 9E ED AF`).
  - In ECB mode, identical plaintext → identical ciphertext, which is why the pattern is
    visible. This is one of ECB's well-known weaknesses but tells us nothing about the key.
- **Not XOR-with-rolling-key.** Tested with the repeated ciphertext block as a 16-byte XOR
  key — decrypted output had entropy 7.996 (no change), no recognizable magic markers.
- **Chi-square distribution** of decrypted bytes near 256 (expected for true random / strong
  cipher).

## What we DON'T know

- Whether the file is a single AES-encrypted blob or multiple independently-encrypted sections
- Whether there is an outer signature wrapper (RSA/ECDSA) verified by the camera before
  decryption
- Whether IV/nonce data is encoded somewhere we didn't recognize
- Whether the section table at 0x40 is the actual structure (the size mismatch is suspicious)
- Whether the encryption key is per-device, per-product-line, or global to Hasselblad

Answering any of these requires access to a real decryption key or a hardware-attack-derived
dump.
