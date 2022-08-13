using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using TrionicCANLib.CAN;
using TrionicCANLib.API;
using TrionicCANLib;
using System.Windows.Forms;
using System.Collections;
using NLog;
using TrionicCANLib.Checksum;
using TrionicCANLib.SeedKey;
using FlasherSettings;

namespace TrionicCANLib.API
{
    public enum DiagnosticType : int
    {
        None,
        OBD2,
        EOBD,
        LOBD
    }

    public enum TankType : int
    {
        US,
        EU,
        AWD
    }

    public class DelcoE39 : ITrionic
    {
        private GMLAN gmlan = null;

        private enum mpc5566Mode : uint
        {
            modeBAM = 0, // Defunct. It must be compiled in this mode
            modeE39 = 1,
            modeE78 = 2
        };

        private Logger logger = LogManager.GetCurrentClassLogger();

        static public List<uint> FilterIdECU = new List<uint> { 0x7E0, 0x7E8 };

        public override bool FormatBootPartition
        {
            get { return formatBootPartition; }
            set { formatBootPartition = value; }
        }

        public override bool FormatSystemPartitions
        {
            get { return formatSystemPartitions; }
            set { formatSystemPartitions = value; }
        }

        private bool formatBootPartition = false;
        private bool formatSystemPartitions = false;
        private bool _stallKeepAlive;
        private const int maxRetries = 100;
        private const int timeoutP2ct = 150;
        private const int timeoutP2ce = 5000;
        private const uint e39e78LoaderBase = 0x40004000;

        private bool _testBool = false;
        public bool TestBool
        {
            get { return _testBool; }
            set { _testBool = value; }
        }
        private bool _testBool2 = false;
        public bool TestBool2
        {
            get { return _testBool2; }
            set { _testBool2 = value; }
        }
        private int _TestInt = 14;
        public int TestInt
        {
            get { return _TestInt; }
            set { _TestInt = value; }
        }

        // Out of bounds on purpose. Overflow
        private int _TestInt2 = 5;
        public int TestInt2
        {
            get { return _TestInt2; }
            set { _TestInt2 = value; }
        }

        // Out of bounds on purpose. Underflow
        private int _TestInt3 = -1;
        public int TestInt3
        {
            get { return _TestInt3; }
            set { _TestInt3 = value; }
        }

        // Individual settings for this target
        private class e39Settings : SettingProperties
        {
            private SettingProperty[] m_settings = new SettingProperty[]
            {
                new SettingProperty ( nameof(TestInt)  , null, "", "Farts" ),
                new SettingProperty ( nameof(TestInt2) , new Object[] { "Index 0", "Index 1", "Index 2" }, "", "Some name" ),
                new SettingProperty ( nameof(TestInt3) , new Object[] { "Index 0", "Index 1", "Index 2" }, "", "Another one" ),
                new SettingProperty ( nameof(TestBool) , null, "", "Play around with Farts" ),
                new SettingProperty ( nameof(TestBool2), null, "", "Click me" ),
            };

            public override ref SettingProperty[] Properties
            {
                get { return ref m_settings; }
            }
        }

        private SettingProperties m_e39Settings = new e39Settings();

        public override ref SettingProperties GetSettings(ECU ecu)
        {
            switch (ecu)
            {
                case ECU.DELCOE39:
                case ECU.DELCOE78:
                    return ref m_e39Settings;
            }

            return ref BaseSettings;
        }

        // This is beyond messy...
        public override void TargetSettingsLogic(ECU ecu, ref SettingsManager manager)
        {
            Object Var;

            if ((Var = manager.Get(nameof(TestInt), typeof(int))) != null)
            {
                bool State = true;
                if ((int)Var < -10 || (int)Var > 400)
                {
                    State = false;
                }

                manager.Enable(nameof(TestBool), State);
            }

            if ((Var = manager.Get(nameof(TestBool2), typeof(bool))) != null)
            {
                manager.Enable(nameof(TestInt3), (bool)Var);
            }
        }

        public void PrintSettings()
        {
            CastInfoEvent("testbool: " + _testBool.ToString(), ActivityType.ConvertingFile);
            CastInfoEvent("testbool2: " + _testBool2.ToString(), ActivityType.ConvertingFile);
            CastInfoEvent("Testint is " + _TestInt.ToString("D"), ActivityType.ConvertingFile);
            CastInfoEvent("Testint2 is " + _TestInt2.ToString("D"), ActivityType.ConvertingFile);
            CastInfoEvent("Testint3 is " + _TestInt3.ToString("D"), ActivityType.ConvertingFile);
        }

        // Individual features of targets
        private class e39features : TargetFeatures
        {
            public override bool FlashFull { get { return true; } }
            // public override bool FlashCalib { get { return true; } } // <- Needs more work in main
            public override bool ReadFull { get { return true; } }
            public override bool ReadCalib { get { return true; } }
            public override bool ReadSram { get { return true; } }
            public override bool FirmwareInfo { get { return true; } }
            public override bool TroubleCodes { get { return true; } }
        }

        private class e78features : TargetFeatures
        {
            public override bool FlashFull { get { return true; } }
            public override bool ReadFull { get { return true; } }
            public override bool TroubleCodes { get { return true; } }
        }

        private TargetFeatures e39feats = new e39features();
        private TargetFeatures e78feats = new e78features();

        public override ref TargetFeatures GetFeatures(ECU ecu)
        {
            switch (ecu)
            {
                case ECU.DELCOE39:
                    return ref e39feats;
                case ECU.DELCOE78:
                    return ref e78feats;
            }

            return ref BaseFeatures;
        }

        private void DefaultGmlan()
        {
            if (gmlan == null)
            {
                gmlan = new GMLAN(this);
            }

            // These settings should ideally be set on a per-target basis
            gmlan.TargetDeterminedDelays = true;
            gmlan.TesterId = 0x7E0;
            gmlan.TargetId = 0x7E8;
        }

        public DelcoE39()
        {
            // Just to verify there's only ONE instance created
            logger.Debug("e39 instance create");

            tmr.Elapsed += tmr_Elapsed;
            // m_ShouldUpdateChecksum = updateChecksum;
            gmlan = new GMLAN(this);

            DefaultGmlan();
        }

        public bool StallKeepAlive
        {
            get { return _stallKeepAlive; }
            set { _stallKeepAlive = value; }
        }

        private System.Timers.Timer tmr = new System.Timers.Timer(2000);
        private Stopwatch sw = new Stopwatch();
        private Stopwatch eraseSw = new Stopwatch();
        public ChecksumDelegate.ChecksumUpdate m_ShouldUpdateChecksum;

        void tmr_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (canUsbDevice.isOpen())
            {
                // send keep alive
                if (!_stallKeepAlive)
                {
                    logger.Debug("Send KA based on timer");
                    // Chriva
                    // SendKeepAlive();
                }
            }
        }

        override public void setCANDevice(CANBusAdapter adapterType)
        {
            if (adapterType == CANBusAdapter.LAWICEL)
            {
                canUsbDevice = new CANUSBDevice();
            }
            else if (adapterType == CANBusAdapter.ELM327)
            {
                Sleeptime = SleepTime.ELM327;
                canUsbDevice = new CANELM327Device() { ForcedBaudrate = m_forcedBaudrate };
            }
            else if (adapterType == CANBusAdapter.JUST4TRIONIC)
            {
                canUsbDevice = new Just4TrionicDevice() { ForcedBaudrate = m_forcedBaudrate };
            }
            else if (adapterType == CANBusAdapter.COMBI)
            {
                canUsbDevice = new LPCCANDevice();
            }
            else if (adapterType == CANBusAdapter.KVASER)
            {
                canUsbDevice = new KvaserCANDevice();
            }
            else if (adapterType == CANBusAdapter.J2534)
            {
                canUsbDevice = new J2534CANDevice();
            }

            canUsbDevice.bypassCANfilters = m_filterBypass;
            canUsbDevice.UseOnlyPBus = m_OnlyPBus;
            canUsbDevice.TrionicECU = ECU.DELCOE39;
            canUsbDevice.onReceivedAdditionalInformation += new ICANDevice.ReceivedAdditionalInformation(canUsbDevice_onReceivedAdditionalInformation);
            canUsbDevice.onReceivedAdditionalInformationFrame += new ICANDevice.ReceivedAdditionalInformationFrame(canUsbDevice_onReceivedAdditionalInformationFrame);
            if (canListener == null)
            {
                canListener = new CANListener();
            }
            canUsbDevice.addListener(canListener);
            canUsbDevice.AcceptOnlyMessageIds = FilterIdECU;
        }

