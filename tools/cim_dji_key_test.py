#!/usr/bin/env python3
"""
CIM-against-DJI-keys attack script.

Loads all known DJI keys from dji_imah_fwsig.py, then tries each one
against the X2D CIM file using multiple cipher modes (ECB, CBC).

Detects success by checking for:
  1. Low entropy in decrypted output (real data, not random)
  2. Known plaintext markers (VHABCIM, ELF, ARM, ASCII)
  3. Structured patterns
"""

import os
import re
import sys
import math
import struct
from collections import Counter
from Crypto.Cipher import AES

# Configure these for your environment:
DJI_TOOL = os.path.expanduser(r"~/dji_imah_fwsig.py")           # from o-gs/dji-firmware-tools
CIM_FILE = os.path.expanduser(r"~/Downloads/X2D_firmware.cim")  # your local CIM file (NOT in this repo)

# Encrypted-region offset within CIM (skip plaintext header)
# Header: VHABCIM + module ID + version + date + time + 0x1A + section table
# Encrypted data starts somewhere after 0x58
TEST_OFFSETS = [0x58, 0x80, 0x100, 0x200]
TEST_BYTES   = 4096   # decrypt 4KB per try

def load_dji_keys(path):
    """Extract all AES keys from dji_imah_fwsig.py"""
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()
    keys = {}
    # Match: "KEYNAME": bytes([ ... 16 hex bytes ... ])
    pattern = re.compile(
        r'"([A-Za-z0-9\-]+)":\s*bytes\(\[\s*(?:#[^\n]*\n\s*)*'
        r'((?:0x[0-9A-Fa-f]{2}\s*,?\s*(?:#[^\n]*\n\s*)?){16,32})'
        r'\s*\]\)',
        re.MULTILINE
    )
    for m in pattern.finditer(content):
        name = m.group(1)
        hex_str = m.group(2)
        # Extract just the hex bytes
        byte_strs = re.findall(r'0x([0-9A-Fa-f]{2})', hex_str)
        if len(byte_strs) in (16, 24, 32):
            keys[name] = bytes(int(x, 16) for x in byte_strs)
    return keys

def entropy(data):
    """Shannon entropy of byte sequence."""
    if len(data) == 0: return 0
    counts = Counter(data)
    total = len(data)
    e = 0.0
    for c in counts.values():
        p = c / total
        e -= p * math.log2(p)
    return e

def score_plaintext(data):
    """Score how likely this is real plaintext (higher = better)."""
    score = 0
    # Check for known magic markers
    markers = [
        b'VHABCIM', b'\x7fELF', b'\x1f\x8b',  # gzip
        b'BFLT', b'UBI#', b'\xfd7zXZ',  # xz
        b'PK\x03\x04', b'<?xml', b'#!/',
    ]
    for m in markers:
        if m in data:
            score += 100
    # Low entropy = compressed/structured (good)
    e = entropy(data)
    if e < 6.0: score += 50   # very low = likely structured
    elif e < 7.0: score += 20
    elif e < 7.5: score += 5
    # ASCII printable ratio
    printable = sum(1 for b in data if 0x20 <= b < 0x7F)
    pr = printable / len(data)
    if pr > 0.5: score += 30
    elif pr > 0.3: score += 10
    # Zero runs (padding suggests structure)
    zero_runs = data.count(b'\x00' * 8)
    if zero_runs > 5: score += 15
    return score, e, pr

