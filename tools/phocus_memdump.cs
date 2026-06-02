using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Phocus Memory Scanner for CIM encryption key
// Strategy: Open Phocus -> trigger firmware update dialog -> run this
// Searches Phocus process memory for:
//   1. AES key candidates near CIM-related strings
//   2. Decrypted CIM data (low entropy regions containing VHABCIM or ELF magic)
//   3. Key schedule patterns (AES expanded keys)

class Phocus_MemDump
{
    const int PROCESS_VM_READ = 0x0010;
    const int PROCESS_QUERY_INFORMATION = 0x0400;
    const int PROCESS_VM_OPERATION = 0x0008;

    [DllImport("kernel32.dll")]
    static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")]
    static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);
    [DllImport("kernel32.dll")]
    static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress,
        out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    const uint MEM_COMMIT      = 0x1000;
    const uint PAGE_READWRITE  = 0x04;
    const uint PAGE_READONLY   = 0x02;
    const uint PAGE_EXECUTE_READ = 0x20;
    const uint PAGE_EXECUTE_READWRITE = 0x40;

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("================================================");
        Console.WriteLine("  Phocus Memory Scanner for CIM Decryption Key");
        Console.WriteLine("================================================\n");

        // Find Phocus64.exe
        var procs = Process.GetProcessesByName("Phocus64");
        if (procs.Length == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[ERROR] Phocus64.exe is not running.");
            Console.ResetColor();
            Console.WriteLine("\nSetup:");
            Console.WriteLine("  1. Open Phocus");
            Console.WriteLine("  2. Connect X2D");
            Console.WriteLine("  3. Open File -> Update Firmware (don't actually install)");
            Console.WriteLine("  4. Run this tool while the dialog is showing");
            Pause(); return;
        }

        var proc = procs[0];
        Console.WriteLine("[OK] Found Phocus64.exe PID=" + proc.Id);
        Console.WriteLine("     Working set: " + (proc.WorkingSet64 / 1024 / 1024) + " MB\n");

        // Load CIM for known-plaintext comparison
        string cimPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "X2D_100C_v4_2_0.cim");
        byte[] cimSample = null;
        if (File.Exists(cimPath))
        {
            cimSample = new byte[4096];
            using (var fs = File.OpenRead(cimPath))
            {
                fs.Position = 0x80; // skip header
                fs.Read(cimSample, 0, 4096);
            }
            Console.WriteLine("[OK] Loaded CIM sample (4KB from 0x80) for cross-reference.\n");
        }

        IntPtr hProc = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, proc.Id);
        if (hProc == IntPtr.Zero)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[ERROR] Cannot open Phocus process. Run as Administrator!");
            Console.ResetColor();
            Pause(); return;
        }

        Console.WriteLine("Scanning regions... (this takes 30-60 seconds)\n");

        var findings = new ScanResults();

        IntPtr addr = IntPtr.Zero;
        IntPtr maxAddr = new IntPtr(0x7FFFFFFFFFFF);
        int regionCount = 0;
        long totalBytes = 0;

        while (addr.ToInt64() < maxAddr.ToInt64())
        {
            MEMORY_BASIC_INFORMATION mbi;
            int result = VirtualQueryEx(hProc, addr, out mbi, (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
            if (result == 0) break;

            long regionSize = mbi.RegionSize.ToInt64();

            // Only scan committed RW or RWX memory (heap, stack, allocated)
            bool isCommitted = mbi.State == MEM_COMMIT;
            bool isReadable  = mbi.Protect == PAGE_READWRITE || mbi.Protect == PAGE_EXECUTE_READWRITE
                            || mbi.Protect == PAGE_READONLY  || mbi.Protect == PAGE_EXECUTE_READ;

            if (isCommitted && isReadable && regionSize > 0 && regionSize < 256 * 1024 * 1024)
            {
                regionCount++;
                ScanRegion(hProc, mbi.BaseAddress, (int)regionSize, cimSample, findings);
                totalBytes += regionSize;
                if (regionCount % 50 == 0)
                    Console.Write(".");
            }

            long next = mbi.BaseAddress.ToInt64() + regionSize;
            if (next <= addr.ToInt64()) break;
            addr = new IntPtr(next);
        }

        Console.WriteLine("\n\n=== Scan complete ===");
        Console.WriteLine("Regions scanned: " + regionCount);
        Console.WriteLine("Total bytes:     " + (totalBytes / 1024 / 1024) + " MB\n");

        // Report findings
        ReportFindings(findings);

        CloseHandle(hProc);
        Pause();
    }

    class ScanResults
    {
        public List<string> vhabcimPlaintext = new List<string>();   // decrypted CIM headers (good sign!)
        public List<string> elfHeaders        = new List<string>();   // ELF firmware images
        public List<string> aesKeyCandidates  = new List<string>();   // 16/32 byte regions near "key"/"AES" strings
        public List<string> cimRelatedStrings = new List<string>();   // strings related to CIM processing
        public List<string> repeatedBlock     = new List<string>();   // matches of the 16-byte ECB pattern
    }

    // The repeated AES-ECB ciphertext block we found in CIM
    static readonly byte[] CipherBlock = {
        0x2E, 0xE3, 0x1A, 0x3B, 0xF4, 0xB6, 0x06, 0x25,
        0xD0, 0x52, 0x41, 0xB2, 0xCA, 0x9E, 0xED, 0xAF
    };

    static void ScanRegion(IntPtr hProc, IntPtr baseAddr, int size, byte[] cimSample, ScanResults f)
    {
        byte[] buf = new byte[size];
        IntPtr read;
        if (!ReadProcessMemory(hProc, baseAddr, buf, new IntPtr(size), out read)) return;
        int len = read.ToInt32();

        // 1. Look for VHABCIM in plaintext (means we found decrypted CIM!)
        for (int i = 0; i < len - 8; i++)
        {
            if (buf[i]==0x56 && buf[i+1]==0x48 && buf[i+2]==0x41 && buf[i+3]==0x42
             && buf[i+4]==0x43 && buf[i+5]==0x49 && buf[i+6]==0x4D)
            {
                // Skip if at file start (encrypted version's plain header)
                // Capture context
                int start = Math.Max(0, i - 16);
                int ctxLen = Math.Min(96, len - start);
                string ctx = "0x" + (baseAddr.ToInt64() + i).ToString("X12") + ": " + HexDump(buf, start, ctxLen);
                f.vhabcimPlaintext.Add(ctx);
                if (f.vhabcimPlaintext.Count > 20) break;
            }
        }

        // 2. Look for ELF magic (7F 45 4C 46) - ARM firmware binary
        for (int i = 0; i < len - 4; i++)
        {
            if (buf[i]==0x7F && buf[i+1]==0x45 && buf[i+2]==0x4C && buf[i+3]==0x46)
            {
                string ctx = "0x" + (baseAddr.ToInt64() + i).ToString("X12") + ": " + HexDump(buf, i, 32);
                f.elfHeaders.Add(ctx);
                if (f.elfHeaders.Count > 10) break;
            }
        }

        // 3. Look for our repeated cipher block (means raw CIM is loaded in this region)
        for (int i = 0; i < len - 16; i++)
        {
            bool match = true;
            for (int j = 0; j < 16; j++)
            {
                if (buf[i+j] != CipherBlock[j]) { match = false; break; }
            }
            if (match)
            {
                string ctx = "0x" + (baseAddr.ToInt64() + i).ToString("X12");
                f.repeatedBlock.Add(ctx);
                if (f.repeatedBlock.Count > 30) break;
            }
        }

        // 4. Look for "VHABCIM" or "HASSLX29" or "firmware" strings - AES key often near them
        FindStringWithContext(buf, len, baseAddr, "HASSLX29.CIM", f.cimRelatedStrings, 5);
        FindStringWithContext(buf, len, baseAddr, "CheckConfirmCIM", f.cimRelatedStrings, 5);

        // 5. Detect AES key schedule patterns
        // AES-128 expanded key is 176 bytes (11 round keys × 16). Round keys are derived
        // via Rcon: 01 00 00 00, 02 00 00 00, 04 00 00 00, 08, 10, 20, 40, 80, 1B, 36
        // If we find these constants nearby, an AES key schedule may be there.
        FindAESScheduleHints(buf, len, baseAddr, f.aesKeyCandidates);
    }

    static void FindStringWithContext(byte[] buf, int len, IntPtr baseAddr, string needle, List<string> output, int maxCount)
    {
        byte[] pat = Encoding.ASCII.GetBytes(needle);
        for (int i = 0; i < len - pat.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pat.Length; j++)
                if (buf[i+j] != pat[j]) { match = false; break; }
            if (match)
            {
                string ctx = "0x" + (baseAddr.ToInt64() + i).ToString("X12") + " [" + needle + "]";
                output.Add(ctx);
                if (output.Count >= maxCount) return;
            }
        }
    }

    static void FindAESScheduleHints(byte[] buf, int len, IntPtr baseAddr, List<string> output)
    {
        // Look for sequence: 01 00 00 00 ... 02 00 00 00 ... within ~200 bytes (Rcon pattern in key schedule)
        for (int i = 0; i < len - 200; i += 16)
        {
            if (buf[i]==0x01 && buf[i+1]==0x00 && buf[i+2]==0x00 && buf[i+3]==0x00)
            {
                // Look ahead for 02 00 00 00 within reasonable AES schedule distance
                for (int j = i + 12; j < Math.Min(i + 64, len - 4); j += 4)
                {
                    if (buf[j]==0x02 && buf[j+1]==0x00 && buf[j+2]==0x00 && buf[j+3]==0x00)
                    {
                        // Possible AES schedule - capture the first 16 bytes as potential key
                        long addr = baseAddr.ToInt64() + i - 16;
                        if (i >= 16)
                        {
                            string key = HexDump(buf, i - 16, 16);
                            string ctx = "0x" + addr.ToString("X12") + " key candidate: " + key;
                            output.Add(ctx);
                            if (output.Count > 10) return;
                        }
                        break;
                    }
                }
            }
        }
    }

    static string HexDump(byte[] buf, int offset, int length)
    {
        var sb = new StringBuilder();
        int end = Math.Min(offset + length, buf.Length);
        for (int i = offset; i < end; i++)
            sb.Append(buf[i].ToString("X2")).Append(' ');
        sb.Append(" | ");
        for (int i = offset; i < end; i++)
            sb.Append(buf[i] >= 0x20 && buf[i] < 0x7F ? (char)buf[i] : '.');
        return sb.ToString();
    }

    static void ReportFindings(ScanResults f)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("== 1. VHABCIM in plaintext (decrypted CIM!) ==");
        Console.ResetColor();
        if (f.vhabcimPlaintext.Count == 0)
            Console.WriteLine("  None found.\n");
        else
        {
            Console.WriteLine("  Found " + f.vhabcimPlaintext.Count + " occurrences:");
            foreach (var x in f.vhabcimPlaintext) Console.WriteLine("    " + x);
            Console.WriteLine();
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("== 2. ELF firmware binaries ==");
        Console.ResetColor();
        if (f.elfHeaders.Count == 0)
            Console.WriteLine("  None found.\n");
        else
        {
            foreach (var x in f.elfHeaders) Console.WriteLine("    " + x);
            Console.WriteLine();
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("== 3. Repeated cipher block (raw CIM loaded?) ==");
        Console.ResetColor();
        Console.WriteLine("  Count: " + f.repeatedBlock.Count);
        if (f.repeatedBlock.Count > 0)
        {
            foreach (var x in f.repeatedBlock.GetRange(0, Math.Min(10, f.repeatedBlock.Count)))
                Console.WriteLine("    " + x);
        }
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("== 4. AES key schedule candidates ==");
        Console.ResetColor();
        if (f.aesKeyCandidates.Count == 0)
            Console.WriteLine("  No AES key schedules detected.\n");
        else
        {
            foreach (var x in f.aesKeyCandidates) Console.WriteLine("    " + x);
            Console.WriteLine();
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("== 5. CIM-related string locations ==");
        Console.ResetColor();
        foreach (var x in f.cimRelatedStrings) Console.WriteLine("    " + x);
        Console.WriteLine();

        // Interpretation
        Console.WriteLine("==================================================");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("INTERPRETATION:");
        Console.ResetColor();
        if (f.vhabcimPlaintext.Count > 1 || f.elfHeaders.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  GREAT NEWS: Decrypted firmware data found in Phocus memory!");
            Console.WriteLine("  This proves Phocus DOES decrypt the CIM.");
            Console.WriteLine("  Next step: dump those memory regions for analysis.");
            Console.ResetColor();
        }
        else if (f.repeatedBlock.Count > 0)
        {
            Console.WriteLine("  Phocus has the RAW (encrypted) CIM in memory but does not decrypt it.");
            Console.WriteLine("  This means decryption happens INSIDE the X2D camera SoC.");
            Console.WriteLine("  Software-only attack is not feasible from this PC.");
        }
        else
        {
            Console.WriteLine("  Phocus has not loaded the CIM yet.");
            Console.WriteLine("  Try opening the firmware update dialog first, then re-run.");
        }
    }

    static void Pause() { Console.WriteLine("\nPress any key to exit..."); Console.ReadKey(); }
}