        override public void SetSelectedAdapter(string adapter)
        {
            canUsbDevice.SetSelectedAdapter(adapter);
        }

        void canUsbDevice_onReceivedAdditionalInformation(object sender, ICANDevice.InformationEventArgs e)
        {
            CastInfoEvent(e.Info, ActivityType.ConvertingFile);
        }

        void canUsbDevice_onReceivedAdditionalInformationFrame(object sender, ICANDevice.InformationFrameEventArgs e)
        {
            CastFrameEvent(e.Message);
        }

        override public bool openDevice(bool requestSecurityAccess)
        {
            CastInfoEvent("Open called in ACDelco", ActivityType.ConvertingFile);
            MM_BeginPeriod(1);
            OpenResult openResult = OpenResult.OpenError;
            try
            {
                openResult = canUsbDevice.open();
            }
            catch (Exception e)
            {
                CastInfoEvent("Exception opening device " + e.ToString(), ActivityType.ConvertingFile);
            }

            if (openResult != OpenResult.OK)
            {
                CastInfoEvent("Open failed in ACDelco", ActivityType.ConvertingFile);
                canUsbDevice.close();
                MM_EndPeriod(1);
                return false;
            }

            // read some data ... 
            for (int i = 0; i < 10; i++)
            {
                CANMessage response = new CANMessage();
                response = canListener.waitMessage(50);
            }

            return true;
        }

        override public void Cleanup()
        {
            try
            {
                m_ECU = ECU.TRIONIC8;
                tmr.Stop();
                MM_EndPeriod(1);
                logger.Debug("Cleanup called in e39");
                //m_canDevice.removeListener(canListener);
                if (canListener != null)
                {
                    canListener.FlushQueue();
                }
                if (canUsbDevice != null)
                {
                    if (canUsbDevice is LPCCANDevice)
                    {
                        LPCCANDevice lpc = (LPCCANDevice)canUsbDevice;
                        lpc.disconnect();
                    }
                    canUsbDevice.close();
                    canUsbDevice = null;
                }
            }
            catch (Exception e)
            {
                logger.Debug(e.Message);
            }

            LogManager.Flush();
        }