def try_decrypt(cim_data, key, key_name):
    """Try all reasonable decryption modes with this key."""
    best = None
    for offset in TEST_OFFSETS:
        ciphertext = cim_data[offset:offset + TEST_BYTES]
        # Round down to 16-byte boundary
        ciphertext = ciphertext[:len(ciphertext) - (len(ciphertext) % 16)]
        if len(ciphertext) < 32: continue

        # ECB mode
        try:
            cipher = AES.new(key, AES.MODE_ECB)
            plain = cipher.decrypt(ciphertext)
            score, ent, pr = score_plaintext(plain)
            if best is None or score > best[0]:
                best = (score, ent, pr, offset, 'ECB', plain[:64])
        except Exception:
            pass

        # CBC with zero IV
        try:
            cipher = AES.new(key, AES.MODE_CBC, iv=b'\x00' * 16)
            plain = cipher.decrypt(ciphertext)
            score, ent, pr = score_plaintext(plain)
            if best is None or score > best[0]:
                best = (score, ent, pr, offset, 'CBC0', plain[:64])
        except Exception:
            pass

        # CTR with zero nonce
        try:
            cipher = AES.new(key, AES.MODE_CTR, nonce=b'\x00' * 8, initial_value=0)
            plain = cipher.decrypt(ciphertext)
            score, ent, pr = score_plaintext(plain)
            if best is None or score > best[0]:
                best = (score, ent, pr, offset, 'CTR0', plain[:64])
        except Exception:
            pass

    return best

def main():
    print(f"Loading DJI keys from: {DJI_TOOL}")
    keys = load_dji_keys(DJI_TOOL)
    print(f"Loaded {len(keys)} keys.\n")
    if not keys:
        print("ERROR: No keys parsed!")
        return

    # Show which keys we got
    for name in sorted(keys.keys()):
        k = keys[name]
        print(f"  {name:20s} ({len(k)*8}-bit) {k.hex()[:32]}...")

    print(f"\nLoading CIM file: {CIM_FILE}")
    with open(CIM_FILE, 'rb') as f:
        cim_data = f.read()
    print(f"CIM size: {len(cim_data):,} bytes\n")

    # Baseline: encrypted data entropy
    sample = cim_data[0x200:0x200 + 4096]
    base_score, base_ent, base_pr = score_plaintext(sample)
    print(f"Baseline (encrypted): entropy={base_ent:.3f} printable={base_pr:.2%} score={base_score}\n")

    print("=" * 80)
    print(f"{'KEY NAME':25s} {'OFF':>5s} {'MODE':5s} {'ENT':>6s} {'PRT':>6s} {'SCORE':>6s}")
    print("=" * 80)

    results = []
    for name, key in sorted(keys.items()):
        # Try as AES-128 and AES-256
        keys_to_try = []
        if len(key) >= 16: keys_to_try.append(('128', key[:16]))
        if len(key) >= 24: keys_to_try.append(('192', key[:24]))
        if len(key) >= 32: keys_to_try.append(('256', key[:32]))

        for size, k in keys_to_try:
            best = try_decrypt(cim_data, k, name)
            if best:
                score, ent, pr, offset, mode, sample = best
                label = f"{name}-{size}"
                results.append((score, label, offset, mode, ent, pr, sample))
                marker = " <<<" if score > base_score + 30 else ""
                print(f"{label:25s} {offset:5x} {mode:5s} {ent:6.3f} {pr:6.2%} {score:>6d}{marker}")

    print("\n" + "=" * 80)
    print("TOP 5 CANDIDATES (highest score = most likely correct key)")
    print("=" * 80)
    results.sort(reverse=True)
    for score, label, offset, mode, ent, pr, sample in results[:5]:
        print(f"\n  {label} @ off=0x{offset:x} mode={mode}  score={score}  ent={ent:.3f}  printable={pr:.2%}")
        print(f"  Decrypted hex: {sample.hex()[:96]}")
        ascii_str = ''.join(chr(b) if 0x20 <= b < 0x7F else '.' for b in sample[:48])
        print(f"  ASCII view   : {ascii_str}")

    if results and results[0][0] > base_score + 50:
        print("\n*** POTENTIAL KEY MATCH FOUND ***")
        print(f"   {results[0][1]} produces meaningful plaintext!")
    else:
        print("\nNo DJI key produced clearly meaningful plaintext.")
        print("CIM likely uses Hasselblad-specific key, not shared DJI infrastructure.")

if __name__ == '__main__':
    main()
