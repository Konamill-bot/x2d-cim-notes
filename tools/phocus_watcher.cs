using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Continuous watcher - polls Phocus memory every 2 seconds
// Looking for the encrypted CIM ciphertext signature being loaded
// When found, dumps the surrounding region to disk for analysis

class Phocus_Watcher
{
    const int PROCESS_VM_READ = 0x0010;
    const int PROCESS_QUERY_INFORMATION = 0x0400;

    [DllImport("kernel32.dll")]
    static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")]
    static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);
    [DllImport("kernel32.dll")]
    static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress,
        out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);
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

    const uint MEM_COMMIT = 0x1000;
    const uint PAGE_READWRITE = 0x04;
    const uint PAGE_EXECUTE_READWRITE = 0x40;

    // The encrypted ciphertext block we found in CIM at offset 0x200, 0x300, etc.
    // Original at 0x200 within CIM file
    static readonly byte[] CIM_SIGNATURE = {
        0x2E, 0xE3, 0x1A, 0x3B, 0xF4, 0xB6, 0x06, 0x25,
        0xD0, 0x52, 0x41, 0xB2, 0xCA, 0x9E, 0xED, 0xAF
    };

    // CIM file header signature (always plaintext - 7 bytes)
    static readonly byte[] CIM_HEADER = {
        0x56, 0x48, 0x41, 0x42, 0x43, 0x49, 0x4D // "VHABCIM"
    };

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("======================================");
        Console.WriteLine("  Phocus CIM Memory Watcher");
        Console.WriteLine("======================================\n");
        Console.WriteLine("This tool watches Phocus memory continuously.");
        Console.WriteLine("Run it BEFORE clicking Open on the CIM file.");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        string dumpDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "phocus_watcher_dumps");
        Directory.CreateDirectory(dumpDir);

        int iteration = 0;
        bool foundEncrypted = false;
        bool foundDecrypted = false;
        DateTime startTime = DateTime.Now;

        while (true)
        {
            iteration++;
            var procs = Process.GetProcessesByName("Phocus64");
            if (procs.Length == 0)
            {
                Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] Phocus64.exe not running. Waiting...");
                Thread.Sleep(3000);
                continue;
            }

            var proc = procs[0];
            IntPtr hProc = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, proc.Id);
            if (hProc == IntPtr.Zero)
            {
                Console.WriteLine("[ERROR] Cannot open Phocus. Run as Administrator!");
                Thread.Sleep(3000);
                continue;
            }

            int sigCount = 0;
            int headerCount = 0;
            long firstSigAddr = 0;
            long firstHeaderAddr = 0;

            IntPtr addr = IntPtr.Zero;
            IntPtr maxAddr = new IntPtr(0x7FFFFFFFFFFF);

            while (addr.ToInt64() < maxAddr.ToInt64())
            {
                MEMORY_BASIC_INFORMATION mbi;
                int r = VirtualQueryEx(hProc, addr, out mbi, new IntPtr(Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))));
                if (r == 0) break;

                long regionSize = mbi.RegionSize.ToInt64();
                bool isCommitted = mbi.State == MEM_COMMIT;
                bool isRW = mbi.Protect == PAGE_READWRITE || mbi.Protect == PAGE_EXECUTE_READWRITE;

                // Only scan writable heap (where CIM data would land)
                if (isCommitted && isRW && regionSize > 1024 * 1024 && regionSize < 512 * 1024 * 1024)
                {
                    byte[] buf = new byte[(int)Math.Min(regionSize, int.MaxValue)];
                    IntPtr read;
                    if (ReadProcessMemory(hProc, mbi.BaseAddress, buf, new IntPtr(buf.Length), out read))
                    {
                        int len = read.ToInt32();

                        // Search for encrypted CIM signature
                        for (int i = 0; i < len - 16; i++)
                        {
                            if (buf[i] == CIM_SIGNATURE[0] && buf[i+1] == CIM_SIGNATURE[1])
                            {
                                bool match = true;
                                for (int j = 2; j < 16; j++)
                                {
                                    if (buf[i+j] != CIM_SIGNATURE[j]) { match = false; break; }
                                }
                                if (match)
                                {
                                    sigCount++;
                                    if (firstSigAddr == 0) firstSigAddr = mbi.BaseAddress.ToInt64() + i;
                                }
                            }
                        }

                        // Search for VHABCIM in heap (decrypted firmware header)
                        for (int i = 0; i < len - 7; i++)
                        {
                            if (buf[i] == 0x56 && buf[i+1] == 0x48 && buf[i+2] == 0x41 &&
                                buf[i+3] == 0x42 && buf[i+4] == 0x43 && buf[i+5] == 0x49 && buf[i+6] == 0x4D)
                            {
                                long absAddr = mbi.BaseAddress.ToInt64() + i;
                                // Skip if in DLL code (0x7FF8... range)
                                if (absAddr < 0x7FF800000000L)
                                {
                                    headerCount++;
                                    if (firstHeaderAddr == 0) firstHeaderAddr = absAddr;
                                }
                            }
                        }

                        // If we found encrypted CIM signature, dump this region
                        if (sigCount > 0 && !foundEncrypted)
                        {
                            string dumpFile = Path.Combine(dumpDir, "ENCRYPTED_CIM_" + DateTime.Now.ToString("HHmmss") + ".bin");
                            File.WriteAllBytes(dumpFile, buf);
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine("\n[!!!] ENCRYPTED CIM detected at 0x" + (mbi.BaseAddress.ToInt64()).ToString("X12"));
                            Console.WriteLine("      Region size: " + (regionSize/1024/1024) + " MB");
                            Console.WriteLine("      Dumped to: " + dumpFile);
                            Console.ResetColor();
                            foundEncrypted = true;
                        }

                        // If we found VHABCIM in heap (not DLL), dump it
                        if (headerCount > 0 && !foundDecrypted && firstHeaderAddr > 0 && firstHeaderAddr < 0x7FF800000000L)
                        {
                            string dumpFile = Path.Combine(dumpDir, "DECRYPTED_CIM_" + DateTime.Now.ToString("HHmmss") + ".bin");
                            File.WriteAllBytes(dumpFile, buf);
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine("\n[!!!] DECRYPTED VHABCIM in heap at 0x" + firstHeaderAddr.ToString("X12"));
                            Console.WriteLine("      Region size: " + (regionSize/1024/1024) + " MB");
                            Console.WriteLine("      Dumped to: " + dumpFile);
                            Console.ResetColor();
                            foundDecrypted = true;
                        }
                    }
                }

                long next = mbi.BaseAddress.ToInt64() + regionSize;
                if (next <= addr.ToInt64()) break;
                addr = new IntPtr(next);
            }
            CloseHandle(hProc);

            TimeSpan elapsed = DateTime.Now - startTime;
            string status = "[" + DateTime.Now.ToString("HH:mm:ss") + "] iter " + iteration
                + " | encrypted sig: " + sigCount
                + " | heap VHABCIM: " + headerCount
                + " | elapsed: " + (int)elapsed.TotalSeconds + "s";

            if (sigCount > 0 || headerCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(status + " <-- HIT!");
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine(status);
            }

            if (foundEncrypted && foundDecrypted)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n[DONE] Both encrypted and decrypted CIM found. Dumps saved.");
                Console.ResetColor();
                Console.WriteLine("\nNext steps:");
                Console.WriteLine("  Files saved in: " + dumpDir);
                Console.WriteLine("  Send me the file sizes - we can compare to original CIM.");
                break;
            }

            Thread.Sleep(2000);
        }
        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }
}
