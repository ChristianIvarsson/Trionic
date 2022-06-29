using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TrionicCANLib.API;

namespace TrionicCANLib
{
    class TargetParameters
    {
        private ECU SelectedTarget;

        // A target must be specified
        public TargetParameters(ECU target)
        {
            SelectedTarget = target;
        }

        // Has 3 M + 1 K
        private readonly uint[] mpc5566Partitions =
        {
            0x00000000, 0x00004000, 0x00010000, 0x0001C000, // Low space
            0x00020000, 0x00030000,
            0x00040000, 0x00060000,                         // Mid space
            0x00080000, 0x000A0000, 0x000C0000, 0x000E0000, // High space
            0x00100000, 0x00120000, 0x00140000, 0x00160000,
            0x00180000, 0x001A0000, 0x001C0000, 0x001E0000,
            0x00200000, 0x00220000, 0x00240000, 0x00260000,
            0x00280000, 0x002A0000, 0x002C0000, 0x002E0000,
            0x00300000, 0x00300400 // End / Shadow data
        };

        private readonly uint[] mpc5566HardwareID =
        {
            0x55660000, 0xFFFF0000
        };

        private uint[] mpc5566ParToAddr(uint partition)
        {
            uint[] MemoryMap = new uint[4] { 0, 0, 0, 0 };
            if (partition > 28)
                return MemoryMap;

            uint start = mpc5566Partitions[partition];
            uint length = mpc5566Partitions[partition + 1] - start;

            MemoryMap[0] = start;
            MemoryMap[1] = start + length;

            if (partition == 28)
                start = 0xFFFC00;

            MemoryMap[2] = start;
            MemoryMap[3] = start + length;

            return MemoryMap;
        }

        // partition counts from 0!
        // ret[0]: File address from
        // ret[1]: File address to
        // ret[2]: Physical address from
        // ret[3]: Physical address to
        public uint[] PartitionToAddress(uint partition)
        {
            switch (SelectedTarget)
            {
                case ECU.DELCOE39:
                case ECU.DELCOE78:
                    return mpc5566ParToAddr(partition);
                default:
                    return new uint[4] { 0, 0, 0, 0 };
            }
        }

        // ret[0]: Hardware ID
        // ret[1]: bitmask of what must match
        public uint[] HardwareID()
        {
            switch (SelectedTarget)
            {
                case ECU.DELCOE39:
                case ECU.DELCOE78:
                    return mpc5566HardwareID;
                default:
                    return new uint[2] { 0, 0 };
            }
        }

        public uint NumberOfPartitions()
        {
            switch (SelectedTarget)
            {
                case ECU.DELCOE39:
                case ECU.DELCOE78:
                    return 29;
                default:
                    return 0;
            }
        }
    }
}
