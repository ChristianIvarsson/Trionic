using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using NLog;
using TrionicCANLib.Firmware;

// Example header -This format is slightly different compared to the stuff in the main OS header!
// Address: 01_0010
// Modsum  ID         P/N as U32    Alpha code
// [54 F0] [00 63] 20 [00 C0 DD DA] [41 41]    00 02 00 00 00

// P/N ASCII
// [31 32 36 33 39 37 30 36]     00 00 00 00 00 00 00 00

// [B6 88] CRC 
// [01]    Tot ranges
// [03]    Number of CRC ranges
// [02]    Number of modsum ranges
// [00 00 40 00] -> [00 01 BF FF] // Total range (Ignored)
// [00 00 40 00] -> [00 00 FF FF] // CRC range 1
// [00 01 00 02] -> [00 01 00 1F] // CRC range 2
// [00 01 00 22] -> [00 01 BF FF] // CRC range 3
// [00 00 40 00] -> [00 00 FF FF] // Modsum range 1
// [00 01 00 02] -> [00 01 BF FF] // Modsum range 2

// [B7 F1] [00 63] 20 [00 C0 C0 DE] [43 42] 00 03 00 00 00
// [31 32 36 33 32 32 38 36]   00 00 00 00 00 00 00 00
// [A3 A2] [01] [03] [02]
// [00 00 40 00] -> [00 01 BF FF]
// [00 00 40 00] -> [00 00 FF FF]
// [00 01 00 02] -> [00 01 00 1F]
// [00 01 00 22] -> [00 01 BF FF]
// [00 00 40 00] -> [00 00 FF FF]
// [00 01 00 02] -> [00 01 BF FF]




namespace TrionicCANLib.Checksum
{
    public class ChecksumE39
    {
        private struct PartRange
        {
            public int start; // Start from
            public int end;   // Up to and include
            public int mod;   // Modsum location
            public int crc;   // CRC location
            public int hdr;   // Header
        };

        // These are stored directly after the modsum
        private readonly static int[] KnownIdentifiers =
        {
            0x0001, // Main OS
            0x0063, // Boot / recovery
            0x8102, // System cal
            0x8203, // Fuel cal
            0x8304, // Speedo cal
            0x8405, // Diag cal
            0x8506, // Engine cal
        };

        // Note about 3-byte / 4-byte desc:
        // 3-byte looks like:
        // 01: Total number of full ranges.
        // 03: Number of CRC ranges.
        // 02: Number of modsum-16 ranges.
        // 4-byte has an additional byte here. Sadly of unknown meaning.
        //
        // Directly after comes the ranges:
        // xx xx xx xx  -  xx xx xx xx  Full range 1
        // xx xx xx xx  -  xx xx xx xx  CRC range 1
        // xx xx xx xx  -  xx xx xx xx  CRC range 2
        // xx xx xx xx  -  xx xx xx xx  CRC range ..
        // xx xx xx xx  -  xx xx xx xx  Modsum range 1
        // xx xx xx xx  -  xx xx xx xx  Modsum range ..
        // The ranges are start from - to and include
        private readonly static PartRange[][] KnownRanges =
        {
            // "Type 1" SAAB ng9-5
            new PartRange[]
            {
                new PartRange { start = 0x004000, end = 0x01bfff, mod = 0x001000, crc = 0x001020, hdr = 0x010022 }, // Boot           (3-byte desc)
                new PartRange { start = 0x024000, end = 0x027FFF, mod = 0x024000, crc = 0x024020, hdr = 0x0CB056 }, // System cal     (4-byte)
                new PartRange { start = 0x028000, end = 0x02F7FF, mod = 0x028000, crc = 0x028020, hdr = 0x0CB07A }, // Fuel cal       (4-byte)
                new PartRange { start = 0x02F800, end = 0x02FFFF, mod = 0x02F800, crc = 0x02F820, hdr = 0x0CB09E }, // Speedo cal     (4-byte)
                new PartRange { start = 0x030000, end = 0x03FFFF, mod = 0x030000, crc = 0x030020, hdr = 0x0CB0C2 }, // Diag cal       (4-byte)
                new PartRange { start = 0x040000, end = 0x07FFFF, mod = 0x040000, crc = 0x040020, hdr = 0x0CB0E6 }, // Engine cal     (4-byte)
                new PartRange { start = 0x080000, end = 0x2FFFFF, mod = 0x0CB000, crc = 0x0CB020, hdr = 0x0CB022 }  // Main OS        (4-byte)
            },

            // "Type 2" No idea
            new PartRange[]
            {
                new PartRange { start = 0x004000, end = 0x01bfff, mod = 0x001000, crc = 0x001020, hdr = 0x010022 }, // Boot           (3-byte desc)
                new PartRange { start = 0x020000, end = 0x023FFF, mod = 0x020000, crc = 0x020020, hdr = 0x0CB056 }, // System cal     (4-byte)
                new PartRange { start = 0x024000, end = 0x0277FF, mod = 0x024000, crc = 0x024020, hdr = 0x0CB07A }, // Fuel cal       (4-byte)
                new PartRange { start = 0x027800, end = 0x027FFF, mod = 0x027800, crc = 0x027820, hdr = 0x0CB09E }, // Speedo cal     (4-byte)
                new PartRange { start = 0x028000, end = 0x037FFF, mod = 0x028000, crc = 0x028020, hdr = 0x0CB0C2 }, // Diag cal       (4-byte)
                new PartRange { start = 0x038000, end = 0x07FFFF, mod = 0x038000, crc = 0x038020, hdr = 0x0CB0E6 }, // Engine cal     (4-byte)
                new PartRange { start = 0x080000, end = 0x2FFFFF, mod = 0x0CB000, crc = 0x0CB020, hdr = 0x0CB022 }  // Main OS        (4-byte)
            },
        };









        private readonly static Logger logger = LogManager.GetCurrentClassLogger();

        public static ChecksumResult VerifyChecksum(string filename, bool autocorrect, ChecksumDelegate.ChecksumUpdate delegateShouldUpdate)
        {

            logger.Debug("Implement me you lazy bastard");
            return ChecksumResult.InvalidFileLength;
        }

        public static int GetChecksumAreaOffset(string filename)
        {
            int retval = 0;
            if (filename == "") return retval;
            FileStream fsread = new FileStream(filename, FileMode.Open, FileAccess.Read);
            using (BinaryReader br = new BinaryReader(fsread))
            {
                fsread.Seek(0x20140, SeekOrigin.Begin);
                retval = (int)br.ReadByte() * 256 * 256 * 256;
                retval += (int)br.ReadByte() * 256 * 256;
                retval += (int)br.ReadByte() * 256;
                retval += (int)br.ReadByte();
            }
            fsread.Close();
            return retval;
        }
    }
}