        private void SendKeepAlive()
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 2);
            ulong cmd = 0x0000000000003E01; // always 2 bytes
            msg.setData(cmd);
            msg.elmExpectedResponses = 1;
            //logger.Debug("KA sent");
            canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return;
            }
            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = canListener.waitMessage(timeoutP2ct);
        }

        private void BroadcastKeepAlive101()
        {
            CANMessage msg = new CANMessage(0x101, 0, 2);
            ulong cmd = 0x0000000000003E01;
            msg.setData(cmd);
            msg.elmExpectedResponses = 1;

            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return;
            }
        }

        private bool UploadMPC5566Loader(uint address, mpc5566Mode mode)
        {
            int saved_progress = 0;
            uint blockSize = 0xF0;
            byte[] buf = new byte[blockSize];
            uint execAddr = address;
            uint bufPntr = 0;
            bool status = true;
            Stopwatch stopWatch = new Stopwatch();
            int msElapsed = 0;
            TimeSpan tSpent;

            Bootloader_mpc5566 m_bootloader = new TrionicCANLib.Bootloader_mpc5566();
            uint bytesLeft = (uint)m_bootloader.Bootloader_mpc5566Bytes.Length;
            uint totalBytes = (bytesLeft + 7) & ~7U;

            // Copy bootloader to local buffer for modification and padding
            byte[] bootloaderBytes = new byte[totalBytes];
            for (uint i = 0; i < bytesLeft; i++)
            {
                bootloaderBytes[i] = m_bootloader.Bootloader_mpc5566Bytes[i];
            }

            bytesLeft = totalBytes;

            bootloaderBytes[4] = (byte)((uint)mode >> 24);
            bootloaderBytes[5] = (byte)((uint)mode >> 16);
            bootloaderBytes[6] = (byte)((uint)mode >> 8);
            bootloaderBytes[7] = (byte)(uint)mode;

            SendKeepAlive();
            Thread.Sleep(50);

            // Why e39. Just why? 24-bit download request but a 32-bit transfer request
            if (!gmlan.RequestDownload(totalBytes, 24))
            {
                CastInfoEvent("Transfer not accepted", ActivityType.ConvertingFile);
                return false;
            }

            CastInfoEvent("Transfer accepted", ActivityType.ConvertingFile);

            CastInfoEvent("Uploading bootloader..", ActivityType.ConvertingFile);
            stopWatch.Start();

            while (bytesLeft > 0 && status)
            {
                uint thisLen = (bytesLeft > blockSize) ? blockSize : bytesLeft;

                tSpent = stopWatch.Elapsed;
                msElapsed = tSpent.Milliseconds;
                msElapsed += tSpent.Seconds * 1000;
                msElapsed += tSpent.Minutes * 60000;

                if (msElapsed >= 1000)
                {
                    stopWatch.Restart();
                    BroadcastKeepAlive101();
                }

                for (uint i = 0; i < thisLen; i++)
                {
                    buf[i] = bootloaderBytes[bufPntr++];
                }

                status = false;
                int tries = 5;

                while (!status && --tries > 0)
                {
                    status = gmlan.TransferData_32(buf, address, thisLen);
                }

                int percentage = (int)(((float)bufPntr * 100) / (float)totalBytes);
                if (percentage > saved_progress)
                {
                    CastProgressReadEvent(percentage);
                    saved_progress = percentage;
                }

                bytesLeft -= thisLen;
                address += thisLen;
            }

            if (status == true)
            {
                if (!(status = gmlan.TransferData_32(null, execAddr, 0, 0, true)))
                {
                    CastInfoEvent("ECU refused to start bootloader?", ActivityType.ConvertingFile);
                }
            }

            stopWatch.Stop();
            return status;
        }

        private byte[] dumpEx(out bool status, uint address, uint end)
        {
            uint length = end - address;
            uint start = address;
            int saved_progress = 0;
            uint retries = 5;
            uint bufPtr = 0;
            byte[] buf = new byte[length];
            status = false;
            bool success;

            CastProgressReadEvent(0);

            for (int i = 0; i < length; i++)
            {
                buf[i] = 0xFF;
            }

            while (address < end)
            {

                uint blockSize = 0xF0;
                if (address + blockSize > end)
                {
                    blockSize = end - address;
                }

                byte[] retdata = readMemoryByAddress32_mpc(address, blockSize, out success);

                if (success)
                {
                    retries = 5;
                    address += blockSize;
                    for (uint i = 0; i < blockSize; i++)
                    {
                        buf[bufPtr++] = retdata[i];
                    }
                    int percentage = (int)(((float)bufPtr * 100) / (float)length);
                    if (percentage > saved_progress)
                    {
                        CastProgressReadEvent(percentage);
                        saved_progress = percentage;
                    }
                }
                else
                {
                    if (retries == 0)
                    {
                        CastInfoEvent("Download failed", ActivityType.DownloadingFlash);
                        return null;
                    }
                    retries--;
                }
            }

            status = true;
            return buf;
        }

        private void LoaderRequestExit()
        {
            byte i = 10;
            CastInfoEvent("Requesting bootloader exit..", ActivityType.ConvertingFile);
            while (--i > 0)
            {
                if (gmlan.ReturnToNormal())
                {
                    return;
                }
                Thread.Sleep(100);
            }

            CastInfoEvent("Bootloader did not respond to exit-request", ActivityType.ConvertingFile);
            CastInfoEvent("You may have to power-cycle the ECU", ActivityType.ConvertingFile);
        }

        private void mpc5566ActDump(DoWorkEventArgs workEvent, string FileName, mpc5566Mode mode)
        {
            byte[] buf = new byte[0x300000 + 1024];
            uint bufPntr = 0;
            bool status;

            for (uint i = 0; i < buf.Length; i++)
            {
                buf[i] = 0xFF;
            }

            CastInfoEvent("Downloading flash..", ActivityType.DownloadingFlash);

            byte[] flash = dumpEx(out status, 0, 0x300000);

            if (flash == null)
            {
                CastInfoEvent("Download failed", ActivityType.FinishedDownloadingFlash);
                LoaderRequestExit();
                return;
            }

            for (uint i = 0; i < 0x300000; i++)
            {
                buf[bufPntr++] = flash[i];
            }


            CastInfoEvent("Downloading shadow..", ActivityType.DownloadingFlash);
            byte[] shadow = dumpEx(out status, 0x00FFFC00, 0x1000000);

            if (shadow != null)
            {
                for (uint i = 0; i < 1024; i++)
                {
                    buf[bufPntr++] = shadow[i];
                }
            }
            else
            {
                CastInfoEvent("Shadow could not be dumped so that part of the file will be 0xFF'ed..", ActivityType.FinishedDownloadingFlash);
            }

            workEvent.Result = true;
            LoaderRequestExit();

            try
            {
                File.WriteAllBytes(FileName, buf);
                Md5Tools.WriteMd5HashFromByteBuffer(FileName, buf);
                CastInfoEvent("Download done", ActivityType.FinishedDownloadingFlash);
                workEvent.Result = true;
            }
            catch (Exception e)
            {
                CastInfoEvent("Could not write file... " + e.Message, ActivityType.ConvertingFile);
                CastInfoEvent("Download failed", ActivityType.FinishedDownloadingFlash);
                workEvent.Result = false;
            }
        }

        private byte[] readMemoryByAddress32_mpc(uint address, uint len, out bool status)
        {
            status = false;

            if (len == 0)
            {
                return null;
            }

            gmlan.DataToSend[0] = 0x23;
            gmlan.DataToSend[1] = (byte)(address >> 24);
            gmlan.DataToSend[2] = (byte)(address >> 16);
            gmlan.DataToSend[3] = (byte)(address >> 8);
            gmlan.DataToSend[4] = (byte)address;
            gmlan.DataToSend[5] = (byte)(len >> 8);
            gmlan.DataToSend[6] = (byte)len;

            int retLen = gmlan.TransferFrame(7);

            // 63 XX XX XX XX .. .. CS CS
            if (retLen == (len + 7) && gmlan.ReadData[0] == 0x63)
            {
                uint checksum = 0;
                byte[] buf = new byte[len];
                for (int i = 0; i < len; i++)
                {
                    checksum += gmlan.ReadData[5 + i];
                    buf[i] = gmlan.ReadData[5 + i];
                }

                if (gmlan.ReadData[5 + len] == ((checksum >> 8) & 0xff) &&
                    gmlan.ReadData[6 + len] == (checksum & 0xff))
                {
                    status = true;
                    return buf;
                }
            }

            // FF-frame
            // 63 FF FF FF FF
            else if (retLen == 5 &&
                gmlan.ReadData[0] == 0x63 &&
                gmlan.ReadData[1] == 0xff && gmlan.ReadData[2] == 0xff &&
                gmlan.ReadData[3] == 0xff && gmlan.ReadData[4] == 0xff)
            {
                byte[] buf = new byte[len];
                for (int i = 0; i < len; i++)
                {
                    buf[i] = 0xff;
                }

                status = true;
                return buf;
            }

            return null;
        }

        // Transfer lz compressed data. A very.. custom request
        //
        // ST: Step. 0 for first frame. 1, 2, 3 .. .. fe ff, 1, 2, 3 .. for consecutive frames
        // AA: Address
        // EE: Final, extracted size once everything has been received
        // ..: Data, if any
        // CS: Checksum-16 (< > denotes where to stard and end)
        //
        // First frame:  37 < ST [AA AA AA AA] [EE EE EE EE] .. .. > [CS CS]
        // Consecutive:  37 < ST .. .. > [CS CS]
        private bool TransferData_Lz32(byte[] data, uint address, uint length, float StartPercent = 0, float TargetPercent = 0)
        {
            int tries = 5;
            int saved_progress = 0;
        retryLz:
            uint checksum = 0;
            int ExtraDataLen = 12;
            int lzLen = data.Length;
            int totLen = lzLen;
            int toCopy = (lzLen > 243) ? 243 : lzLen; // f3
            int lzStep = 0;
            uint lzPtr = 0;

            gmlan.DataToSend[0] = 0x37;
            gmlan.DataToSend[1] = (byte)lzStep++;
            gmlan.DataToSend[2] = (byte)(address >> 24);
            gmlan.DataToSend[3] = (byte)(address >> 16);
            gmlan.DataToSend[4] = (byte)(address >> 8);
            gmlan.DataToSend[5] = (byte)(address);
            gmlan.DataToSend[6] = (byte)(length >> 24);
            gmlan.DataToSend[7] = (byte)(length >> 16);
            gmlan.DataToSend[8] = (byte)(length >> 8);
            gmlan.DataToSend[9] = (byte)(length);

            for (uint i = 0; i < toCopy; i++)
            {
                gmlan.DataToSend[10 + i] = data[lzPtr++];
            }

            for (uint i = 0; i < (toCopy + 8); i++)
            {
                checksum += gmlan.DataToSend[2 + i];
            }

            // Append checksum
            gmlan.DataToSend[toCopy + 10] = (byte)(checksum >> 8);
            gmlan.DataToSend[toCopy + 11] = (byte)(checksum);

            while (lzLen > 0)
            {
                int retLen = gmlan.TransferFrame(toCopy + ExtraDataLen);

                // Programming error, abort asap
                // xx 7f 37 85
                if (retLen > 2 &&
                    gmlan.ReadData[0] == 0x7F &&
                    gmlan.ReadData[1] == 0x37 &&
                    gmlan.ReadData[2] == 0x85)
                {
                    CastInfoEvent("Target reported a unrecoverable programming error", ActivityType.UploadingFlash);
                    return false;
                }

                // Expect at least 01 77
                else if (retLen < 1 || gmlan.ReadData[0] != 0x77)
                {
                    Thread.Sleep(500);
                    if (tries-- > 0)
                    {
                        CastInfoEvent("Lz retry", ActivityType.UploadingFlash);
                        goto retryLz;
                    }
                    return false;
                }

                lzLen -= toCopy;

                if (TargetPercent > 0)
                {
                    int percentage = (int)((((float)(totLen - lzLen) * (TargetPercent - StartPercent)) / (float)totLen) + StartPercent);
                    if (percentage != saved_progress)
                    {
                        CastProgressWriteEvent(percentage);
                        saved_progress = percentage;
                    }
                }

                toCopy = (lzLen > 251) ? 251 : lzLen; // fb
                ExtraDataLen = 4;
                gmlan.DataToSend[1] = (byte)lzStep;

                // 0 indicates new frame so keep stepping within 1 - FF
                lzStep = (lzStep > 254) ? 1 : (lzStep + 1);

                for (uint i = 0; i < toCopy; i++)
                {
                    gmlan.DataToSend[2 + i] = data[lzPtr++];
                }

                checksum = 0;
                for (uint i = 0; i < (toCopy + 1); i++)
                {
                    checksum += gmlan.DataToSend[1 + i];
                }

                // Append checksum
                gmlan.DataToSend[toCopy + 2] = (byte)(checksum >> 8);
                gmlan.DataToSend[toCopy + 3] = (byte)(checksum);
            }

            return true;
        }

        // Service Hash md5                (0 : start : length)
        // Service Format                  (1 : mask  : ~mask)
        // Service Select storage module N (2 : N     : ~N)
        private bool StartRoutineById_mpc(uint service, uint param1, uint param2)
        {
            int tries = 5;

            gmlan.DataToSend[0] = 0x31;
            gmlan.DataToSend[1] = (byte)service;
            gmlan.DataToSend[2] = (byte)(param1 >> 24);
            gmlan.DataToSend[3] = (byte)(param1 >> 16);
            gmlan.DataToSend[4] = (byte)(param1 >> 8);
            gmlan.DataToSend[5] = (byte)(param1);
            gmlan.DataToSend[6] = (byte)(param2 >> 24);
            gmlan.DataToSend[7] = (byte)(param2 >> 16);
            gmlan.DataToSend[8] = (byte)(param2 >> 8);
            gmlan.DataToSend[9] = (byte)(param2);

            while (--tries > 0)
            {
                int retLen = gmlan.TransferFrame(10);
                if (retLen != 2)
                {
                    Thread.Sleep(500);
                    continue;
                }
                else if (gmlan.ReadData[0] != 0x71 || gmlan.ReadData[1] != service)
                {
                    Thread.Sleep(500);
                    continue;
                }
                return true;
            }

            return false;
        }

        private byte[] RequestRoutineResult_mpc(byte service, out bool success)
        {
            int tries = 5;

            success = false;

            gmlan.DataToSend[0] = 0x33;
            gmlan.DataToSend[1] = (byte)service;

            while (--tries > 0)
            {
                int retLen = gmlan.TransferFrame(2);

                if (retLen < 2)
                {
                    Thread.Sleep(150);
                    continue;
                }

                // Busy, come again
                else if (retLen > 2 &&
                    gmlan.ReadData[0] == 0x7f &&
                    gmlan.ReadData[1] == 0x33 &&
                    gmlan.ReadData[2] == 0x21)
                {
                    tries = 5;
                    Thread.Sleep(25);
                    continue;
                }

                if (gmlan.ReadData[0] == 0x73 &&
                    gmlan.ReadData[1] == service)
                {
                    success = true;

                    // 73 service ..
                    if (retLen > 2)
                    {
                        byte[] data = new byte[retLen - 1];
                        data[0] = (byte)(retLen - 2);

                        for (uint i = 0; i < (retLen - 2); i++)
                        {
                            data[1 + i] = gmlan.ReadData[2 + i];
                        }

                        return data;
                    }

                    return null;
                }

                // Other or unknown response
                Thread.Sleep(150);
            }

            return null;
        }

        private bool mpc5566Different(TargetParameters tparam, byte[] filebytes, uint partition, bool shutup)
        {
            uint[] part = tparam.PartitionToAddress(partition);
            uint fileStart = part[0];
            uint fileEnd = part[1];
            uint physStart = part[2];
            uint physEnd = part[3];

            // Debug..
            if (fileStart >= fileEnd || physStart >= physEnd || fileEnd > filebytes.Length)
            {
                CastInfoEvent("md5: Internal fault! Range out of bounds", ActivityType.ConvertingFile);
                return true;
            }

            if (!shutup)
            {
                CastInfoEvent("Hashing partition " + partition.ToString("D2") + " (" + physStart.ToString("X6")
                    + " - " + (physEnd - 1).ToString("X6") + ")", ActivityType.ConvertingFile);
            }

            bool success = StartRoutineById_mpc(0, physStart, physEnd - physStart);

            if (!success || part[1] == 0)
            {
                CastInfoEvent("Could not start md5 service", ActivityType.ConvertingFile);
                return true;
            }

            byte[] data = RequestRoutineResult_mpc(0, out success);

            if (data != null && data[0] == 16 && success)
            {
                byte[] tmpBuf = new byte[fileEnd - fileStart];
                ulong[] mLoc = { 0, 0 };
                ulong[] mRem = { 0, 0 };
                uint bufPtr = 0;

                System.Security.Cryptography.MD5CryptoServiceProvider md5 = new System.Security.Cryptography.MD5CryptoServiceProvider();
                md5.Initialize();

                for (uint i = fileStart; i < fileEnd; i++)
                {
                    tmpBuf[bufPtr++] = filebytes[i];
                }

                byte[] bLoc = md5.ComputeHash(tmpBuf);

                for (int i = 0; i < 8; i++)
                {
                    mRem[0] |= (ulong)data[1 + i] << ((7 - i) * 8);
                    mRem[1] |= (ulong)data[9 + i] << ((7 - i) * 8);
                    mLoc[0] |= (ulong)bLoc[/**/i] << ((7 - i) * 8);
                    mLoc[1] |= (ulong)bLoc[8 + i] << ((7 - i) * 8);
                }

                // CastInfoEvent("Remote hash: " + mRem[0].ToString("X16") + mRem[1].ToString("X16"), ActivityType.ConvertingFile);
                // CastInfoEvent("Local hash : " + mLoc[0].ToString("X16") + mLoc[1].ToString("X16"), ActivityType.ConvertingFile);
                return (mRem[0] != mLoc[0] || mRem[1] != mLoc[1]);

            }
            else
            {
                CastInfoEvent("Could not retrieve md5", ActivityType.ConvertingFile);
            }

            return true;
        }

        private byte[] ReadSRAM_32_24(uint address, uint length, uint blockSize)
        {
            byte[] buf = new byte[length];
            uint firstaddress = address;
            uint lastaddress = address + length;
            uint bufPntr = 0;
            uint defaultBlockSize = blockSize;
            int saved_progress = 0;

            Stopwatch stopWatch = new Stopwatch();

            for (uint i = 0; i < buf.Length; i++)
            {
                buf[i] = 0xFF;
            }

            stopWatch.Start();

            while (address < lastaddress)
            {
            SRAMRET:

                TimeSpan tSpent = stopWatch.Elapsed;
                int msElapsed = tSpent.Milliseconds;
                msElapsed += tSpent.Seconds * 1000;
                msElapsed += tSpent.Minutes * 60000;

                if (msElapsed > 1000)
                {
                    SendKeepAlive();
                    stopWatch.Restart();
                }

                byte[] data = gmlan.ReadMemoryByAddress_32_16(address, blockSize, blockSize);

                // Try once more but with smaller size
                if (data == null && blockSize > 4)
                {
                    blockSize = 4;
                    goto SRAMRET;
                }

                if (data != null)
                {
                    address += blockSize;
                    for (uint i = 0; i < blockSize; i++)
                    {
                        buf[bufPntr++] = data[i];
                    }

                    blockSize = defaultBlockSize;

                    if (address + blockSize > lastaddress)
                    {
                        blockSize = lastaddress - address;
                    }
                }
                else
                {
                    CastInfoEvent("Could not read address: 0x" + address.ToString("X"), ActivityType.ConvertingFile);
                    bufPntr += blockSize;
                    address += blockSize;
                }

                int percentage = (int)(((float)(address - firstaddress) * 100) / (float)(lastaddress - firstaddress));
                if (percentage > saved_progress)
                {
                    CastProgressReadEvent(percentage);
                    saved_progress = percentage;
                }

            }

            stopWatch.Stop();

            return buf;
        }

        // lock mask has priority over force mask since it's a safety feature
        private void mpc5566ActFlash(DoWorkEventArgs workEvent, string FileName, ECU target, uint lockMask, uint forceMask, bool recoverSes)
        {
            TargetParameters tparam = new TargetParameters(target);
            byte[] filebytes = File.ReadAllBytes(FileName);

            // Placeholder to prevent myself from doing stupid sh..
            if (filebytes.Length != 0x300000 + 1024 && filebytes.Length != 0x300000)
            {
                CastInfoEvent("Incorrect file size", ActivityType.ConvertingFile);
                return;
            }

            CastProgressWriteEvent(0);

            // Some paranoia for ya..
            CastInfoEvent("Verifying target compatibility..", ActivityType.ConvertingFile);

            byte[] procBytes = gmlan.ReadByIdentifier(0x92);
            if (procBytes == null || procBytes.Length != 4)
            {
                CastInfoEvent("Could not verify. Aborting..", ActivityType.ConvertingFile);

                // Do not exit in case this is a recovered session. User must've picked the wrong target
                if (!recoverSes)
                {
                    LoaderRequestExit();
                }
                return;
            }

            uint[] reqID = tparam.HardwareID();
            uint actID = (uint)(procBytes[0] << 24 | procBytes[1] << 16 |
                                procBytes[2] << 8 | procBytes[3]);
            string hwID = "Hardware ID: " + actID.ToString("X8");

            if (reqID[0] != (actID & reqID[1]))
            {
                hwID += " (incompatible). Aborting..";
                CastInfoEvent(hwID, ActivityType.ConvertingFile);

                // Do not exit in case this is a recovered session. User must've picked the wrong target
                if (!recoverSes)
                {
                    LoaderRequestExit();
                }
                return;
            }
            else
            {
                hwID += " (compatible)";
                CastInfoEvent(hwID, ActivityType.ConvertingFile);
            }

            // Must be manually enabled for now...
            formatBootPartition = false;

            // Only shadow is checked in here. It's up to the other functions to determine additional boot and system partitions
            // Reason: Same code is used on E39 and E78, they have different needs
            int maxPartition = (int)tparam.NumberOfPartitions();

            if (filebytes.Length < (0x300000 + 1024) || !formatBootPartition)
            {
                maxPartition = 28;
            }

            uint mask = 0;

            for (int i = 0; i < maxPartition; i++)
            {
                if (mpc5566Different(tparam, filebytes, (uint)i, false))
                {
                    mask |= (uint)1 << i;
                }
            }

            mask |= forceMask; // Append forced partitions
            mask &= ~lockMask; // Remove locked partitions

            // Recovery is looking for a few magic bytes. Make sure they are not there while flashing the main binary
            if (target == ECU.DELCOE39)
            {
                if ((mask & 0xFFFFF00) > 0)
                {
                    mask |= 0x8000100;
                }
            }

            // TODO:
            // Always backup shadow key, no matter which settings are used, sneak it in somewhere...
            // 
            // Verify health, if the key has 0000 or FFFF somewhere in it, clear mask and refuse to do anything unless the user let the flasher repair it
            // Set key to the default public key
            // Reason: If it contains one of those values, bam recovery is disabled even if you had the key. Brick the main firmware -ECU is junk
            // Check censor word combinations. Some of them will also disable BAM recovery (and nexus but that is of less importance)

            // Write main, verify it's working. THEN write shadow. You need at least one way to access the ecu if the worst happens
            // If you brick it and shadow is erased, it's bye bye. No means of recovery, throw the ECU in the bin
            if ((mask & 0x10000000) > 0 && (mask & 0xFFFFFFF) > 0)
            {
                CastInfoEvent(".", ActivityType.ConvertingFile);
                CastInfoEvent(".", ActivityType.ConvertingFile);
                CastInfoEvent("Shadow block is different!", ActivityType.ConvertingFile);
                CastInfoEvent("For your safety, it'll not be touched during this session", ActivityType.ConvertingFile);
                CastInfoEvent("If you really want to change it, flash the same file again after this one completed successfully", ActivityType.ConvertingFile);
                CastInfoEvent("If all the criterias are met, you'll be asked for a final confirmation", ActivityType.ConvertingFile);
                CastInfoEvent(".", ActivityType.ConvertingFile);
                CastInfoEvent(".", ActivityType.ConvertingFile);
                mask &= 0xFFFFFFF;
            }

            // Confirmation blah.. blah..
            if ((mask & 0x10000000) != 0)
            {
                CastInfoEvent("Shadow block is different!", ActivityType.ConvertingFile);
            }

            if (mask == 0)
            {
                CastInfoEvent("No need to flash, identical", ActivityType.ConvertingFile);
                LoaderRequestExit();
                workEvent.Result = true;
                return;
            }

            // Count total number of bytes to write. Only used for progress bar
            uint totLen = 0;
            for (int i = 0; i < maxPartition; i++)
            {
                if ((mask & (1 << i)) > 0)
                {
                    uint[] range = tparam.PartitionToAddress((uint)i);
                    totLen += range[1] - range[0];
                }
            }

            CastInfoEvent("Partition mask: " + mask.ToString("X8") + " (" + (totLen / 1024).ToString("D") + " kilobytes)", ActivityType.ConvertingFile);
            // LegionRequestexit();
            // workEvent.Result = true;
            // return;

            const uint defChunk = 128 * 1024;
            bool useCompression = true;
            bool restartLz = true;
            int saved_progress = 0;
            // uint inducedFault = 1;
            int partition = 0;
            uint totLoc = 0;
            uint actSent = 0;

            LZ77 lzComp = new LZ77();

            CastInfoEvent("Erasing..", ActivityType.ConvertingFile);
            if (StartRoutineById_mpc(1, mask, ~mask))
            {
                RequestRoutineResult_mpc(1, out bool success);
                if (!success)
                {
                    CastInfoEvent("Could not erase partitions to be written", ActivityType.ConvertingFile);
                    return;
                }
            }
            else
            {
                CastInfoEvent("Could not erase partitions to be written", ActivityType.ConvertingFile);
                return;
            }

            while (partition < maxPartition)
            {
                if ((mask & ((uint)1 << partition)) > 0)
                {
                    uint[] range = tparam.PartitionToAddress((uint)partition);
                    uint oldTotal = totLoc;
                    uint start = range[0];
                    uint end = range[1];
                    uint chunk = (defChunk + start > end) ? (end - start) : defChunk;

                    if (restartLz && useCompression)
                    {
                        if (!lzComp.lzQueueCompression(filebytes, start, (int)chunk))
                        {
                            CastInfoEvent("LZ write failed: Queue failure", ActivityType.ConvertingFile);
                            CastInfoEvent("Switching over to regular transfer", ActivityType.ConvertingFile);
                            useCompression = false;
                        }
                        restartLz = false;
                    }

                    uint physStart = (start >= 0x300000) ? ((start & 0x3FF) + 0xFFFC00) : start;
                    uint physEnd = physStart + (end - start);

                    CastInfoEvent("Writing: " + physStart.ToString("X6") + " - " + (physEnd - 1).ToString("X6"), ActivityType.ConvertingFile);

                    if (useCompression)
                    {
                        while (start < end && useCompression)
                        {
                            byte[] compressed = lzComp.lzRetrieveCompressedBytes();
                            if (compressed == null || compressed.Length < 2/* || inducedFault-- == 0*/)
                            {
                                CastInfoEvent("LZ write failed: Retrieve failure", ActivityType.ConvertingFile);
                                CastInfoEvent("Switching over to regular transfer", ActivityType.ConvertingFile);
                                useCompression = false;
                            }
                            else
                            {
                                // Check if another compression should be queued within this partition
                                uint oldstart = start;
                                uint oldlen = chunk;
                                start += chunk;
                                chunk = (chunk + start > end) ? (end - start) : chunk;

                                float StartPercent = ((float)totLoc * 100) / (float)totLen;
                                float TargetPercent = ((float)(totLoc + oldlen) * 100) / (float)totLen;

                                // Still data left to be sent to this partition?
                                if (chunk > 0)
                                {
                                    if (!lzComp.lzQueueCompression(filebytes, start, (int)chunk)/* || inducedFault-- == 0*/)
                                    {
                                        CastInfoEvent("LZ write failed: Retrieve failure", ActivityType.ConvertingFile);
                                        CastInfoEvent("Switching over to regular transfer", ActivityType.ConvertingFile);
                                        useCompression = false;
                                    }
                                }

                                // The next partition is to be written?
                                else if (partition < (maxPartition - 1) && (mask & ((uint)1 << (partition + 1))) > 0)
                                {
                                    uint[] rng2 = tparam.PartitionToAddress((uint)partition + 1);
                                    uint tmpR = (defChunk + rng2[0] > rng2[1]) ? (rng2[1] - rng2[0]) : defChunk;

                                    if (!lzComp.lzQueueCompression(filebytes, rng2[0], (int)tmpR)/* || inducedFault-- == 0*/)
                                    {
                                        CastInfoEvent("LZ write failed: Queue for next partition failure", ActivityType.ConvertingFile);
                                        CastInfoEvent("Switching over to regular transfer after the final frame has been sent", ActivityType.ConvertingFile);
                                        useCompression = false;

                                        // We work on file address, ECU works on physical address
                                        oldstart = (oldstart >= 0x300000) ? ((oldstart & 0x3FF) + 0xFFFC00) : oldstart;

                                        // Send previously queued data
                                        if (TransferData_Lz32(compressed, oldstart, oldlen, StartPercent, TargetPercent))
                                        {
                                            actSent += (uint)compressed.Length;
                                            // Verify written partition
                                            // if (true) // 
                                            if (!mpc5566Different(tparam, filebytes, (uint)partition, true)/* && inducedFault != 0*/)
                                            {
                                                CastInfoEvent("Verified current partition", ActivityType.ConvertingFile);
                                                // Increment to make sure regular write is performed on the NEXT partition
                                                oldTotal = rng2[0];
                                                partition++;
                                            }
                                            else
                                            {
                                                CastInfoEvent("Write verification of current partition failed", ActivityType.ConvertingFile);
                                            }
                                        }

                                        // No else, just switch over to regular transfer, format current partition and write the normal way
                                    }
                                }

                                // Last one or gap in mask? -You queue if need be
                                else
                                {
                                    restartLz = true;
                                }

                                if (useCompression)
                                {
                                    // We work on file address, ECU works on physical address
                                    oldstart = (oldstart >= 0x300000) ? ((oldstart & 0x3FF) + 0xFFFC00) : oldstart;

                                    // Send previously queued data
                                    if (!TransferData_Lz32(compressed, oldstart, oldlen, StartPercent, TargetPercent)/* || inducedFault-- == 0*/)
                                    {
                                        CastInfoEvent("LZ write failed: Transfer failure", ActivityType.ConvertingFile);
                                        CastInfoEvent("Switching over to regular transfer", ActivityType.ConvertingFile);
                                        useCompression = false;
                                    }
                                    else
                                    {
                                        actSent += (uint)compressed.Length;
                                        totLoc += oldlen;
                                    }
                                }
                            }
                        }

                        // Verify written partition
                        if (useCompression)
                        {
                            if (!mpc5566Different(tparam, filebytes, (uint)partition, true))
                            {
                                CastInfoEvent("Verified", ActivityType.ConvertingFile);
                                partition++;
                            }
                            else
                            {
                                CastInfoEvent("Write verification failed", ActivityType.ConvertingFile);
                                lzComp.lzTerminateQueue();
                                // restartLz = true;
                                totLoc = oldTotal;

                                // It's too early to trust this thing. No retries, just give up and use a known good method
                                // In doubt why? -Just check the loader; It's pure madness!
                                CastInfoEvent("Switching over to regular transfer", ActivityType.ConvertingFile);
                                useCompression = false;
                                restartLz = false;
                            }
                        }
                        // Recover from previous failures
                        else
                        {
                            lzComp.lzTerminateQueue();
                            restartLz = false;
                            totLoc = oldTotal;
                        }
                    }

                    // Do not use compression
                    else
                    {
                        while (start < end)
                        {
                            uint oldStart = (start >= 0x300000) ? ((start & 0x3FF) + 0xFFFC00) : start;
                            uint blockSize = end - start < 0xF0 ? end - start : 0xF0;
                            byte[] flashBuf = new byte[blockSize];
                            bool success = false;
                            uint retries = 5;

                            for (uint e = 0; e < blockSize; e++)
                                flashBuf[e] = filebytes[start++];

                            while (!success)
                            {
                                success = true;
                                for (uint f = 0; f < blockSize; f++)
                                {
                                    if (flashBuf[f] != 0xFF)
                                    {
                                        success = false;
                                        f = blockSize;
                                    }
                                }

                                if (!success)
                                {
                                    // Verified
                                    success = gmlan.TransferData_32(flashBuf, oldStart, blockSize, blockSize);
                                }

                                if (!success)
                                {
                                    if (--retries == 0)
                                    {
                                        CastInfoEvent("Write failed", ActivityType.ConvertingFile);
                                        return;
                                    }
                                }
                                else
                                {
                                    retries = 5;
                                    totLoc += blockSize;
                                    actSent += blockSize;
                                }

                                int percentage = (int)(((float)totLoc * 100) / (float)totLen);
                                if (percentage != saved_progress)
                                {
                                    CastProgressWriteEvent(percentage);
                                    saved_progress = percentage;
                                }
                            }
                        }

                        // Verify written partition
                        if (!mpc5566Different(tparam, filebytes, (uint)partition, true))
                        {
                            CastInfoEvent("Verified", ActivityType.ConvertingFile);
                            partition++;
                        }
                        else
                        {
                            CastInfoEvent("Write verification failed", ActivityType.ConvertingFile);
                            totLoc = oldTotal;
                        }
                    }
                }

                // Mask not set
                else
                {
                    partition++;
                }
            }

            // This thing is very much a joy-meter when using compression...
            CastProgressWriteEvent(100);
            LoaderRequestExit();

            // Simple debug. Fun to know how much lz helped
            CastInfoEvent("Sent a total of " + actSent.ToString("D") + " bytes to the target", ActivityType.ConvertingFile);
            CastInfoEvent("Write done", ActivityType.ConvertingFile);
            workEvent.Result = true;
        }

        private bool InitCommonE78(out bool recoveredSession)
        {
            bool success;
            uint delay = LegionOptions.InterframeDelay;

            recoveredSession = false;
            _stallKeepAlive = true;

            CastInfoEvent("Polling id 90 to determine state", ActivityType.ConvertingFile);
            string ident = gmlan.ReadByIdentifier(0x90, out success);

            if (ident != "MPC5566-LOADER: TXSUITE.ORG")
            {
                CastInfoEvent("Got: " + ident, ActivityType.ConvertingFile);
                SendKeepAlive();
                sw.Reset();
                sw.Start();
                gmlan.InitiateDiagnosticOperation(2);

                Thread.Sleep(50);

                if (!gmlan.SecurityAccess(ECU.DELCOE78, 1))
                {
                    return false;
                }

                SendKeepAlive();
                gmlan.DisableNormalCommunication();
                gmlan.ReportProgrammedState();
                gmlan.ProgrammingMode(1);
                gmlan.ProgrammingMode(3);

                Thread.Sleep(100);

                SendKeepAlive();
                if (!UploadMPC5566Loader(e39e78LoaderBase, mpc5566Mode.modeE78))
                    return false;

                ident = gmlan.ReadByIdentifier(0x90, out success);
                if (ident != "MPC5566-LOADER: TXSUITE.ORG")
                {
                    CastInfoEvent("Loader did not start as expected", ActivityType.ConvertingFile);
                    return false;
                }
            }
            else
            {
                recoveredSession = true;
                CastInfoEvent("Loader left running", ActivityType.ConvertingFile);
            }

            gmlan.WriteDataByIdentifier(new byte[] { (byte)(delay >> 8), (byte)delay }, 0x91);
            return true;
        }

        private bool InitCommonE39(out bool recoveredSession)
        {
            uint delay = LegionOptions.InterframeDelay;
            bool success;

            recoveredSession = false;
            _stallKeepAlive = true;

            CastInfoEvent("Polling id 90 to determine state", ActivityType.ConvertingFile);
            string ident = gmlan.ReadByIdentifier(0x90, out success);

            if (ident != "MPC5566-LOADER: TXSUITE.ORG")
            {
                CastInfoEvent("Got: " + ident, ActivityType.ConvertingFile);
                SendKeepAlive();
                sw.Reset();
                sw.Start();
                gmlan.InitiateDiagnosticOperation(4);

                if (!gmlan.SecurityAccess(ECU.DELCOE39, 1))
                {
                    return false;
                }

                SendKeepAlive();
                gmlan.DisableNormalCommunication();
                gmlan.ReportProgrammedState();
                gmlan.ProgrammingMode(1);
                gmlan.ProgrammingMode(3);

                Thread.Sleep(100);

                SendKeepAlive();
                if (!UploadMPC5566Loader(e39e78LoaderBase, mpc5566Mode.modeE39))
                {
                    return false;
                }

                ident = gmlan.ReadByIdentifier(0x90, out success);
                if (ident != "MPC5566-LOADER: TXSUITE.ORG")
                {
                    CastInfoEvent("Loader did not start as expected", ActivityType.ConvertingFile);
                    return false;
                }
            }
            else
            {
                recoveredSession = true;
                CastInfoEvent("Loader left running", ActivityType.ConvertingFile);
            }

            gmlan.WriteDataByIdentifier(new byte[] { (byte)(delay >> 8), (byte)delay }, 0x91);
            return true;
        }

        private void ReadFlashE39(DoWorkEventArgs workEvent, string FileName)
        {
            bool recoveredSession;

            if (!InitCommonE39(out recoveredSession))
            {
                return;
            }

            mpc5566ActDump(workEvent, FileName, mpc5566Mode.modeE39);
        }

        private void ReadFlashE78(DoWorkEventArgs workEvent, string FileName)
        {
            bool recoveredSession;

            if (!InitCommonE78(out recoveredSession))
            {
                return;
            }

            mpc5566ActDump(workEvent, FileName, mpc5566Mode.modeE78);
        }

        override public void ReadFlash(object sender, DoWorkEventArgs workEvent)
        {
            WorkerArgument arguments = (WorkerArgument)workEvent.Argument;
            workEvent.Result = false;

            DefaultGmlan();

            switch (arguments.ecu)
            {
                case ECU.DELCOE39:
                    ReadFlashE39(workEvent, arguments.FileName);
                    break;
                case ECU.DELCOE78:
                    ReadFlashE78(workEvent, arguments.FileName);
                    break;
                default:
                    CastInfoEvent("Unknown target for read", ActivityType.ConvertingFile);
                    break;
            }
        }

        private void ReadCalE39(DoWorkEventArgs workEvent, string FileName)
        {
            bool recoveredSession = false;
            bool status;

            if (!InitCommonE39(out recoveredSession))
            {
                return;
            }

            CastInfoEvent("Downloading calibration..", ActivityType.DownloadingFlash);

            byte[] buf = dumpEx(out status, 0x20000, 0x80000);

            LoaderRequestExit();

            if (buf == null)
            {
                CastInfoEvent("Download failed", ActivityType.FinishedDownloadingFlash);
                return;
            }

            try
            {
                File.WriteAllBytes(FileName, buf);
                Md5Tools.WriteMd5HashFromByteBuffer(FileName, buf);
                CastInfoEvent("Download done", ActivityType.FinishedDownloadingFlash);
                workEvent.Result = true;
            }
            catch (Exception e)
            {
                CastInfoEvent("Could not write file... " + e.Message, ActivityType.ConvertingFile);
                CastInfoEvent("Download failed", ActivityType.FinishedDownloadingFlash);
                workEvent.Result = false;
            }
        }

        public override void ReadCal(object sender, DoWorkEventArgs workEvent)
        {
            WorkerArgument arguments = (WorkerArgument)workEvent.Argument;
            workEvent.Result = false;

            DefaultGmlan();

            switch (arguments.ecu)
            {
                case ECU.DELCOE39:
                    ReadCalE39(workEvent, arguments.FileName);
                    break;
                default:
                    CastInfoEvent("Unknown target for read", ActivityType.ConvertingFile);
                    break;
            }
        }

        private void WriteFlashE39(DoWorkEventArgs workEvent, string FileName)
        {
            bool recoveredSession = false;
            uint lockedPartitions = (formatBootPartition == false) ? (uint)0x0000000F : (uint)0x00000000;
            uint forcedPartitions = 0x00000000;

            if (!InitCommonE39(out recoveredSession))
            {
                return;
            }

            mpc5566ActFlash(workEvent, FileName, ECU.DELCOE39, lockedPartitions, forcedPartitions, recoveredSession);
        }

        private void WriteFlashE78(DoWorkEventArgs workEvent, string FileName)
        {
            bool recoveredSession = false;
            uint lockedPartitions = 0;
            uint forcedPartitions = 0;

            // Translation: It must be re-verified after major changes to the loader
            CastInfoEvent("Currently disabled for your safety", ActivityType.ConvertingFile);
            return;

            if (!InitCommonE78(out recoveredSession))
            {
                return;
            }

            mpc5566ActFlash(workEvent, FileName, ECU.DELCOE78, lockedPartitions, forcedPartitions, recoveredSession);
        }

        // Remember to erase FF partitions!
        public override void WriteFlash(object sender, DoWorkEventArgs workEvent)
        {
            WorkerArgument arguments = (WorkerArgument)workEvent.Argument;
            workEvent.Result = false;

            DefaultGmlan();

            switch (arguments.ecu)
            {
                case ECU.DELCOE39:
                    WriteFlashE39(workEvent, arguments.FileName);
                    break;
                case ECU.DELCOE78:
                    WriteFlashE78(workEvent, arguments.FileName);
                    break;
                default:
                    CastInfoEvent("Unknown target for write", ActivityType.ConvertingFile);
                    break;
            }
        }

        public override void ReadTroubleCodes(object sender, DoWorkEventArgs workEvent)
        {
            WorkerArgument arguments = (WorkerArgument)workEvent.Argument;
            List<FailureRecord> records;
            workEvent.Result = false;

            DefaultGmlan();

            SendKeepAlive();

            if (gmlan.ReadFailureRecordIdentifiers(out records))
            {
                workEvent.Result = true;

                if (records.Count > 0)
                {
                    foreach (FailureRecord record in records)
                    {
                        CastInfoEvent("Record " + record.Number.ToString("D2") + ": " + record.Code.ToString("X04") +
                            " " + record.Type.ToString("X02"), ActivityType.QueryingTroubleCodes);
                    }

                    CastInfoEvent("No further codes", ActivityType.QueryingTroubleCodes);
                }
                else
                {
                    CastInfoEvent("No stored trouble codes", ActivityType.QueryingTroubleCodes);
                }
            }
            else
            {
                CastInfoEvent("Could not read trouble codes", ActivityType.QueryingTroubleCodes);
            }
        }

        private readonly List<ReadIdInfo> E39InfoList = new List<ReadIdInfo>
        {
            new ReadIdInfo { Id = 0x90, Type = InfoType.InfoTypeString  , Readable = "VIN                     " },
            // new ReadIdInfo { Id = 0x92, Type = InfoType.InfoTypeString  , Readable = "Supplier                " },
            // new ReadIdInfo { Id = 0x97, Type = InfoType.InfoTypeString  , Readable = "Name                    " },
            new ReadIdInfo { Id = 0x99, Type = InfoType.InfoTypeArray   , Readable = "Programming date        " },

            new ReadIdInfo { Id = 0xc0, Type = InfoType.InfoTypeU32     , Readable = "Boot software P/N       " },
            new ReadIdInfo { Id = 0xd0, Type = InfoType.InfoTypeString  , Readable = "Boot software Alphacode " },
            new ReadIdInfo { Id = 0xc1, Type = InfoType.InfoTypeU32     , Readable = "Main software P/N       " },
            new ReadIdInfo { Id = 0xd1, Type = InfoType.InfoTypeString  , Readable = "Main software Alphacode " },
            new ReadIdInfo { Id = 0xc2, Type = InfoType.InfoTypeU32     , Readable = "System calibration P/N  " },
            new ReadIdInfo { Id = 0xd2, Type = InfoType.InfoTypeString  , Readable = "System calib. Alphacode " },
            new ReadIdInfo { Id = 0xc3, Type = InfoType.InfoTypeU32     , Readable = "Fuel calibration P/N    " },
            new ReadIdInfo { Id = 0xd3, Type = InfoType.InfoTypeString  , Readable = "Fuel calib. Alphacode   " },
            new ReadIdInfo { Id = 0xc4, Type = InfoType.InfoTypeU32     , Readable = "Speedo calibration P/N  " },
            new ReadIdInfo { Id = 0xd4, Type = InfoType.InfoTypeString  , Readable = "Speedo calib. Alphacode " },
            new ReadIdInfo { Id = 0xc5, Type = InfoType.InfoTypeU32     , Readable = "Diag calibration P/N    " },
            new ReadIdInfo { Id = 0xd5, Type = InfoType.InfoTypeString  , Readable = "Diag calib. Alphacode   " },
            new ReadIdInfo { Id = 0xc6, Type = InfoType.InfoTypeU32     , Readable = "Engine calibration P/N  " },
            new ReadIdInfo { Id = 0xd6, Type = InfoType.InfoTypeString  , Readable = "Engine calib. Alphacode " },
            new ReadIdInfo { Id = 0xc9, Type = InfoType.InfoTypeU32     , Readable = "Module 9 P/N            " }, // What are these??
            new ReadIdInfo { Id = 0xd9, Type = InfoType.InfoTypeString  , Readable = "Module 9 Alphacode      " },
            new ReadIdInfo { Id = 0xca, Type = InfoType.InfoTypeU32     , Readable = "Module A P/N            " },
            new ReadIdInfo { Id = 0xda, Type = InfoType.InfoTypeString  , Readable = "Module A Alphacode      " },
            new ReadIdInfo { Id = 0xcb, Type = InfoType.InfoTypeU32     , Readable = "End model P/N           " },
            new ReadIdInfo { Id = 0xdb, Type = InfoType.InfoTypeString  , Readable = "End model P/N Alphacode " },
            new ReadIdInfo { Id = 0xcc, Type = InfoType.InfoTypeU32     , Readable = "Base model P/N          " },
            new ReadIdInfo { Id = 0xdc, Type = InfoType.InfoTypeString  , Readable = "Base model P/N Alphacode" },

            new ReadIdInfo { Id = 0xdf, Type = InfoType.InfoTypeU32     , Readable = "Distance traveled. Km   " },
            new ReadIdInfo { Id = 0x9a, Type = InfoType.InfoTypeArray   , Readable = "Diagnostic Ident.       " },
            new ReadIdInfo { Id = 0xa0, Type = InfoType.InfoTypeArray   , Readable = "Manufacturer Enable Cntr" },
            new ReadIdInfo { Id = 0xb0, Type = InfoType.InfoTypeArray   , Readable = "Diagnostic address      " },
            new ReadIdInfo { Id = 0x98, Type = InfoType.InfoTypeString  , Readable = "Tester S/N / Repair code" },
            new ReadIdInfo { Id = 0x9f, Type = InfoType.InfoTypeString  , Readable = "Previous Tester / Repair" },
            new ReadIdInfo { Id = 0xb4, Type = InfoType.InfoTypeString  , Readable = "Manufacturer trace str. " },
        };

        private readonly List<ReadIdInfo> E78InfoList = new List<ReadIdInfo>
        {
            new ReadIdInfo { Id = 0x90, Type = InfoType.InfoTypeString  , Readable = "VIN                     " },
        };

        public override void GetFirmwareInfo(object sender, DoWorkEventArgs workEvent)
        {
            WorkerArgument arguments = (WorkerArgument)workEvent.Argument;
            workEvent.Result = true;

            DefaultGmlan();

            switch (arguments.ecu)
            {
                case ECU.DELCOE39:
                    gmlan.ReportProgrammedState();
                    gmlan.ReadIdList(E39InfoList);
                    // gmlan.TryAllIds()();
                    break;
                case ECU.DELCOE78:
                    gmlan.ReportProgrammedState();
                    gmlan.ReadIdList(E78InfoList);
                    break;
                default:
                    workEvent.Result = true;
                    CastInfoEvent("Unknown target for information", ActivityType.ConvertingFile);
                    break;
            }
        }

        public override void ReadSram(object sender, DoWorkEventArgs workEvent)
        {
            WorkerArgument arguments = (WorkerArgument)workEvent.Argument;
            workEvent.Result = false;

            DefaultGmlan();

            switch (arguments.ecu)
            {
                case ECU.DELCOE39:
                    ReadSram_e39(workEvent, arguments.FileName, mpc5566Mode.modeE39);
                    break;
            }
        }

        private void ReadSram_e39(DoWorkEventArgs workEvent, string filename, mpc5566Mode mode)
        {
            CastInfoEvent("Downloading sram..", ActivityType.DownloadingFlash);

            SendKeepAlive();

            gmlan.InitiateDiagnosticOperation(4);

            if (!gmlan.SecurityAccess(ECU.DELCOE39, 1))
            {
                return;
            }

            SendKeepAlive();
            gmlan.DisableNormalCommunication();

            gmlan.TargetDelay = 0;
            gmlan.HostDelay = 0;
            gmlan.TargetDeterminedDelays = false;

            // It doesn't like dumping more than 16-20 bytes per transfer for some reason..
            byte[] secret = ReadSRAM_32_24(0x40000000, (128 * 1024), 16);

            if (secret == null)
            {
                CastInfoEvent("Download failed", ActivityType.FinishedDownloadingFlash);
                return;
            }

            try
            {
                File.WriteAllBytes(filename, secret);
                Md5Tools.WriteMd5HashFromByteBuffer(filename, secret);
                CastInfoEvent("Download done", ActivityType.FinishedDownloadingFlash);
                workEvent.Result = true;
            }
            catch (Exception e)
            {
                CastInfoEvent("Could not write file... " + e.Message, ActivityType.ConvertingFile);
                CastInfoEvent("Download failed", ActivityType.FinishedDownloadingFlash);
                workEvent.Result = false;
            }
        }
    }
}
