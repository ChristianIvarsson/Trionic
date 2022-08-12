﻿using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using TrionicCANLib.Firmware;

namespace TrionicCANLib
{
    public class BlockManager
    {
        private int _blockNumber = 0;
        private long Len = 0;

        private string _filename = string.Empty;
        byte[] filebytes = null;

        public bool SetFilename(string filename)
        {
            if (File.Exists(filename))
            {
                FileInfo fi = new FileInfo(filename);

                if (fi.Length == FileT8.Length || fi.Length == FileT8mcp.Length)
                {
                    Len = fi.Length; 
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
    }
}
