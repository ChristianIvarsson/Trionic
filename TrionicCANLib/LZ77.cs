using System;
using System.Diagnostics;
using System.Threading;

namespace TrionicCANLib
{
    // Since this code is doing its own "mutex locking", you can only queue ONE compression at a time, which is enough for its intended purpose
    class LZ77
    {
        static Thread lzThread = null;
        static byte[] m_compressedData;
        static volatile bool m_queuedCompression = false;
        static volatile uint m_compressedLen = 0;

        ~LZ77()
        {
            if (lzThread != null)
            {
                lzThread.Abort();
                lzThread.Join();
            }

            m_queuedCompression = false;
        }

        static void lzCompress(byte[] dataIn, out uint outPtr)
        {
            int length = dataIn.Length;
            // Workaround to ease boundary checks
            Array.Resize(ref dataIn, length + 64);
            // Exact length formula is.. unknown :/
            m_compressedData = new byte[(length * 2) + 1024];
            outPtr = 0;
            m_compressedData[outPtr++] = 0;
            int bytesLeft = length;
            byte mask = 0x80;
            uint flagPtr = 0;
            uint inPtr = 0;

            try
            {
                do
                {
                    // 15:12: Length
                    // 11:00: Offset
                    uint maxOfs = inPtr > 4096 ? 4096 : inPtr;
                    uint bestOfs = 0;
                    int bestLen = 2;

                    for (uint ofs = 3; ofs <= maxOfs; ofs++)
                    {
                        uint ptr2 = inPtr - ofs;
                        if (dataIn[inPtr/*      */] == dataIn[ptr2/*      */] &&
                            dataIn[inPtr + bestLen] == dataIn[ptr2 + bestLen])
                        {
                            uint maxLen = inPtr > 17 ? 17 : inPtr;
                            int minLen = 0;
                            for ( ; (minLen <= maxLen) && (dataIn[inPtr + minLen] == dataIn[ptr2 + minLen]); minLen++) ;

                            if (minLen > bestLen)
                            {
                                bestLen = minLen;
                                bestOfs = ofs;
                            }
                        }
                    }

                    if (bestLen > 2)
                    {
                        m_compressedData[flagPtr] |= mask;
                        m_compressedData[outPtr++] = (byte)((uint)(bestLen - 3) << 4 | (((bestOfs - 1) & 0xF00) >> 8));
                        m_compressedData[outPtr++] = (byte)(bestOfs - 1);
                        inPtr += (uint)bestLen;
                        bytesLeft -= bestLen;
                    }
                    else
                    {
                        m_compressedData[outPtr++] = dataIn[inPtr++];
                        bytesLeft--;
                    }

                    mask >>= 1;
                    if (mask == 0)
                    {
                        mask = 0x80;
                        flagPtr = outPtr++;
                        m_compressedData[flagPtr] = 0;
                    }
                }
                while (bytesLeft > 3);

                while (inPtr < length)
                {
                    mask >>= 1;
                    m_compressedData[outPtr++] = dataIn[inPtr++];

                    if (mask == 0)
                    {
                        mask = 0x80;
                        flagPtr = outPtr++;
                        m_compressedData[flagPtr] = 0;
                    }
                }

                while ((outPtr & 3) > 0)
                {
                    m_compressedData[outPtr++] = 0;
                }
            }

            catch (Exception)
            {
                outPtr = 0;
            }
        }

        static void lzThreadWorker(Object thPrm)
        {
            lzCompress((byte[])thPrm, out uint templen);
            m_compressedLen = templen;
            m_queuedCompression = false;
        }

        public bool lzQueueCompression(byte[] dataIn, uint ofs, int len)
        {
            // Too paranoid?
            if (dataIn == null || m_queuedCompression || len == 0)
            {
                return false;
            }

            int inlen = dataIn.Length;
            if (ofs + len > inlen)
            {
                return false;
            }

            byte[] m_dat = new byte[len];
            for (uint i = 0; i < len; i++)
            {
                m_dat[i] = dataIn[i + ofs];
            }

            m_queuedCompression = true;
            m_compressedLen = 0;
            lzThread = new Thread(lzThreadWorker);
            lzThread.Start(m_dat);
            return true;
        }

        public byte[] lzRetrieveCompressedBytes(int msTimeout)
        {
            Stopwatch stopWatch = new Stopwatch();
            int msElapsed = 0;
            TimeSpan tSpent;

            // Other thread is still compressing, sit here and wait
            stopWatch.Start();
            while (m_queuedCompression && (msElapsed <= msTimeout || msTimeout == 0))
            {
                tSpent = stopWatch.Elapsed;
                msElapsed  = tSpent.Milliseconds;
                msElapsed += tSpent.Seconds * 1000;
                msElapsed += tSpent.Minutes * 60000;
            }
            stopWatch.Stop();

            // Stop thread. 
            if (m_queuedCompression || m_compressedLen == 0)
            {
                lzThread.Abort();
                lzThread.Join();
                m_queuedCompression = false;
                return null;
            }

            lzThread.Join();
            m_queuedCompression = false;

            Array.Resize(ref m_compressedData, (int)m_compressedLen);
            return m_compressedData;
        }

        public byte[] lzExtract(byte[] dataIn, uint outLen)
        {
            // Ugly workaround but it does the job..
            Array.Resize(ref dataIn, dataIn.Length + 8);
            byte[] outBuf = new byte[outLen];
            uint outPtr = 0;
            uint inPtr = 0;

            try
            {
                while (outPtr < outLen)
                {
                    byte flg = dataIn[inPtr++];
                    for (uint n = 0x80; outPtr < outLen && n > 0; n = (n >> 1))
                    {
                        if ((flg & n) == 0)
                        {
                            outBuf[outPtr++] = dataIn[inPtr++];
                        }
                        else
                        {
                            uint len = (uint)(((dataIn[inPtr] >> 4) & 15) + 3);
                            uint ofs = (uint)(((dataIn[inPtr] & 15) << 8 | dataIn[inPtr + 1]) + 1);
                            inPtr += 2;
                            for (uint i = 0; i < len && outPtr < outLen; i++)
                            {
                                outBuf[outPtr] = outBuf[outPtr - ofs];
                                outPtr++;
                            }
                        }
                    }
                }
            }

            // Catch all
            catch (Exception)
            {
                return null;
            }

            return outBuf;
        }

        public void lzTerminateQueue()
        {
            lzThread.Abort();
            lzThread.Join();
            m_queuedCompression = false;
            m_compressedLen = 0;
        }

        public byte[] lzRetrieveCompressedBytes()
        {
            return lzRetrieveCompressedBytes(0);
        }

        public bool lzQueueCompression(byte[] dataIn)
        {
            return dataIn == null ? false : lzQueueCompression(dataIn, 0, dataIn.Length);
        }
    }
}
