﻿using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

namespace TrionicCANLib
{
    public class BlockManager
    {
        private uint checksum32 = 0;
        private int _blockNumber = 0;

        // Legion mod
        private long Len = 0;



        private string _filename = string.Empty;
        byte[] filebytes = null;

        public bool SetFilename(string filename)
        {
            if (File.Exists(filename))
            {
                FileInfo fi = new FileInfo(filename);
                // Legion mod
                // if (fi.Length == 0x100000) 
                if (fi.Length == 0x100000 || fi.Length == 0x40100)
                {
                    Len = fi.Length; /* I really hope this won't break stuff / Christian */
                    _filename = filename;
                    filebytes = File.ReadAllBytes(_filename);
                    return true;
                }
            }
            return false;
        }

        public byte[] GetNextBlock()
        {
            byte[] returnarray = GetCurrentBlock();
            _blockNumber++;
            return returnarray;
            
        }

        public byte[] GetCurrentBlock()
        {
            // get 0xEA bytes from the current file but return 0xEE bytes (7 * 34 = 238 = 0xEE bytes)
            // if the blocknumber is 0xF50 return less bytes because that is the last block
            int address = 0x020000 + _blockNumber * 0xEA;
            ByteCoder bc = new ByteCoder();
            if (_blockNumber == 0xF50)
            {
                byte[] array = new byte[0xE0];
                bc.ResetCounter();
                for (int byteCount = 0; byteCount < 0xE0; byteCount++)
                {
                    array[byteCount] = bc.codeByte(filebytes[address++]);
                }
                return array;
            }
            else
            {
                byte[] array = new byte[0xEE];
                bc.ResetCounter();
                for (int byteCount = 0; byteCount < 0xEA; byteCount++)
                {
                    array[byteCount] = bc.codeByte(filebytes[address++]);
                }
                return array;
            }
        }

        // Determine last part of the FLASH chip that is used (to save time when reading (DUMPing))
        // Address 0x020140 stores a pointer to the BIN file Header which is the last used area in FLASH
        public int GetLastBlockNumber()
        {
            int lastAddress = (int)filebytes[0x020141] << 16 | (int)filebytes[0x020142] << 8 | (int)filebytes[0x020143];
            // Add another 512 bytes to include header region (with margin)!!!
            lastAddress += 0x200;
            return (lastAddress - 0x020000) / 0xEA;
        }

        // Legion hacks
        public byte[] GetNextBlock_128()
        {
            byte[] returnarray = GetCurrentBlock_128();
            _blockNumber++;
            return returnarray;
        }

        public byte[] GetCurrentBlock_128()
        {
            int address = 0 + _blockNumber * 0x80;

            ByteCoder bc = new ByteCoder();

            byte[] array = new byte[0x86];
            bc.ResetCounter();
            for (int byteCount = 0; byteCount < 0x80; byteCount++)
            {
                array[byteCount] = bc.codeByte(filebytes[address++]);
            }
            return array;
        }


        public byte FFblock(int address)
        {
            int count = 0;

            for (int byteCount = 0; byteCount < 0x80; byteCount++)
            {
                if (address == Len)
                    break;
                if (filebytes[address] == 0xFF)
                    count++;

                address++;
            }

            if (count == 0x80)
                return 1;
            else
                return 0;
        }

        public uint GetChecksum32()
        {
            checksum32 = 0;
            long i;

            for (i = 0; i < Len; i++)
                checksum32 += (byte)filebytes[i];

            return checksum32;
        }





    }
}
