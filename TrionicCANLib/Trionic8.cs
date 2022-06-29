﻿using System;
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

    public class Trionic8 : ITrionic
    {
        AccessLevel _securityLevel = AccessLevel.AccessLevelFD; // by default 0xFD
        private Logger logger = LogManager.GetCurrentClassLogger();

        static public byte EcuByte_MCP = 5;
        static public byte EcuByte_T8 = 6;

        static public List<uint> FilterIdECU = new List<uint> { 0x7E0, 0x7E8 /*, 0x5E8*/, 1, 2, 3, 0x11, 0x12, 0x13 };
        static public List<uint> FilterIdRecovery = new List<uint> { 0x011, 0x311, 0x7E0, 0x7E8, 0x5E8 };
        static public List<uint> FilterIdCIM = new List<uint> { 0x245, 0x545, 0x645, 1, 2, 3, 0x11, 0x12, 0x13 };

        public AccessLevel SecurityLevel
        {
            get { return _securityLevel;  }
            set { _securityLevel = value; }
        }

        public bool FormatBootPartition
        {
            get { return formatBootPartition;  }
            set { formatBootPartition = value; }
        }

        public bool FormatSystemPartitions
        {
            get { return formatSystemPartitions;  }
            set { formatSystemPartitions = value; }
        }

        private bool formatBootPartition = false;
        private bool formatSystemPartitions = false;
        private CANListener m_canListener;
        private bool _stallKeepAlive;
        private float _oilQualityRead = 0;
        private const int maxRetries = 100;
        private const int timeoutP2ct = 150;
        private const int timeoutP2ce = 5000;
        private int Blockstoskip;

        public bool StallKeepAlive
        {
            get { return _stallKeepAlive; }
            set { _stallKeepAlive = value; }
        }

        private System.Timers.Timer tmr = new System.Timers.Timer(2000);
        private Stopwatch sw = new Stopwatch();
        private Stopwatch eraseSw = new Stopwatch();
        public ChecksumDelegate.ChecksumUpdate m_ShouldUpdateChecksum;

        public Trionic8()
        {
            tmr.Elapsed += tmr_Elapsed;
            m_ShouldUpdateChecksum = updateChecksum;
        }

        void tmr_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (canUsbDevice.isOpen())
            {
                // send keep alive
                if (!_stallKeepAlive)
                {
                    logger.Debug("Send KA based on timer");
                    SendKeepAlive();
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
            canUsbDevice.TrionicECU = ECU.TRIONIC8;
            canUsbDevice.onReceivedAdditionalInformation += new ICANDevice.ReceivedAdditionalInformation(canUsbDevice_onReceivedAdditionalInformation);
            canUsbDevice.onReceivedAdditionalInformationFrame += new ICANDevice.ReceivedAdditionalInformationFrame(canUsbDevice_onReceivedAdditionalInformationFrame);
            if (m_canListener == null)
            {
                m_canListener = new CANListener();
            }
            canUsbDevice.addListener(m_canListener);
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

        public bool openDevice(bool requestSecurityAccess)
        {
            CastInfoEvent("Open called in Trionic8", ActivityType.ConvertingFile);
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
                CastInfoEvent("Open failed in Trionic8", ActivityType.ConvertingFile);
                canUsbDevice.close();
                MM_EndPeriod(1);
                return false;
            }

            // read some data ... 
            for (int i = 0; i < 10; i++)
            {
                CANMessage response = new CANMessage();
                response = m_canListener.waitMessage(50);
            }

            if (requestSecurityAccess)
            {
                CastInfoEvent("Open succeeded in Trionic8", ActivityType.ConvertingFile);
                InitializeSession();
                CastInfoEvent("Session initialized", ActivityType.ConvertingFile);
                // read some data ... 
                for (int i = 0; i < 10; i++)
                {
                    CANMessage response = new CANMessage();
                    response = m_canListener.waitMessage(50);
                }
                bool _securityAccessOk = false;
                for (int i = 0; i < 3; i++)
                {
                    if (RequestSecurityAccess(0))
                    {
                        _securityAccessOk = true;
                        tmr.Start();
                        logger.Debug("Timer started");
                        break;
                    }
                }
                if (!_securityAccessOk)
                {
                    CastInfoEvent("Failed to get security access", ActivityType.ConvertingFile);
                    canUsbDevice.close();
                    MM_EndPeriod(1);
                    return false;
                }
                CastInfoEvent("Open successful", ActivityType.ConvertingFile);
            }
            return true;
        }

        private bool RequestSecurityAccessCIM(int millisecondsToWaitWithResponse)
        {
            int secondsToWait = millisecondsToWaitWithResponse / 1000;
            ulong cmd = 0x0000000000012702; // request security access
            CANMessage msg = new CANMessage(0x245, 0, 8);
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x645);
            CastInfoEvent("Requesting security access to CIM", ActivityType.ConvertingFile);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            //ulong data = response.getData();
            logger.Debug("---" + response.getData().ToString("X16"));
            if (response.getCanData(1) == 0x67)
            {
                if (response.getCanData(2) == 0x01)
                {
                    CastInfoEvent("Got seed value from CIM", ActivityType.ConvertingFile);
                    while (secondsToWait > 0)
                    {
                        CastInfoEvent("Waiting for " + secondsToWait.ToString() + " seconds...", ActivityType.UploadingBootloader);
                        Thread.Sleep(1000);
                        SendKeepAlive();
                        secondsToWait--;

                    }
                    byte[] seed = new byte[2];
                    seed[0] = response.getCanData(3);
                    seed[1] = response.getCanData(4);
                    if (seed[0] == 0x00 && seed[1] == 0x00)
                    {
                        return true; // security access was already granted
                    }
                    else
                    {
                        SeedToKey s2k = new SeedToKey();
                        byte[] key = s2k.calculateKeyForCIM(seed);
                        CastInfoEvent("Security access CIM : Key (" + key[0].ToString("X2") + key[1].ToString("X2") + ") calculated from seed (" + seed[0].ToString("X2") + seed[1].ToString("X2") + ")", ActivityType.ConvertingFile);

                        ulong keydata = 0x0000000000022704;
                        ulong key1 = key[1];
                        key1 *= 0x100000000;
                        keydata ^= key1;
                        ulong key2 = key[0];
                        key2 *= 0x1000000;
                        keydata ^= key2;
                        msg = new CANMessage(0x245, 0, 8);
                        msg.setData(keydata);
                        m_canListener.setupWaitMessage(0x645);
                        if (!canUsbDevice.sendMessage(msg))
                        {
                            CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                            return false;
                        }
                        response = new CANMessage();
                        response = m_canListener.waitMessage(timeoutP2ct);
                        // is it ok or not
                        if (response.getCanData(1) == 0x67 && response.getCanData(2) == 0x02)
                        {
                            CastInfoEvent("Security access to CIM granted", ActivityType.ConvertingFile);
                            return true;
                        }
                        else if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x27)
                        {
                            CastInfoEvent("Error: " + TranslateErrorCode(response.getCanData(3)), ActivityType.ConvertingFile);
                        }
                    }

                }
                else if (response.getCanData(2) == 0x02)
                {
                    CastInfoEvent("Security access to CIM granted", ActivityType.ConvertingFile);
                    return true;
                }
            }
            else if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x27)
            {
                CastInfoEvent("Error: " + TranslateErrorCode(response.getCanData(3)), ActivityType.ConvertingFile);
            }
            return false;
        }

        private bool RequestSecurityAccess(int millisecondsToWaitWithResponse)
        {
            int secondsToWait = millisecondsToWaitWithResponse / 1000;
            ulong cmd = 0x0000000000FD2702; // request security access
            if (_securityLevel == AccessLevel.AccessLevel01)
            {
                cmd = 0x0000000000012702; // request security access
            }
            else if (_securityLevel == AccessLevel.AccessLevelFB)
            {
                cmd = 0x0000000000FB2702; // request security access
            }
            CANMessage msg = new CANMessage(0x7E0, 0, 3); 
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            CastInfoEvent("Requesting security access", ActivityType.ConvertingFile);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);

            //ulong data = response.getData();
            logger.Debug("---" + response.getData().ToString("X16"));
            if (response.getCanData(1) == 0x67)
            {
                if (response.getCanData(2) == 0xFD || response.getCanData(2) == 0xFB || response.getCanData(2) == 0x01)
                {
                    CastInfoEvent("Got seed value from ECU", ActivityType.ConvertingFile);

                    while (secondsToWait > 0)
                    {
                        CastInfoEvent("Waiting for " + secondsToWait.ToString() + " seconds...", ActivityType.UploadingBootloader);
                        Thread.Sleep(1000);
                        SendKeepAlive();
                        secondsToWait--;
                    }

                    byte[] seed = new byte[2];
                    seed[0] = response.getCanData(3);
                    seed[1] = response.getCanData(4);
                    if (seed[0] == 0x00 && seed[1] == 0x00)
                    {
                        return true; // security access was already granted
                    }
                    else
                    {
                        SeedToKey s2k = new SeedToKey();

                        byte[] key = new byte[2];
                        if (m_ECU == ECU.TRIONIC8 || m_ECU == ECU.TRIONIC8_MCP || m_ECU == ECU.Z22SEMain_LEG || m_ECU == ECU.Z22SEMCP_LEG)
                        {
                            key = s2k.calculateKey(seed, _securityLevel);
                        }
                        else if (m_ECU == ECU.MOTRONIC96)
                        {
                            key = s2k.calculateKeyForME96(seed);
                        }
                        CastInfoEvent("Security access : Key (" + key[0].ToString("X2") + key[1].ToString("X2") + ") calculated from seed (" + seed[0].ToString("X2") + seed[1].ToString("X2") + ")", ActivityType.ConvertingFile);

                        ulong keydata = 0x0000000000FE2704;
                        if (_securityLevel == AccessLevel.AccessLevel01)
                        {
                            keydata = 0x0000000000022704;
                        }
                        else if (_securityLevel == AccessLevel.AccessLevelFB)
                        {
                            keydata = 0x0000000000FC2704;
                        }
                        ulong key1 = key[1];
                        key1 *= 0x100000000;
                        keydata ^= key1;
                        ulong key2 = key[0];
                        key2 *= 0x1000000;
                        keydata ^= key2;
                        msg = new CANMessage(0x7E0, 0, 5);
                        msg.setData(keydata);
                        m_canListener.setupWaitMessage(0x7E8);
                        if (!canUsbDevice.sendMessage(msg))
                        {
                            CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                            return false;
                        }
                        response = new CANMessage();
                        response = m_canListener.waitMessage(timeoutP2ct);
                        logger.Debug("---" + response.getData().ToString("X16"));
                        // is it ok or not
                        if (response.getCanData(1) == 0x67 && (response.getCanData(2) == 0xFE || response.getCanData(2) == 0xFC || response.getCanData(2) == 0x02))
                        {
                            CastInfoEvent("Security access granted", ActivityType.ConvertingFile);
                            return true;
                        }
                        else if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x27)
                        {
                            CastInfoEvent("Error: " + TranslateErrorCode(response.getCanData(3)), ActivityType.ConvertingFile);
                        }
                    }

                }
                else if (response.getCanData(2) == 0xFE || response.getCanData(2) == 0x02)
                {
                    CastInfoEvent("Security access granted", ActivityType.ConvertingFile);
                    return true;
                }
            }
            else if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x27)
            {
                CastInfoEvent("Error: " + TranslateErrorCode(response.getCanData(3)), ActivityType.ConvertingFile);
            }
            return false;
        }

        private string TranslateErrorCode(byte p)
        {
            string retval = "code " + p.ToString("X2");
            switch (p)
            {
                case 0x10:
                    retval = "General reject";
                    break;
                case 0x11:
                    retval = "Service not supported";
                    break;
                case 0x12:
                    retval = "subFunction not supported - invalid format";
                    break;
                case 0x21:
                    retval = "Busy, repeat request";
                    break;
                case 0x22:
                    retval = "conditions not correct or request sequence error";
                    break;
                case 0x23:
                    retval = "Routine not completed or service in progress";
                    break;
                case 0x31:
                    retval = "Request out of range or session dropped";
                    break;
                case 0x33:
                    retval = "Security access denied";
                    break;
                case 0x35:
                    retval = "Invalid key supplied";
                    break;
                case 0x36:
                    retval = "Exceeded number of attempts to get security access";
                    break;
                case 0x37:
                    retval = "Required time delay not expired, you cannot gain security access at this moment";
                    break;
                case 0x40:
                    retval = "Download (PC -> ECU) not accepted";
                    break;
                case 0x41:
                    retval = "Improper download (PC -> ECU) type";
                    break;
                case 0x42:
                    retval = "Unable to download (PC -> ECU) to specified address";
                    break;
                case 0x43:
                    retval = "Unable to download (PC -> ECU) number of bytes requested";
                    break;
                case 0x50:
                    retval = "Upload (ECU -> PC) not accepted";
                    break;
                case 0x51:
                    retval = "Improper upload (ECU -> PC) type";
                    break;
                case 0x52:
                    retval = "Unable to upload (ECU -> PC) for specified address";
                    break;
                case 0x53:
                    retval = "Unable to upload (ECU -> PC) number of bytes requested";
                    break;
                case 0x71:
                    retval = "Transfer suspended";
                    break;
                case 0x72:
                    retval = "Transfer aborted";
                    break;
                case 0x74:
                    retval = "Illegal address in block transfer";
                    break;
                case 0x75:
                    retval = "Illegal byte count in block transfer";
                    break;
                case 0x76:
                    retval = "Illegal block transfer type";
                    break;
                case 0x77:
                    retval = "Block transfer data checksum error";
                    break;
                case 0x78:
                    retval = "Response pending";
                    break;
                case 0x79:
                    retval = "Incorrect byte count during block transfer";
                    break;
                case 0x80:
                    retval = "Service not supported in current diagnostics session";
                    break;
                case 0x81:
                    retval = "Scheduler full";
                    break;
                case 0x83:
                    retval = "Voltage out of range";
                    break;
                case 0x85:
                    retval = "General programming failure";
                    break;
                case 0x89:
                    retval = "Device type error";
                    break;
                case 0x99:
                    retval = "Ready for download";
                    break;
                case 0xE3:
                    retval = "DeviceControl Limits Exceeded";
                    break;
            }
            return retval;
        }

        /// <summary>
        /// Cleans up connections and resources
        /// </summary>
        override public void Cleanup()
        {
            try
            {
                m_ECU = ECU.TRIONIC8;
                tmr.Stop();
                MM_EndPeriod(1);
                logger.Debug("Cleanup called in Trionic8");
                //m_canDevice.removeListener(m_canListener);
                if (m_canListener != null)
                {
                    m_canListener.FlushQueue();
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

        public string RequestECUInfoAsString(uint _pid)
        {
            return RequestECUInfoAsString(_pid, -1);
        }

        public string RequestECUInfoAsString(uint _pid, int expectedResponses)
        {
            string retval = string.Empty;
            byte[] rx_buffer = new byte[1024];
            int rx_pnt = 0;

            if (canUsbDevice.isOpen())
            {
                ulong cmd = 0x0000000000001A02 | _pid << 16;
                //SendMessage(data);  // software version
                CANMessage msg = new CANMessage(0x7E0, 0, 3); // test GS was 8
                msg.setData(cmd);
                msg.elmExpectedResponses = expectedResponses;
                m_canListener.setupWaitMessage(0x7E8);
                if (!canUsbDevice.sendMessage(msg))
                {
                    CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                    return string.Empty;
                }

                int msgcnt = 0;
                bool _success = false;
                CANMessage response = new CANMessage();
                ulong data = 0;
                int timeout = timeoutP2ct;
                while (!_success && msgcnt < 2)
                {
                    response = new CANMessage();
                    response = m_canListener.waitMessage(timeout);
                    data = response.getData();
                    //RequestCorrectlyReceived-ResponsePending
                    if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x1A && response.getCanData(3) == 0x78)
                    {
                        logger.Debug("RequestCorrectlyReceived-ResponsePending");
                        timeout *= 3;
                    }
                    else if (data == 0)
                    {
                        logger.Debug("Received blank message while waiting for data");
                    }
                    else if (response.getCanData(1) != 0x7E)
                    {
                        _success = true;
                        msgcnt++;
                    }
                }

                if (response.getCanData(1) == 0x5A)
                {
                    // only one frame in this response
                    byte canLength = response.getCanData(0);
                    for (uint fi = 3; fi <= canLength; fi++)
                    {
                        rx_buffer[rx_pnt++] = response.getCanData(fi);
                    }
                    retval = Encoding.ASCII.GetString(rx_buffer, 0, rx_pnt - 1);
                }
                else if (response.getCanData(2) == 0x5A)
                {
                    SendAckMessageT8();
                    byte len = response.getCanData(1);
                    int m_nrFrameToReceive = ((len - 4) / 8);
                    if ((len - 4) % 8 > 0) m_nrFrameToReceive++;
                    int lenthisFrame = len;
                    if (lenthisFrame > 4) lenthisFrame = 4;
                    for (uint fi = 4; fi < 4 + lenthisFrame; fi++)
                        rx_buffer[rx_pnt++] = response.getCanData(fi);
                    // wait for more records now

                    while (m_nrFrameToReceive > 0)
                    {
                        m_canListener.setupWaitMessage(0x7E8);
                        response = new CANMessage();
                        response = m_canListener.waitMessage(timeoutP2ct);
                        //RequestCorrectlyReceived-ResponsePending
                        if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x1A && response.getCanData(3) == 0x78)
                        {
                            CastInfoEvent("RequestCorrectlyReceived-ResponsePending", ActivityType.UploadingFlash);
                        }
                        else if (data == 0)
                        {
                            logger.Debug("Received blank message while waiting for data");
                        }
                        else if (response.getCanData(1) != 0x7E)
                        {
                            m_nrFrameToReceive--;
                            data = response.getData();
                            // add the bytes to the receive buffer
                            for (uint fi = 1; fi < 8; fi++)
                            {
                                if (rx_pnt < rx_buffer.Length) // prevent overrun
                                {
                                    rx_buffer[rx_pnt++] = response.getCanData(fi);
                                }
                            }
                        }
                    }
                    retval = Encoding.ASCII.GetString(rx_buffer, 0, rx_pnt - 1);
                }
                else if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x27)
                {
                    CastInfoEvent("Error: " + TranslateErrorCode(response.getCanData(3)), ActivityType.ConvertingFile);
                }
            }
            Thread.Sleep(25);

            return retval;
        }

        public string RequestCIMInfo(uint _pid)
        {
            string retval = string.Empty;
            byte[] rx_buffer = new byte[128];
            int rx_pnt = 0;

            if (canUsbDevice.isOpen())
            {
                // pid=2
                // 17-12-2012 17:15:51.239 - TX: 0245 0000000000021A02
                // 17-12-2012 17:15:51.298 - RX: 0645 00000000311A7F03
                // pid=3
                // 17-12-2012 17:16:41.190 - TX: 0245 0000000000031A02
                // 17-12-2012 17:16:41.238 - RX: 0645 00000000311A7F03

                ulong cmd = 0x0000000000001A02 | _pid << 16;
                //SendMessage(data);  // software version
                CANMessage msg = new CANMessage(0x245, 0, 8);
                msg.setData(cmd);
                m_canListener.setupWaitMessage(0x645);
                if (!canUsbDevice.sendMessage(msg))
                {
                    CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                    return string.Empty;
                }

                int msgcnt = 0;
                bool _success = false;
                CANMessage response = new CANMessage();
                ulong data = 0;
                while (!_success && msgcnt < 2)
                {
                    response = new CANMessage();
                    response = m_canListener.waitMessage(timeoutP2ct);
                    data = response.getData();
                    //RequestCorrectlyReceived-ResponsePending
                    if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x1A && response.getCanData(3) == 0x78)
                    {
                        //CastInfoEvent("RequestCorrectlyReceived-ResponsePending", ActivityType.UploadingFlash);
                    }
                    else if (response.getCanData(1) != 0x7E) _success = true;
                    msgcnt++;
                }

                //CANMessage response = new CANMessage();
                //response = m_canListener.waitMessage(timeoutPTct);
                //ulong data = response.getData();
                if (response.getCanData(1) == 0x5A)
                {
                    // only one frame in this repsonse

                    for (uint fi = 3; fi < 8; fi++) rx_buffer[rx_pnt++] = response.getCanData(fi);
                    retval = Encoding.ASCII.GetString(rx_buffer, 0, rx_pnt);
                }
                else if (response.getCanData(2) == 0x5A)
                {
                    SendAckMessageCIM();
                    byte len = response.getCanData(1);
                    int m_nrFrameToReceive = ((len - 4) / 8);
                    if ((len - 4) % 8 > 0) m_nrFrameToReceive++;
                    int lenthisFrame = len;
                    if (lenthisFrame > 4) lenthisFrame = 4;
                    for (uint fi = 4; fi < 4 + lenthisFrame; fi++) rx_buffer[rx_pnt++] = response.getCanData(fi);
                    // wait for more records now

                    while (m_nrFrameToReceive > 0)
                    {
                        m_canListener.setupWaitMessage(0x645);
                        response = new CANMessage();
                        response = m_canListener.waitMessage(timeoutP2ct);
                        //RequestCorrectlyReceived-ResponsePending
                        if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x1A && response.getCanData(3) == 0x78)
                        {
                            //CastInfoEvent("RequestCorrectlyReceived-ResponsePending", ActivityType.UploadingFlash);
                        }
                        else if (response.getCanData(1) != 0x7E)
                        {
                            m_nrFrameToReceive--;
                            data = response.getData();
                            // add the bytes to the receive buffer
                            for (uint fi = 1; fi < 8; fi++)
                            {
                                if (rx_pnt < rx_buffer.Length) // prevent overrun
                                {
                                    rx_buffer[rx_pnt++] = response.getCanData(fi);
                                }
                            }
                        }
                    }
                    retval = Encoding.ASCII.GetString(rx_buffer, 0, rx_pnt);
                }
                else if (response.getCanData(1) == 0x7F && response.getCanData(1) == 0x27)
                {
                    string info = TranslateErrorCode(response.getCanData(3));
                    CastInfoEvent("Error: " + info, ActivityType.ConvertingFile);
                }
            }
            Thread.Sleep(25);

            return retval;
        }

        // ReadDataByIdentifier 
        public byte[] RequestECUInfo(uint _pid)
        {
            byte[] retval = new byte[2];
            byte[] rx_buffer = new byte[1024];
            int rx_pnt = 0;

            if (canUsbDevice.isOpen())
            {
                ulong cmd = 0x0000000000001A02 | _pid << 16;
                CANMessage msg = new CANMessage(0x7E0, 0, 3); //<GS-18052011> support for ELM requires length byte
                msg.setData(cmd);
                m_canListener.setupWaitMessage(0x7E8);
                if (!canUsbDevice.sendMessage(msg))
                {
                    CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                    return retval;
                }

                int msgcnt = 0;
                bool _success = false;
                CANMessage response = new CANMessage();
                ulong data = 0;
                int timeout = timeoutP2ct;
                while (!_success && msgcnt < 2)
                {
                    response = new CANMessage();
                    response = m_canListener.waitMessage(timeout);
                    data = response.getData();
                    //RequestCorrectlyReceived-ResponsePending
                    if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x1A && response.getCanData(3) == 0x78)
                    {
                        logger.Debug("RequestCorrectlyReceived-ResponsePending");
                        timeout *= 3;
                    }
                    else if (data == 0)
                    {
                        logger.Debug("Received blank message while waiting for data");
                    }
                    else if (response.getCanData(1) != 0x7E)
                    {
                        _success = true;
                        msgcnt++;
                    }
                }

                if (response.getCanData(1) == 0x5A)
                {
                    // only one frame in this response
                    byte canLength = response.getCanData(0);
                    for (uint fi = 3; fi <= canLength; fi++)
                    {
                        rx_buffer[rx_pnt++] = response.getCanData(fi);
                    }
                    retval = new byte[rx_pnt];
                    for (int i = 0; i < rx_pnt; i++)
                    {
                        retval[i] = rx_buffer[i];
                    }
                }
                else if (response.getCanData(2) == 0x5A)
                {
                    SendAckMessageT8();
                    byte len = response.getCanData(1);
                    int m_nrFrameToReceive = ((len - 4) / 8);
                    if ((len - 4) % 8 > 0) m_nrFrameToReceive++;
                    int lenthisFrame = len;
                    if (lenthisFrame > 4) lenthisFrame = 4;

                    for (uint fi = 4; fi < 4 + lenthisFrame; fi++) rx_buffer[rx_pnt++] = response.getCanData(fi);
                    // wait for more records now

                    while (m_nrFrameToReceive > 0)
                    {
                        m_canListener.setupWaitMessage(0x7E8);
                        response = new CANMessage();
                        response = m_canListener.waitMessage(timeoutP2ct);
                        //RequestCorrectlyReceived-ResponsePending
                        if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x1A && response.getCanData(3) == 0x78)
                        {
                            logger.Debug("RequestCorrectlyReceived-ResponsePending");
                        }
                        else if (data == 0)
                        {
                            logger.Debug("Received blank message while waiting for data");
                        }
                        else if (response.getCanData(1) != 0x7E)
                        {
                            m_nrFrameToReceive--;
                            data = response.getData();
                            // add the bytes to the receive buffer
                            for (uint fi = 1; fi < 8; fi++)
                            {
                                if (rx_pnt < rx_buffer.Length) // prevent overrun
                                {
                                    rx_buffer[rx_pnt++] = response.getCanData(fi);
                                }
                            }
                        }
                    }

                    int length = rx_pnt - 1;
                    retval = new byte[length];
                    for (int i = 0; i < length; i++)
                    {
                        retval[i] = rx_buffer[i];
                    }
                }
                else if (response.getCanData(1) == 0x7F && response.getCanData(1) == 0x27)
                {
                    string info = TranslateErrorCode(response.getCanData(3));
                    CastInfoEvent("Error: " + info, ActivityType.ConvertingFile);
                }
            }
            Thread.Sleep(5);

            return retval;
        }

        // writeDataByIdentifier service 0x3B
        public bool WriteECUInfo(uint _pid, byte[] write)
        {
            if (write.Length > 6)
            {
                return WriteECUInfoMultipleFrames(_pid, write);
            }

            if (canUsbDevice.isOpen())
            {
                ulong cmd = 0x00000000003B00 | _pid << 16;
                cmd |= (2 + (ulong)write.Length);
                CANMessage msg = new CANMessage(0x7E0, 0, 8);
                
                for (int i = 0; i < write.Length; i++)
                {
                    cmd = AddByteToCommand(cmd, write[i], i + 3);
                }
                msg.setData(cmd);
                m_canListener.setupWaitMessage(0x7E8);
                if (!canUsbDevice.sendMessage(msg))
                {
                    CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                    return false;
                }

                int tries = 0;
                while (tries < 5)
                {
                    CANMessage ECMresponse = new CANMessage();
                    ECMresponse = m_canListener.waitMessage(timeoutP2ct);
                    ulong rxdata = ECMresponse.getData();

                    // response should be 0000000000<pid>7B02
                    if (getCanData(rxdata, 1) == 0x7B && getCanData(rxdata, 2) == _pid)
                    {
                        return true;
                    }
                    //RequestCorrectlyReceived-ResponsePending
                    else if (getCanData(rxdata, 1) == 0x7F && getCanData(rxdata, 2) == 0x3B && getCanData(rxdata, 3) == 0x78)
                    {
                        //CastInfoEvent("RequestCorrectlyReceived-ResponsePending", ActivityType.UploadingFlash);
                    }
                    else if (getCanData(rxdata, 1) == 0x7E)
                    {
                        //CastInfoEvent("0x3E Service TesterPresent response 0x7E received", ActivityType.ConvertingFile);
                        tries++;
                    }
                    // Negative Response 0x7F Service <nrsi> <service> <returncode>
                    else if (getCanData(rxdata, 1) == 0x7F && getCanData(rxdata, 2) == 0x3B)
                    {
                        string info = TranslateErrorCode(getCanData(rxdata, 3));
                        CastInfoEvent("Error: " + info, ActivityType.ConvertingFile);
                        return false;
                    }
                    else
                    {
                        CastInfoEvent("Error unexpected response: " + rxdata.ToString("X16"), ActivityType.ConvertingFile);
                        return false;
                    }
                }
            }
            return false;
        }

        private bool WriteECUInfoMultipleFrames(uint _pid, byte[] write)
        {
            if (canUsbDevice.isOpen())
            {
                ulong cmd = 0x00000000003B0010 | _pid << 24;
                cmd |= (2 + (ulong)write.Length) << 8;
                CANMessage msg = new CANMessage(0x7E0, 0, 7);
                int currentPosition = 0;
                int leftToWrite = Math.Min(write.Length, 4);
                for (int i = 0; i < leftToWrite; i++)
                {
                    cmd = AddByteToCommand(cmd, write[currentPosition], i + 4);
                    currentPosition++;
                }
                msg.setData(cmd);
                m_canListener.setupWaitMessage(0x7E8);
                if (!canUsbDevice.sendMessage(msg))
                {
                    CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                    return false;
                }

                int tries = 0;
                while (tries < 5)
                {
                    CANMessage ECMresponse = new CANMessage();
                    ECMresponse = m_canListener.waitMessage(timeoutP2ct);
                    ulong rxdata = ECMresponse.getData();

                    if (getCanData(rxdata, 0) == 0x30)
                    {
                        ulong command = 0x0000000000000021;

                        leftToWrite = Math.Min(write.Length - currentPosition, 7);
                        while (leftToWrite > 0)
                        {
                            cmd = command;
                            for (int i = 0; i < leftToWrite; i++)
                            {
                                cmd = AddByteToCommand(cmd, write[currentPosition], i + 1);
                                currentPosition++;
                            }
                            msg.setData(cmd);
                            m_canListener.setupWaitMessage(0x7E8);
                            canUsbDevice.sendMessage(msg);

                            command++;
                            leftToWrite = Math.Min(write.Length - currentPosition, 7);

                            if (command > 0x2F) command = 0x20;
                            msg.elmExpectedResponses = command == 0x21 ? 1 : 0;//on last command (iFrameNumber 22 expect 1 message)
                            if (command == 0x21)
                                m_canListener.ClearQueue();
                        }
                    }
                    // response should be 0000000000<pid>7B02
                    else if (getCanData(rxdata, 1) == 0x7B && getCanData(rxdata, 2) == _pid)
                    {
                        return true;
                    }
                    else if (getCanData(rxdata, 1) == 0x7E)
                    {
                        //CastInfoEvent("0x3E Service TesterPresent response 0x7E received", ActivityType.ConvertingFile);
                        tries++;
                    }
                    //RequestCorrectlyReceived-ResponsePending
                    else if (getCanData(rxdata, 1) == 0x7F && getCanData(rxdata, 2) == 0x3B && getCanData(rxdata, 3) == 0x78)
                    {
                        //CastInfoEvent("RequestCorrectlyReceived-ResponsePending", ActivityType.UploadingFlash);
                    }
                    // Negative Response 0x7F Service <nrsi> <service> <returncode>
                    else if (getCanData(rxdata, 1) == 0x7F && getCanData(rxdata, 2) == 0x3B)
                    {
                        string info = TranslateErrorCode(getCanData(rxdata, 3));
                        CastInfoEvent("Error: " + info, ActivityType.ConvertingFile);
                        return false;
                    }
                    else
                    {
                        CastInfoEvent("Error unexpected response: " + rxdata.ToString("X16"), ActivityType.ConvertingFile);
                        return false;
                    }
                }
            }
            return false;
        }

        private void SendAckMessageCIM()
        {
            if (canUsbDevice is CANELM327Device) return;
            SendMessage(0x245, 0x0000000000000030);
        }

        private void SendAckMessageT8()
        {
            if (canUsbDevice is CANELM327Device) return;
            SendMessage(0x7E0, 0x0000000000000030);
        }

        private void SendMessage(uint id, ulong data)
        {
            CANMessage msg = new CANMessage();
            msg.setID(id);
            msg.setLength(8);
            msg.setData(data);
            if (!canUsbDevice.sendMessage(msg))
            {
                logger.Debug("Failed to send message");
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        /// Set and Get methods for specific ECU parameters
        ///
        public float GetOilQuality()
        {
            float retval = 0;
            byte[] data = RequestECUInfo(0x25);
            if (data.Length == 4)
            {
                ulong lper = Convert.ToUInt64(data[0]) * 256 * 256 * 256;
                lper += Convert.ToUInt64(data[1]) * 256 * 256;
                lper += Convert.ToUInt64(data[2]) * 256;
                lper += Convert.ToUInt64(data[3]);
                retval = lper;
                retval /= 256;
            }
            return retval;
        }

        /// <summary>
        /// Sets the Oil quality indication (used for service interval calculation)
        /// </summary>
        /// <param name="percentage">Range from 0 to 16777215 %</param>
        /// <returns></returns>
        public bool SetOilQuality(float percentage)
        {
            bool retval = false;

            percentage *= 256;
            ulong lper = Convert.ToUInt64(percentage);

            byte[] write = new byte[4];
            write[0] = Convert.ToByte(lper / 0x1000000);
            write[1] = Convert.ToByte(lper / 0x10000 - (ulong)write[0] * 0x100);
            write[2] = Convert.ToByte(lper / 0x100 - (ulong)write[1] * 0x100 - (ulong)write[0] * 0x10000);
            write[3] = Convert.ToByte(lper - (ulong)write[2] * 0x100 - (ulong)write[1] * 0x10000 - (ulong)write[0] * 0x1000000);

            //ulong cmd = 0x0000000000253B06;
            //000D340000253B06 example  0000340D = 52,05078125 percent
            retval = WriteECUInfo(0x25, write);
            // Persist 
            RequestSecurityAccess(0);
            SendDeviceControlMessage(0x16);
            return retval;
        }

        public int GetTopSpeed()
        {
            int retval = 0;
            byte[] data = RequestECUInfo(0x02);
            if (data.Length == 2)
            {
                retval = Convert.ToInt32(data[0]) * 256;
                retval += Convert.ToInt32(data[1]);
                retval /= 10;
            }
            return retval;
        }

        /// <summary>
        /// Sets the speed limit in a T8 ECU
        /// </summary>
        /// <param name="speedlimit">speed limit in km/h</param>
        /// <returns></returns>
        public bool SetTopSpeed(int speedlimit)
        {
            bool retval = false;

            speedlimit *= 10;

            byte[] write = new byte[2];
            write[0] = Convert.ToByte(speedlimit / 256);
            write[1] = Convert.ToByte(speedlimit - (int)write[0] * 256);

            //ulong cmd = 0x0000000000023B04;
            //0000008C0A023B04 example  0A8C = 2700
            
            retval = WriteECUInfo(0x02, write);
            // Persist 
            RequestSecurityAccess(0);
            SendDeviceControlMessage(0x16);
            return retval;
        }

        public int GetRadum()
        {
            int retval = 0;
            byte[] data = RequestECUInfo(0x24);
            if (data.Length == 1)
            {
                retval = Convert.ToInt32(data[0]);
            }
            return retval;
        }

        public int GetPmcW()
        {
            int retval = 0;
            byte[] data = RequestECUInfo(0x2E);
            if (data.Length == 2)
            {
                retval = Convert.ToInt32(data[0]) * 256;
                retval += Convert.ToInt32(data[1]);
                retval /= 10;
            }
            return retval;
        }

        public string GetDiagnosticDataIdentifier()
        {
            //9A = 01 10 0
            string retval = string.Empty;
            byte[] data = RequestECUInfo(0x9A);
            logger.Debug("data: " + data[0].ToString("X2") + " " + data[1].ToString("X2"));
            if (data.Length == 2)
            {
                if (data[0] == 0x00 && data[1] == 0x00)
                    return string.Empty;

                retval = "0x" + data[0].ToString("X2") + " " + "0x" + data[1].ToString("X2");
            }
            return retval;
        }

        public string GetSaabPartnumber()
        {
            return GetInt64FromIdAsString(0x7C);
        }

        public string GetInt64FromIdAsString(uint id)
        {
            return GetInt64FromId(id).ToString();
        }

        private ulong GetInt64FromId(uint id)
        {
            ulong retval = 0;
            byte[] data = RequestECUInfo(id);
            if (data.Length == 4)
            {
                retval = Convert.ToUInt64(data[0]) * 256 * 256 * 256;
                retval += Convert.ToUInt64(data[1]) * 256 * 256;
                retval += Convert.ToUInt64(data[2]) * 256;
                retval += Convert.ToUInt64(data[3]);
            }
            return retval;
        }

        public string GetVehicleVIN()
        {
            // read and wait for sequence of acks
            return RequestECUInfoAsString(0x90, 3);
        }

        public string GetBuildDate()
        {
            // read and wait for sequence of acks
            return RequestECUInfoAsString(0x0A);
        }

        public string GetECUSWVersionNumber()
        {
            // read and wait for sequence of acks
            return RequestECUInfoAsString(0x95);
        }

        public string GetProgrammingDate()
        {
            // read and wait for sequence of acks
            return RequestECUInfoAsString(0x99);
        }

        public string GetProgrammingDateME96()
        {
            return GetInt64FromId(0x99).ToString("x");
        }

        public string GetDiagnosticAddress()
        {
            return "0x" + Convert.ToUInt32(RequestECUInfo(0xB0)[0]).ToString("x");
        }

        public string GetBoschEnableCounter()
        {
            return "0x" + Convert.ToUInt32(RequestECUInfo(0x70)[0]).ToString("x");
        }

        public string GetSerialNumber()
        {
            // read and wait for sequence of acks
            return RequestECUInfoAsString(0xB4);
        }

        public string GetCalibrationSet()
        {
            // read and wait for sequence of acks
            return RequestECUInfoAsString(0x74);
        }

        public string GetCodefileVersion()
        {
            // read and wait for sequence of acks
            return RequestECUInfoAsString(0x73);
        }

        public string GetECUDescription()
        {
            // read and wait for sequence of acks
            return RequestECUInfoAsString(0x72);
        }
        public string GetECUHardware()
        {
            // read and wait for sequence of acks
            return RequestECUInfoAsString(0x71);
        }

        public string GetSoftwareVersion()
        {
            // read and wait for sequence of acks
            string retval = RequestECUInfoAsString(0x08);
            retval = retval.Replace("\x00", "");
            return retval.Trim();
        }

        public float GetE85Percentage()
        {
            float retval = 0;
            GetDiagnosticDataIdentifier();

            // ReadDataByPacketIdentifier ($AA) Service
            CANMessage msg = new CANMessage(0x7E0, 0, 4); 
            ulong cmd = 0x000000007A01AA03;// <dpid=7A> <level=sendOneResponse> <service=AA> <length>
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8, 0x5E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return 0f;
            }
            CANMessage response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            // 7A 00 52 04 00 16 DC 00
            // <dpid><6 datavalues>
            if (response.getCanData(0) == 0x7A)
            {
                retval = Convert.ToInt32(response.getCanData(2));
            }
            // Negative Response 0x7F Service <nrsi> <service> <returncode>
            else if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0xAA)
            {
                string info = TranslateErrorCode(response.getCanData(3));
                logger.Debug("Error, cannot get optional E85%: " + info);
            }
            return retval;
        }

        public bool SetE85Percentage(float percentage)
        {
            bool retval = false;
            percentage *= 256;
            int iper = Convert.ToInt32(percentage);
            CANMessage msg = new CANMessage(0x7E0, 0, 5);
            // DeviceControl Service
            ulong cmd = 0x000000000018AE04; // <ControlByte 5-1> <CPID Number 0x18> <Device Control 0xAE service>
            byte b1 = Convert.ToByte(iper / 256);
            cmd = AddByteToCommand(cmd, b1, 4);
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage ECMresponse = new CANMessage();
            ECMresponse = m_canListener.waitMessage(timeoutP2ct);
            ulong rxdata = ECMresponse.getData();
            // response should be 000000000018EE02
            if (getCanData(rxdata, 1) == 0xEE && getCanData(rxdata, 2) == 0x18) // <EE positive response service id> <cpid>
            {
                RequestSecurityAccess(0);
                SendDeviceControlMessage(0x16);
                retval = true;
            }
            // Negative Response 0x7F Service <nrsi> <service> <returncode>
            else if (getCanData(rxdata, 1) == 0x7F && getCanData(rxdata, 2) == 0xAE)
            {
                string info = TranslateErrorCode(getCanData(rxdata, 3));
                logger.Debug("Error, cannot set optional E85%: " + info);
            }
            return retval;
        }

        public int GetManufacturersEnableCounter()
        {
            int retval = 0;
            byte[] data = RequestECUInfo(0xA0);
            if (data.Length >= 1)
            {
                retval = Convert.ToInt32(data[0]);
            }
            return retval;
        }

        /// <summary>
        /// Sets the RPM limit in a T8 ECU
        /// </summary>
        /// <param name="rpmlimit"></param>
        /// <returns></returns>
        public bool SetRPMLimiter(int rpmlimit)
        {
            bool retval = false;

            byte[] write = new byte[2];
            write[0] = Convert.ToByte(rpmlimit / 256);
            write[1] = Convert.ToByte(rpmlimit - (int)write[0] * 256);

            //ulong cmd = 0x0000000000293B04;
            //0000000618293B04 example  1806 = 6150
            retval = WriteECUInfo(0x29, write);
            // Persist 
            RequestSecurityAccess(0);
            SendDeviceControlMessage(0x16);
            return retval;
        }

        public int GetRPMLimiter()
        {
            int retval = 0;
            byte[] data = RequestECUInfo(0x29);
            if (data.Length == 2)
            {
                retval = Convert.ToInt32(data[0]) * 256;
                retval += Convert.ToInt32(data[1]);
            }
            return retval;
        }

        public bool SetVIN(string vin)
        {
            bool retval = false;
            // 62 DPID + 01 sendOneResponse + $AA ReadDataByPacketIdentifier
            CANMessage msg62 = new CANMessage(0x7E0, 0, 4);
            msg62.setData(0x000000006201AA03);
            m_canListener.setupWaitMessage(0x7E8, 0x5E8);
            CastInfoEvent("Wait for response 5E8 62 00 00", ActivityType.ConvertingFile);
            if (!canUsbDevice.sendMessage(msg62))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage response62 = new CANMessage();
            response62 = m_canListener.waitMessage(timeoutP2ct);
            logger.Debug("---" + response62.getData().ToString("X16"));
            //05E8	62	00	00	02	A7	01	7F	01
            if (response62.getCanData(0) == 0x62)
            {
                if (response62.getCanData(1) == 0x00)
                {
                    if (response62.getCanData(2) == 0x00)
                    {
                        CastInfoEvent("Got response 5E8 62 00 00", ActivityType.ConvertingFile);
                    }
                }
            }

            if (GetManufacturersEnableCounter() == 0x00)
                CastInfoEvent("GetManufacturersEnableCounter == 0x00", ActivityType.ConvertingFile);

            CastInfoEvent("ECM EOL Parameter Settings-part1", ActivityType.ConvertingFile);
            // 02 DPID + 01 sendOneResponse + $AA ReadDataByPacketIdentifier
            CANMessage msg = new CANMessage(0x7E0, 0, 4);
            msg.setData(0x000000000201AA03);
            m_canListener.setupWaitMessage(0x7E8, 0x5E8);
            CastInfoEvent("Wait for response 5E8 02 02", ActivityType.ConvertingFile);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            logger.Debug("---" + response.getData().ToString("X16"));
            //05E8	02	02	A0	42	80	A0	00	00
            if (response.getCanData(0) == 0x02)
            {
                if (response.getCanData(1) == 0x02)
                {
                    CastInfoEvent("Got response 5E8 02 02", ActivityType.ConvertingFile);
                }
            }

            retval = ProgramVIN(vin);
            
            Thread.Sleep(200);

            // Persist 
            RequestSecurityAccess(0);
            SendDeviceControlMessage(0x16);

            return retval;
        }

        // Output level - Low, High
        // Convertible - true, false
        // Biopower - true, false 
        // Diagnostics - EOBD, OBD2, LOBD
        // SAI - true, false
        // Clutch start - true, false
        // Tank type - AWD, US, EU
        public bool GetPI01(out bool convertible, out bool sai, out bool highoutput, out bool biopower, out DiagnosticType diagnosticType, out bool clutchStart, out TankType tankType, out string raw)
        {
            convertible = false;
            sai = false;
            highoutput = false;
            biopower = false;
            raw = string.Empty;
            diagnosticType = DiagnosticType.EOBD;
            tankType = TankType.EU;
            clutchStart = false;
            byte[] data = RequestECUInfo(0x01);
            
            if (data.Length >= 2)
            {
                if (data[0] == 0x00 && data[1] == 0x00) return false;

                // -------C
                biopower = BitTools.GetBit(data[0], 0);

                // -----C--
                convertible = BitTools.GetBit(data[0], 2);

                // ---01--- US
                // ---10--- EU
                // ---11--- AWD
                switch (data[0] & 0x18)
                {
                    case 0x08:
                        tankType = TankType.US;
                        break;
                    case 0x10:
                        tankType = TankType.EU;
                        break;
                    case 0x18:
                        tankType = TankType.AWD;
                        break;
                }

                // -01----- OBD2
                // -10----- EOBD
                // -11----- LOBD
                switch (data[0] & 0x60)
                {
                    case 0x20:
                        diagnosticType = DiagnosticType.OBD2;
                        break;
                    case 0x40:
                        diagnosticType = DiagnosticType.EOBD;
                        break;
                    case 0x60:
                        diagnosticType = DiagnosticType.LOBD;
                        break;
                    default:
                        diagnosticType = DiagnosticType.None;
                        break;
                }

                // on = -----10-
                // off= -----01-
                clutchStart = !BitTools.GetBit(data[1], 1) && BitTools.GetBit(data[1], 2) ? true : false;

                // on = ---10---
                // off= ---01---
                sai = !BitTools.GetBit(data[1], 3) && BitTools.GetBit(data[1], 4) ? true : false;


                // high= -01-----
                // low = -10-----
                highoutput = BitTools.GetBit(data[1], 5) && !BitTools.GetBit(data[1], 6) ? true : false;
                
                for (int i = 0; i < data.Length; i++)
                {
                    raw += "0x" + data[i].ToString("X2") + " ";
                }
            }

            return true;
        }

        public bool SetPI01(bool convertible, bool sai, bool highoutput, bool biopower, DiagnosticType diagnosticType, bool clutchStart, TankType tankType)
        {
            bool retval = false;
            byte[] data = RequestECUInfo(0x01);
            CANMessage msg = new CANMessage(0x7E0, 0, 7);
            ulong cmd = 0x0000000000013B06;
            // -------C
            data[0] = BitTools.SetBit(data[0], 0, biopower);

            // -----C--
            data[0] = BitTools.SetBit(data[0], 2, convertible);

            // ---01--- US
            // ---10--- EU
            // ---11--- AWD
            switch (tankType)
            {
                case TankType.US:
                    data[0] = BitTools.SetBit(data[0], 3, true);
                    data[0] = BitTools.SetBit(data[0], 4, false);
                    break;
                case TankType.EU:
                    data[0] = BitTools.SetBit(data[0], 3, false);
                    data[0] = BitTools.SetBit(data[0], 4, true);
                    break;
                case TankType.AWD:
                    data[0] = BitTools.SetBit(data[0], 3, true);
                    data[0] = BitTools.SetBit(data[0], 4, true);
                    break;
            }

            // -01----- OBD2
            // -10----- EOBD
            // -11----- LOBD
            switch (diagnosticType)
            {
                case DiagnosticType.OBD2:
                    data[0] = BitTools.SetBit(data[0], 5, true);
                    data[0] = BitTools.SetBit(data[0], 6, false);
                    break;
                case DiagnosticType.EOBD:
                    data[0] = BitTools.SetBit(data[0], 5, false);
                    data[0] = BitTools.SetBit(data[0], 6, true);
                    break;
                case DiagnosticType.LOBD:
                    data[0] = BitTools.SetBit(data[0], 5, true);
                    data[0] = BitTools.SetBit(data[0], 6, true);
                    break;
                case DiagnosticType.None:
                default:
                    data[0] = BitTools.SetBit(data[0], 5, false);
                    data[0] = BitTools.SetBit(data[0], 6, false);
                    break;
            }

            // on = -----10-
            // off= -----01-
            data[1] = BitTools.SetBit(data[1], 1, !clutchStart);
            data[1] = BitTools.SetBit(data[1], 2, clutchStart);

            // on = ---10---
            // off= ---01---
            data[1] = BitTools.SetBit(data[1], 3, !sai);
            data[1] = BitTools.SetBit(data[1], 4, sai);
            
            // high= -01-----
            // low = -10-----
            data[1] = BitTools.SetBit(data[1], 5, highoutput);
            data[1] = BitTools.SetBit(data[1], 6, !highoutput);

            cmd = AddByteToCommand(cmd, data[0], 3);
            cmd = AddByteToCommand(cmd, data[1], 4);
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage ECMresponse = new CANMessage();
            ECMresponse = m_canListener.waitMessage(timeoutP2ct);
            ulong rxdata = ECMresponse.getData();
            // response should be 0000000000017B02
            if (getCanData(rxdata, 1) == 0x7B && getCanData(rxdata, 2) == 0x01)
            {
                //7e0  02 27 FD 00 00 00 00 00 request sequrity access FD
                //7e8  04 67 FD 00 00 00 00 00
                RequestSecurityAccess(0);

                //7e0  07 AE 16 00 00 00 00 00
                //7e8  02 EE 16 00 00 00 00 00
                SendDeviceControlMessage(0x16);

                retval = true;
            }
            else if (getCanData(rxdata, 1) == 0x7F && getCanData(rxdata, 2) == 0x3B)
            {
                CastInfoEvent("Error: " + TranslateErrorCode(getCanData(rxdata, 3)), ActivityType.ConvertingFile);
            }
            return retval;
        }

        // AC (Air Condition) type.
        public string GetPI03()
        {
            string retval = string.Empty;
            byte[] data = RequestECUInfo(0x03);
            if (data.Length == 1)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    retval += "0x" + data[i].ToString("X2") + " ";
                }
                logger.Debug("03data: " + retval);
            }
            return retval;
        }

        // Shift indicator
        public string GetPI04()
        {
            string retval = string.Empty;
            byte[] data = RequestECUInfo(0x04);
            if (data.Length == 1)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    retval += "0x" + data[i].ToString("X2") + " ";
                }
                logger.Debug("04data: " + retval);
            }
            return retval;
        }

        // odometer circumference (cm)
        public string GetPI07()
        {
            string retval = string.Empty;
            byte[] data = RequestECUInfo(0x07);
            if (data.Length == 1)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    retval += "0x" + data[i].ToString("X2") + " ";
                }
                logger.Debug("07data: " + retval);
            }
            return retval;
        }

        // Opel variant programming
        public string GetPI2E()
        {
            string retval = string.Empty;
            byte[] data = RequestECUInfo(0x2E);
            if (data.Length == 2)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    retval += "0x" + data[i].ToString("X2") + " ";
                }
                logger.Debug("2Edata: " + retval);
            }
            return retval;
        }

        // Subnet config list highspeed - ECM, ABS, SADS, TCM, CIM
        public string GetPIB9()
        {
            string retval = string.Empty;
            byte[] data = RequestECUInfo(0xB9);
            if (data.Length == 2)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    retval += "0x" + data[i].ToString("X2") + " ";
                }
                logger.Debug("B9data: " + retval);
            }
            return retval;
        }

        // Wheel circumference (cm)
        public string GetPI24()
        {
            string retval = string.Empty;
            byte[] data = RequestECUInfo(0x24);
            if (data.Length == 1)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    retval += "0x" + data[i].ToString("X2") + " ";
                }
                logger.Debug("24data: " + retval);
            }
            return retval;
        }

        // Manufacturer Enable Counter
        // same as GetManufacturersEnableCounter()
        public string GetPIA0()
        {
            string retval = string.Empty;
            byte[] data = RequestECUInfo(0xA0);
            if (data.Length == 1)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    retval += "0x" + data[i].ToString("X2") + " ";
                }
                logger.Debug("A0data: " + retval);
            }
            return retval;
        }

        // Type Approval no
        public string GetPI96()
        {
            string retval = string.Empty;
            byte[] data = RequestECUInfo(0x96);
            if (data.Length == 10)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    retval += "0x" + data[i].ToString("X2") + " ";
                }
                logger.Debug("96data: " + retval);
            }
            return retval;
        }

        ///////////////////////////////////////////////////////////////////////////
        /// Flash and read methods
        ///

        public byte[] readDataByLocalIdentifier(bool LegionMode, byte PCI, int address, int length, out bool success)
        {
            success = false;
            byte[] buffer = this.sendReadDataByLocalIdentifier(LegionMode, PCI, address, length, out success);

            //Thread.Sleep(1); //was 1 <GS-05052011>
            return buffer;
        }

        public byte[] readMemory(int address, int length, out bool success)
        {
            //lock (this)
            {
                success = false;
                byte[] buffer = this.sendReadCommand(address, length, out success);
                //AddToCanTrace("sendReadCommand returned: " + buffer[0].ToString("X2") + " " + success.ToString());
                Thread.Sleep(1); //was 1 <GS-05052011>
                return buffer;
            }
        }

        public byte[] readMemoryNew(int address, int length, int blockSize, bool ShowProgress)
        {
            if (length == 0 || blockSize == 0)
            {
                return null;
            }
            return this.sendReadCommand_24bit(address, length, blockSize, ShowProgress);
        }

        public bool writeMemoryNew(int address, byte[] memdata, int blockSize, bool ShowProgress)
        {
            if (memdata == null || memdata.Length == 0 || blockSize == 0)
            {
                return false;
            }
            return this.atWriteByAddress(address, memdata, blockSize, ShowProgress);
        }

        public byte[] ReadSymbolByIndex(UInt16 idx)
        {
            return this.atReadSymByIdx(idx);
        }

        public bool ResetDynamicList()
        {
            return this.atResetDynList();
        }

        public bool ConfigureDynamicListByIndex(List<UInt16> dynList)
        {
            return this.atConfigureDynSyms_ByIdx(dynList);
        }

        public bool ConfigureDynamicListByAddress(List<dynAddrHelper> dynList)
        {
            return this.atConfigureDynSyms_ByAddr(dynList);
        }

        public byte[] ReadDynamicSymbols()
        {
            return this.atReadDynSyms();
        }

        public void InitializeSession()
        {
            CANMessage response = new CANMessage();

            //101      8 FE 01 3E 00 00 00 00 00 
            CANMessage msg = new CANMessage(0x11, 0, 2);
            ulong cmd = 0x0000000000003E01;
            msg.setData(cmd);
            if (!canUsbDevice.sendMessage(msg))
            {
                logger.Debug("Couldn't send message");
            }
        }

        private bool requestDownload(bool z22se)
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 7);   
            ulong cmd = 0x0000000000003400;
            if (z22se)
                cmd += 5;
            else
                cmd += 6;

            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            //CastInfoEvent("rx requestDownload: " + data.ToString("X16"), ActivityType.UploadingBootloader);
            if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x74)
            {
                return false;
            }
            return true;
        }

        private bool Send0120()
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 2); 
            ulong cmd = 0x0000000000002001;
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            if (getCanData(data, 0) != 0x01 || (getCanData(data, 1) != 0x50 && getCanData(data, 1) != 0x60))
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// InitiateDiagnosticOperation 
        /// </summary>
        /// <returns></returns>
        private bool StartSession10()
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 3);
            ulong cmd = 0x0000000000021002; // 0x02 0x10 0x02
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x50)
            {
                return false;
            }
            return true;
        }

        private bool StartSession10_WakeUp()
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 3);
            ulong cmd = 0x0000000000041002; // 0x02 0x10 0x02
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x50)
            {
                return false;
            }
            return true;
        }

        private bool StartSession1081()
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 3);
            ulong cmd = 0x0000000000811002; // 0x02 0x10 0x02
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
            }
            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            if (getCanData(data, 0) != 0x03 || getCanData(data, 1) != 0x7F)
            {
                return false;
            }
            return true;
        }

        private bool StartSession20()
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 2);
            ulong cmd = 0x0000000000002001; // 0x02 0x10 0x02
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x60)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// DisableNormalCommunication 
        /// </summary>
        /// <returns></returns>
        private bool SendShutup()
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 2);
            ulong cmd = 0x0000000000002801; // 0x02 0x10 0x02
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x68)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// ReportProgrammedState 
        /// </summary>
        /// <returns></returns>
        private bool SendA2()
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 2);
            ulong cmd = 0x000000000000A201; // 0x02 0x10 0x02
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0xE2)
            {
                return false;
            }
            return true;
        }

        private bool StartBootloader(uint StartAddr)
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 7);
            // ulong cmd = 0x0060241000803606;
            ulong cmd = 0x0000000000803606;

            // Swap address to make it easy to use
            ulong tmp = (
                (StartAddr & 0xFF) << 24 |
                ((StartAddr >> 8) & 0xFF) << 16 |
                ((StartAddr >> 16) & 0xFF) << 8 |
                ((StartAddr >> 24) & 0xFF));
            tmp <<= 24;
            cmd += tmp;


            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x76)
            {
                return false;
            }
            return true;
        }

        private bool SendA5()
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 3);   
            ulong cmd = 0x000000000001A502; // 0x02 0x10 0x02
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0xE5)
            {
                return false;
            }
            return true;
        }

        private bool SendA503()
        {
            // expect no response
            CANMessage msg = new CANMessage(0x7E0, 0, 3);
            ulong cmd = 0x000000000003A502;
            msg.setData(cmd);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            return true;
        }

        public bool testSRAMWrite(/*int address, byte[] data*/)
        {
            StartSession10();
            CastInfoEvent("Requesting mandatory data", ActivityType.UploadingBootloader);

            RequestECUInfo(0x90);
            RequestECUInfo(0x97);
            RequestECUInfo(0x92);
            RequestECUInfo(0xB4);
            RequestECUInfo(0xC1);
            RequestECUInfo(0xC2);
            RequestECUInfo(0xC3);
            RequestECUInfo(0xC4);
            RequestECUInfo(0xC5);
            RequestECUInfo(0xC6);
            Send0120();
            Thread.Sleep(1000);

            StartSession1081();

            StartSession10();
            CastInfoEvent("Telling ECU to clear CANbus", ActivityType.UploadingBootloader);
            SendShutup();
            SendA2();
            SendA5();
            SendA503();
            Thread.Sleep(500);
            SendKeepAlive();
            _securityLevel = AccessLevel.AccessLevel01;
            CastInfoEvent("Requesting security access", ActivityType.UploadingBootloader);
            RequestSecurityAccess(2000);
            Thread.Sleep(500);
            CastInfoEvent("Uploading data", ActivityType.UploadingBootloader);

            int startAddress = 0x102400;
            int saved_progress = 0;
            Bootloader btloaderdata = new Bootloader();
            if (requestDownload(false))
            {
                for (int i = 0; i < 0x46; i++)
                {
                    //10 F0 36 00 00 10 24 00
                    //logger.Debug("Sending bootloader: " + startAddress.ToString("X8"));
                    // cast event
                    int percentage = (int)(((float)i * 100) / 70F);
                    if (percentage > saved_progress)
                    {
                        CastProgressWriteEvent(percentage);
                        saved_progress = percentage;
                    }

                    byte iFrameNumber = 0x21;
                    if (SendTransferData(0xF0, startAddress, 0x7E8))
                    {
                        // send 0x22 (34) frames with data from bootloader
                        CANMessage msg = new CANMessage(0x7E0, 0, 8);
                        for (int j = 0; j < 0x22; j++)
                        {
                            ulong cmd = 0x0000000000000000; // 0x34 = upload data to ECU
                            msg.setData(cmd);
                            msg.setCanData(iFrameNumber, 0);
                            msg.setCanData(0x00, 1);
                            msg.setCanData(0x01, 2);
                            msg.setCanData(0x02, 3);
                            msg.setCanData(0x03, 4);
                            msg.setCanData(0x04, 5);
                            msg.setCanData(0x05, 6);
                            msg.setCanData(0x06, 7);
                            iFrameNumber++;
                            if (iFrameNumber > 0x2F) iFrameNumber = 0x20;
                            if (!canUsbDevice.sendMessage(msg))
                            {
                                logger.Debug("Couldn't send message");
                            }
                            Thread.Sleep(1);
                        }
                        // send the remaining data
                        m_canListener.setupWaitMessage(0x7E8);
                        // now wait for 01 76 00 00 00 00 00 00 
                        CANMessage response = new CANMessage();
                        response = new CANMessage();
                        response = m_canListener.waitMessage(timeoutP2ct);
                        ulong datax = response.getData();
                        if (getCanData(datax, 0) != 0x01 || getCanData(datax, 1) != 0x76)
                        {
                            return false;
                        }
                        SendKeepAlive();
                        startAddress += 0xEA;

                    }
                    else
                    {
                        logger.Debug("Did not receive correct response from SendTransferData");
                    }
                }
            }
            return true;
        }

        public bool WriteToSRAM(int address, byte[] memdata)
        {
            if (!canUsbDevice.isOpen()) return false;

            return false;
        }



        private bool BroadcastSession10()
        {
            CANMessage msg = new CANMessage(0x11, 0, 3);
            ulong cmd = 0x0000000000021002; // 0x02 0x10 0x02
            msg.setData(cmd);
            //m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            /*CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutPTct);
            ulong data = response.getData();
            if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x50)
            {
                return false;
            }*/
            return true;
        }

        private bool BroadcastShutup()
        {
            CANMessage msg = new CANMessage(0x11, 0, 2);
            ulong cmd = 0x0000000000002801; // 0x02 0x10 0x02
            msg.setData(cmd);
            //m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            /*CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutPTct);
            ulong data = response.getData();
            if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x68)
            {
                return false;
            }*/
            return true;
        }

        private bool BroadcastShutup011()
        {
            CANMessage msg = new CANMessage(0x11, 0, 2);
            ulong cmd = 0x0000000000002801; // 0x02 0x10 0x02
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x311);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x68)
            {
                return false;
            }
            return true;
        }

        private int GetProgrammingState(uint responseID)
        {
            logger.Debug("Get programming state");
            CANMessage msg = new CANMessage(0x11, 0, 2);
            ulong cmd = 0x000000000000A201; // 0x02 0x10 0x02
            msg.setData(cmd);
            m_canListener.setupWaitMessage(responseID);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return 0;
            }
            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            logger.Debug("Get programming state response: " + data.ToString("X16"));
            //\__ 00 00 03 11 02 e2 01 00 00 00 00 00 Magic reply, T8 replies with 0311 and programming state 01(recovery state?)
            if (data == 0) return -1;
            if (getCanData(data, 1) != 0xE2 || getCanData(data, 0) != 0x02)
            {
                return 0;
            }
            return Convert.ToInt32(getCanData(data, 2));
        }

        private int GetProgrammingState011()
        {
            CANMessage msg = new CANMessage(0x11, 0, 2);
            ulong cmd = 0x000000000000A201; // 0x02 0x10 0x02
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x311);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return 0;
            }
            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            //\__ 00 00 03 11 02 e2 01 00 00 00 00 00 Magic reply, T8 replies with 0311 and programming state 01(recovery state?)
            if (getCanData(data, 1) != 0xE2 || getCanData(data, 0) != 0x02)
            {
                return 0;
            }
            return Convert.ToInt32(getCanData(data, 2));
        }

        private bool SendA5011()
        {
            CANMessage msg = new CANMessage(0x11, 0, 3);   
            ulong cmd = 0x000000000001A502; // 0x02 0x10 0x02
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x311);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0xE5)
            {
                return false;
            }
            return true;
        }

        private bool SendA503011()
        {
            // expect no response
            CANMessage msg = new CANMessage(0x11, 0, 3);
            ulong cmd = 0x000000000003A502;
            msg.setData(cmd);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            return true;
        }

        private bool RequestSecurityAccess011(int millisecondsToWaitWithResponse)
        {
            int secondsToWait = millisecondsToWaitWithResponse / 1000;
            ulong cmd = 0x0000000000FD2702; // request security access
            if (_securityLevel == AccessLevel.AccessLevel01)
            {
                cmd = 0x0000000000012702; // request security access
            }
            else if (_securityLevel == AccessLevel.AccessLevelFB)
            {
                cmd = 0x0000000000FB2702; // request security access
            }
            CANMessage msg = new CANMessage(0x11, 0, 3); 
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x311);
            CastInfoEvent("Requesting security access", ActivityType.ConvertingFile);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            //ulong data = response.getData();
            logger.Debug("---" + response.getData().ToString("X16"));
            if (response.getCanData(1) == 0x67)
            {
                if (response.getCanData(2) == 0xFD || response.getCanData(2) == 0xFB || response.getCanData(2) == 0x01)
                {
                    CastInfoEvent("Got seed value from ECU", ActivityType.ConvertingFile);

                    while (secondsToWait > 0)
                    {
                        CastInfoEvent("Waiting for " + secondsToWait.ToString() + " seconds...", ActivityType.UploadingBootloader);
                        Thread.Sleep(1000);
                        SendKeepAlive();
                        secondsToWait--;

                    }

                    byte[] seed = new byte[2];
                    seed[0] = response.getCanData(3);
                    seed[1] = response.getCanData(4);
                    if (seed[0] == 0x00 && seed[1] == 0x00)
                    {
                        return true; // security access was already granted
                    }
                    else
                    {
                        SeedToKey s2k = new SeedToKey();
                        byte[] key = s2k.calculateKey(seed, _securityLevel);
                        CastInfoEvent("Security access : Key (" + key[0].ToString("X2") + key[1].ToString("X2") + ") calculated from seed (" + seed[0].ToString("X2") + seed[1].ToString("X2") + ")", ActivityType.ConvertingFile);

                        ulong keydata = 0x0000000000FE2704;
                        if (_securityLevel == AccessLevel.AccessLevel01)
                        {
                            keydata = 0x0000000000022704;
                        }
                        else if (_securityLevel == AccessLevel.AccessLevelFB)
                        {
                            keydata = 0x0000000000FC2704;
                        }
                        ulong key1 = key[1];
                        key1 *= 0x100000000;
                        keydata ^= key1;
                        ulong key2 = key[0];
                        key2 *= 0x1000000;
                        keydata ^= key2;
                        msg = new CANMessage(0x11, 0, 5);
                        msg.setData(keydata);
                        m_canListener.setupWaitMessage(0x311);
                        if (!canUsbDevice.sendMessage(msg))
                        {
                            CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                            return false;
                        }
                        response = new CANMessage();
                        response = m_canListener.waitMessage(timeoutP2ct);
                        // is it ok or not
                        if (response.getCanData(1) == 0x67 && (response.getCanData(2) == 0xFE || response.getCanData(2) == 0xFC || response.getCanData(2) == 0x02))
                        {
                            CastInfoEvent("Security access granted", ActivityType.ConvertingFile);
                            return true;
                        }
                        else if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x27)
                        {
                            CastInfoEvent("Error: " + TranslateErrorCode(response.getCanData(3)), ActivityType.ConvertingFile);
                        }
                    }

                }
                else if (response.getCanData(2) == 0xFE || response.getCanData(2) == 0x02)
                {
                    CastInfoEvent("Security access granted", ActivityType.ConvertingFile);
                    return true;
                }
            }
            else if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x27)
            {
                CastInfoEvent("Error: " + TranslateErrorCode(response.getCanData(3)), ActivityType.ConvertingFile);
            }
            return false;
        }

        private bool requestDownload011()
        {
            CANMessage msg = new CANMessage(0x11, 0, 7);   
            ulong cmd = 0x0000000000003406;
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x311);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            //CastInfoEvent("rx requestDownload: " + data.ToString("X16"), ActivityType.UploadingBootloader);
            if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x74)
            {
                return false;
            }
            return true;
        }

        private bool StartBootloader011(bool LegionMode)
        {
            CANMessage msg = new CANMessage(0x11, 0, 7);
            // ulong cmd = 0x0060241000803606;
            ulong cmd = 0x0000241000803606;
            if (!LegionMode)
                cmd += 0x0060000000000000;

            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x311);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x76)
            {
                return false;
            }
            return true;
        }

        public byte[] RequestECUInfo0101(uint _pid)
        {
            byte[] retval = new byte[2];
            byte[] rx_buffer = new byte[128];
            int rx_pnt = 0;

            if (canUsbDevice.isOpen())
            {
                ulong cmd = 0x0000000000001A02 | _pid << 16;

                //SendMessage(data);  // software version
                CANMessage msg = new CANMessage(0x11, 0, 3); //<GS-18052011> support for ELM requires length byte
                msg.setData(cmd);
                m_canListener.setupWaitMessage(0x7E8);
                if (!canUsbDevice.sendMessage(msg))
                {
                    CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                    return retval;
                }

                int msgcnt = 0;
                bool _success = false;
                CANMessage response = new CANMessage();
                ulong data = 0;
                while (!_success && msgcnt < 2)
                {
                    response = new CANMessage();
                    response = m_canListener.waitMessage(timeoutP2ct);
                    data = response.getData();
                    //RequestCorrectlyReceived-ResponsePending
                    if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x1A && response.getCanData(3) == 0x78)
                    {
                        //CastInfoEvent("RequestCorrectlyReceived-ResponsePending", ActivityType.UploadingFlash);
                    }
                    else if (response.getCanData(1) != 0x7E)
                    {
                        _success = true;
                    }
                    msgcnt++;
                }

                //CANMessage response = new CANMessage();
                //response = m_canListener.waitMessage(timeoutPTct);
                //ulong data = response.getData();
                if (response.getCanData(1) == 0x5A)
                {
                    // only one frame in this repsonse

                    for (uint fi = 3; fi < 8; fi++) rx_buffer[rx_pnt++] = response.getCanData(fi);
                    retval = new byte[rx_pnt];
                    for (int i = 0; i < rx_pnt; i++) retval[i] = rx_buffer[i];
                }
                else if (response.getCanData(2) == 0x5A)
                {
                    SendAckMessageT8();
                    byte len = response.getCanData(1);
                    int m_nrFrameToReceive = ((len - 4) / 8);
                    if ((len - 4) % 8 > 0) m_nrFrameToReceive++;
                    int lenthisFrame = len;
                    if (lenthisFrame > 4) lenthisFrame = 4;
                    for (uint fi = 4; fi < 4 + lenthisFrame; fi++) rx_buffer[rx_pnt++] = response.getCanData(fi);
                    // wait for more records now

                    while (m_nrFrameToReceive > 0)
                    {
                        m_canListener.setupWaitMessage(0x7E8);
                        response = new CANMessage();
                        response = m_canListener.waitMessage(timeoutP2ct);
                        //RequestCorrectlyReceived-ResponsePending
                        if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x1A && response.getCanData(3) == 0x78)
                        {
                            //CastInfoEvent("RequestCorrectlyReceived-ResponsePending", ActivityType.UploadingFlash);
                        }
                        else if (response.getCanData(1) != 0x7E)
                        {
                            m_nrFrameToReceive--;
                            data = response.getData();
                            // add the bytes to the receive buffer
                            for (uint fi = 1; fi < 8; fi++)
                            {
                                if (rx_pnt < rx_buffer.Length) // prevent overrun
                                {
                                    rx_buffer[rx_pnt++] = response.getCanData(fi);
                                }
                            }
                        }
                    }
                    retval = new byte[rx_pnt];
                    for (int i = 0; i < rx_pnt; i++) retval[i] = rx_buffer[i];

                }
                else if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x27)
                {
                    CastInfoEvent("Error: " + TranslateErrorCode(response.getCanData(3)), ActivityType.ConvertingFile);
                }
            }
            Thread.Sleep(5);

            return retval;
        }

        public string GetDiagnosticDataIdentifier0101()
        {
            //9A = 01 10 0
            string retval = string.Empty;
            byte[] data = RequestECUInfo0101(0x9A);
            if (data[0] == 0x00 && data[1] == 0x00) return string.Empty;
            if (data.Length >= 2)
            {
                retval = "0x" + data[0].ToString("X2") + " " + "0x" + data[1].ToString("X2");
            }
            return retval;
        }

        private bool _needRecovery = false;

        public bool NeedRecovery
        {
            get { return _needRecovery; }
            set { _needRecovery = value; }
        }

        /// <summary>
        /// Write a byte array to an address.
        /// </summary>
        /// <param name="address">Address. Must be greater than 0x1000</param>
        /// <param name="data">Data to be written</param>
        /// <returns></returns>
        //KWP2000 can read more than 6 bytes at a time.. but for now we are happy with this
        public bool writeMemory(int address, byte[] memdata)
        {
            if (!canUsbDevice.isOpen()) return false;
            _stallKeepAlive = true;

            /* for (int i = 0; i < 6; i++)
             {
                 InitializeSession();
                 Thread.Sleep(1000);
             }*/

        CANMessage response = new CANMessage();
            ulong data = 0;
            // first send 
            CANMessage msg = new CANMessage(0x7E0, 0, 7);
            //logger.Debug("Writing " + address.ToString("X8") + " len: " + memdata.Length.ToString("X2"));
            ulong cmd = 0x0000000000003406; // 0x34 = upload data to ECU
            ulong addressHigh = (uint)address & 0x0000000000FF0000;
            addressHigh /= 0x10000;
            ulong addressMiddle = (uint)address & 0x000000000000FF00;
            addressMiddle /= 0x100;
            ulong addressLow = (uint)address & 0x00000000000000FF;
            ulong len = (ulong)memdata.Length;

            //cmd |= (addressLow * 0x100000000);
            //cmd |= (addressMiddle * 0x1000000);
            //cmd |= (addressHigh * 0x10000);
            //            cmd |= (len * 0x1000000000000);


            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                logger.Debug("Couldn't send message");
            }


            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            data = response.getData();
            CastInfoEvent("Waited for response: " + data.ToString("X8"), ActivityType.ConvertingFile);
            if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x74)
            {
                CastInfoEvent("Unable to write to ECUs memory", ActivityType.ConvertingFile);
                logger.Debug("Unable to write data to ECUs memory");
                //_stallKeepAlive = false;
                //return false;
            }
            //10 F0 36 00 00 10 24 00 
            cmd = 0x0000000000360010; // 0x34 = upload data to ECU


            cmd |= (addressLow * 0x100000000000000);
            cmd |= (addressMiddle * 0x1000000000000);
            cmd |= (addressHigh * 0x10000000000);
            cmd |= (len * 0x100);
            //logger.Debug("send: " + cmd.ToString("X16"));

            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                logger.Debug("Couldn't send message");
            }
            // wait for response, should be 30 00 00 00 00 00 00 00
            data = 0;
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            data = response.getData();
            int numberOfFrames = (int)len / 7; // remnants?
            if (((int)len % 7) > 0) numberOfFrames++;
            byte iFrameNumber = 0x21;
            int txpnt = 0;
            if (data == 0x0000000000000030)
            {
                for (int i = 0; i < numberOfFrames; i++)
                {
                    cmd = 0x0000000000000000; // 0x34 = upload data to ECU
                    msg.setData(cmd);
                    msg.setCanData(iFrameNumber, 0);
                    msg.setCanData(memdata[txpnt++], 1);
                    msg.setCanData(memdata[txpnt++], 2);
                    msg.setCanData(memdata[txpnt++], 3);
                    msg.setCanData(memdata[txpnt++], 4);
                    msg.setCanData(memdata[txpnt++], 5);
                    msg.setCanData(memdata[txpnt++], 6);
                    msg.setCanData(memdata[txpnt++], 7);
                    iFrameNumber++;
                    if (!canUsbDevice.sendMessage(msg))
                    {
                        logger.Debug("Couldn't send message");
                    }
                    Thread.Sleep(1);
                    // send the data with 7 bytes at a time
                }
                m_canListener.setupWaitMessage(0x7E8);
                response = new CANMessage();
                response = m_canListener.waitMessage(timeoutP2ct);
                data = response.getData();
                logger.Debug("received: " + data.ToString("X8"));
            }
            _stallKeepAlive = false;
            return true;
        }

        private void SendKeepAlive()
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 2);
            ulong cmd = 0x0000000000003E01; // always 2 bytes
            msg.setData(cmd);
            msg.elmExpectedResponses = 1;
            //logger.Debug("KA sent");
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return;
            }
            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            //logger.Debug("received KA: " + response.getCanData(1).ToString("X2"));
        }

        public void GetSRAMSnapshot(object sender, DoWorkEventArgs workEvent)
        {
            BackgroundWorker bw = sender as BackgroundWorker;
            string filename = (string)workEvent.Argument;
        
            bool result = false;
            int retryCount = 0;
            int startAddress = 0x100000;
            int blockSize = 0x40;
            int bufpnt = 0;
            int saved_progress = 0;
            byte[] buf = new byte[0x8000];

            _stallKeepAlive = true;

            while (bufpnt < buf.Length - 1)
            {
                if (!canUsbDevice.isOpen())
                {
                    _stallKeepAlive = false;
                    workEvent.Result = false;
                    return;
                }

                byte[] readbuf = readMemory(startAddress, blockSize, out result);
                if (result)
                {

                    if (readbuf.Length == blockSize)
                    {
                        for (int j = 0; j < blockSize; j++)
                        {
                            buf[bufpnt++] = readbuf[j];
                        }
                    }
                    int percentage = (int)((float)100 * bufpnt / (float)buf.Length);
                    if (percentage > saved_progress)
                    {
                        CastProgressReadEvent(percentage);
                        saved_progress = percentage;
                    }
                    retryCount = 0;
                    startAddress += blockSize;
                }
                else
                {
                    CastInfoEvent("Frame dropped, retrying", ActivityType.DownloadingSRAM);
                    retryCount++;
                    if (retryCount == maxRetries)
                    {
                        CastInfoEvent("Failed to download SRAM content", ActivityType.DownloadingSRAM);
                        _stallKeepAlive = false;
                        workEvent.Result = false;
                    }
                }
                SendKeepAlive();
            }

            _stallKeepAlive = false;

            if (buf != null)
            {
                try
                {
                    File.WriteAllBytes(filename, buf);
                    CastInfoEvent("Snapshot done", ActivityType.DownloadingSRAM);
                    workEvent.Result = true;
                }
                catch (Exception ex)
                {
                    CastInfoEvent("Could not write file... " + ex.Message, ActivityType.DownloadingSRAM);
                    workEvent.Result = false;
                }
            }
            else
            {
                workEvent.Result = false;
            }
        }

        private byte getCanData(ulong m_data, uint a_index)
        {
            return (byte)(m_data >> (int)(a_index * 8));
        }

        private byte[] sendReadDataByLocalIdentifier(bool LegionMode, byte PCI, int address, int length, out bool success)
        {
            // we send: 0040000000002106
            // .. send: 06 21 80 00 00 00 00 00

            success = false;
            byte[] retData = new byte[length];
            if (!canUsbDevice.isOpen()) return retData;

            CANMessage msg = new CANMessage(0x7E0, 0, 7);
            //logger.Debug("Reading " + address.ToString("X8") + " len: " + length.ToString("X2"));

            // Legion mod
            /* ulong cmd = 0x0000000000002106;*/ // always 2 bytes
            ulong cmd = 0x0000000000002100;
            cmd += PCI;
            // Only used by Legion. Determine how many blocks to skip (Stuff filled with 0xFF)
            Blockstoskip = 0;

            ulong addressHigh = (uint)address & 0x0000000000FF0000;
            addressHigh /= 0x10000;
            ulong addressMiddle = (uint)address & 0x000000000000FF00;
            addressMiddle /= 0x100;
            ulong addressLow = (uint)address & 0x00000000000000FF;
            ulong len = (ulong)length;


            cmd |= (addressLow * 0x1000000000000);
            cmd |= (addressMiddle * 0x10000000000);
            cmd |= (addressHigh * 0x100000000);
            cmd |= (len * 0x10000); // << 2 * 8
            //logger.Debug("send: " + cmd.ToString("X16"));
            /*cmd |= (ulong)(byte)(address & 0x000000FF) << 4 * 8;
            cmd |= (ulong)(byte)((address & 0x0000FF00) >> 8) << 3 * 8;
            cmd |= (ulong)(byte)((address & 0x00FF0000) >> 2 * 8) << 2 * 8;
            cmd |= (ulong)(byte)((address & 0xFF000000) >> 3 * 8) << 8;*/
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            msg.elmExpectedResponses = 19; //in 19 messages there are 0x82 = 130 bytes of data, bootloader requests 0x80 =128 each time
            if (!canUsbDevice.sendMessage(msg))
            {
                logger.Debug("Couldn't send message");

            }
            // wait for max two messages to get rid of the alive ack message
            CANMessage response = new CANMessage();
            ulong data = 0;
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            data = response.getData();

            if (getCanData(data, 0) == 0x7E)
            {
                logger.Debug("Got 0x7E message as response to 0x21, ReadDataByLocalIdentifier command");
                success = false;
                return retData;
            }
            else if (response.getData() == 0x00000000)
            {
                logger.Debug("Get blank response message to 0x21, ReadDataByLocalIdentifier");
                success = false;
                return retData;
            }
            else if (getCanData(data, 0) == 0x03 && getCanData(data, 1) == 0x7F && getCanData(data, 2) == 0x23)
            {
                // reason was 0x31
                logger.Debug("No security access granted");
                RequestSecurityAccess(0);
                success = false;
                return retData;
            }
            else if (getCanData(data, 2) != 0x61 && getCanData(data, 1) != 0x61)
            {
                if (data == 0x0000000000007E01)
                {
                    // was a response to a KA.
                }
                logger.Debug("Incorrect response to 0x21, sendReadDataByLocalIdentifier.  Byte 2 was " + getCanData(data, 2).ToString("X2"));
                success = false;
                return retData;
            }
            //TODO: Check whether we need more than 2 bytes of data and wait for that many records after sending an ACK
            int rx_cnt = 0;
            byte frameIndex = 0x21;
            if (length > 4)
            {
                retData[rx_cnt++] = getCanData(data, 4);
                retData[rx_cnt++] = getCanData(data, 5);
                retData[rx_cnt++] = getCanData(data, 6);
                retData[rx_cnt++] = getCanData(data, 7);
                // in that case, we need more records from the ECU
                // Thread.Sleep(1);

                // Check if autoskip feature is enabled and a tag has been received
                if (!LegionMode || (getCanData(data, 3) == 0))
                {
                    SendAckMessageT8(); // send ack to request more bytes

                    //Thread.Sleep(1);
                    // now we wait for the correct number of records to be received
                    int m_nrFrameToReceive = ((length - 4) / 7);
                    if ((len - 4) % 7 > 0) m_nrFrameToReceive++;
                    //AddToCanTrace("Number of frames: " + m_nrFrameToReceive.ToString());
                    while (m_nrFrameToReceive > 0)
                    {
                        // response = new CANMessage();
                        //response.setData(0);
                        //response.setID(0);
                        // m_canListener.setupWaitMessage(0x7E8);
                        response = m_canListener.waitMessage(timeoutP2ct);
                        data = response.getData();
                        //AddToCanTrace("frame " + frameIndex.ToString("X2") + ": " + data.ToString("X16"));
                        if (frameIndex != getCanData(data, 0))
                        {
                            // sequence broken
                            logger.Debug("Received invalid sequenced frame " + frameIndex.ToString("X2") + ": " + data.ToString("X16"));
                            m_canListener.dumpQueue();
                            success = false;
                            return retData;
                        }
                        else if (data == 0)
                        {
                            logger.Debug("Received blank message while waiting for data");
                            success = false;
                            return retData;
                        }
                        frameIndex++;
                        if (frameIndex > 0x2F) frameIndex = 0x20;
                        // additional check for sequencing of frames
                        m_nrFrameToReceive--;
                        //AddToCanTrace("frames left: " + m_nrFrameToReceive.ToString());
                        // add the bytes to the receive buffer
                        //string checkLine = string.Empty;
                        for (uint fi = 1; fi < 8; fi++)
                        {
                            //checkLine += getCanData(data, fi).ToString("X2");
                            if (rx_cnt < retData.Length) // prevent overrun
                            {
                                retData[rx_cnt++] = getCanData(data, fi);
                            }
                        }
                        //AddToCanTrace("frame(2): " + checkLine);
                        //Thread.Sleep(1);

                    }
                }
                //Loader tagged package as filled with FF (Ie it's not necessary to send a go and receive the rest of the frame, we already know what it contains) 
                else
                {
                    success = true;
                    for (int i = 0; i < 0x80; i++)
                        retData[i] = 0xFF;

                    Blockstoskip = getCanData(data, 3);

                    logger.Debug("Skipping: " + (length*Blockstoskip).ToString() + " bytes");
                    return retData;
                }
            }
            else
            {
                if (retData.Length > rx_cnt) retData[rx_cnt++] = getCanData(data, 4);
                if (retData.Length > rx_cnt) retData[rx_cnt++] = getCanData(data, 5);
                if (retData.Length > rx_cnt) retData[rx_cnt++] = getCanData(data, 6);
                if (retData.Length > rx_cnt) retData[rx_cnt++] = getCanData(data, 7);
                //AddToCanTrace("received data: " + retData[0].ToString("X2"));
            }
            /*string line = address.ToString("X8") + " ";
            foreach (byte b in retData)
            {
                line += b.ToString("X2") + " ";
            }
            AddToCanTrace(line);*/
            success = true;

            return retData;
        }

        //KWP2000 can read more than 6 bytes at a time.. but for now we are happy with this
        private byte[] sendReadCommand(int address, int length, out bool success)
        {

            success = false;
            byte[] retData = new byte[length];
            if (!canUsbDevice.isOpen()) return retData;

            CANMessage msg = new CANMessage(0x7E0, 0, 7);
            //optimize reading speed for ELM
            if (length <= 3)
                msg.elmExpectedResponses = 1;
            //logger.Debug("Reading " + address.ToString("X8") + " len: " + length.ToString("X2"));
            ulong cmd = 0x0000000000002306; // always 2 bytes
            ulong addressHigh = (uint)address & 0x0000000000FF0000;
            addressHigh /= 0x10000;
            ulong addressMiddle = (uint)address & 0x000000000000FF00;
            addressMiddle /= 0x100;
            ulong addressLow = (uint)address & 0x00000000000000FF;
            ulong len = (ulong)length;


            cmd |= (addressLow * 0x100000000);
            cmd |= (addressMiddle * 0x1000000);
            cmd |= (addressHigh * 0x10000);
            cmd |= (len * 0x1000000000000);
            //logger.Debug("send: " + cmd.ToString("X16"));
            /*cmd |= (ulong)(byte)(address & 0x000000FF) << 4 * 8;
            cmd |= (ulong)(byte)((address & 0x0000FF00) >> 8) << 3 * 8;
            cmd |= (ulong)(byte)((address & 0x00FF0000) >> 2 * 8) << 2 * 8;
            cmd |= (ulong)(byte)((address & 0xFF000000) >> 3 * 8) << 8;*/
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                logger.Debug("Couldn't send message");

            }
            // wait for max two messages to get rid of the alive ack message
            CANMessage response = new CANMessage();
            ulong data = 0;
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            data = response.getData();

            if (getCanData(data, 0) == 0x7E)
            {
                logger.Debug("Got 0x7E message as response to 0x23, readMemoryByAddress command");
                success = false;
                return retData;
            }
            else if (response.getData() == 0x00000000)
            {
                logger.Debug("Get blank response message to 0x23, readMemoryByAddress");
                success = false;
                return retData;
            }
            else if (getCanData(data, 0) == 0x03 && getCanData(data, 1) == 0x7F && getCanData(data, 2) == 0x23 && getCanData(data, 3) == 0x31)
            {
                // reason was 0x31 RequestOutOfRange
                // memory address is either: invalid, restricted, secure + ECU locked
                // memory size: is greater than max
                logger.Debug("No security access granted");
                RequestSecurityAccess(0);
                success = false;
                return retData;
            }
            else if (getCanData(data, 0) == 0x03 && getCanData(data, 1) == 0x7F && getCanData(data, 2) == 0x23)
            {
                logger.Debug("readMemoryByAddress " + TranslateErrorCode(getCanData(data, 3)));
                success = false;
                return retData;
            }
            /*else if (getCanData(data, 0) != 0x10)
            {
                AddToCanTrace("Incorrect response message to 0x23, readMemoryByAddress. Byte 0 was " + getCanData(data, 0).ToString("X2"));
                success = false;
                return retData;
            }
            else if (getCanData(data, 1) != len + 4)
            {
                AddToCanTrace("Incorrect length data message to 0x23, readMemoryByAddress.  Byte 1 was " + getCanData(data, 1).ToString("X2"));
                success = false;
                return retData;
            }*/
            else if (getCanData(data, 2) != 0x63 && getCanData(data, 1) != 0x63)
            {
                if (data == 0x0000000000007E01)
                {
                    // was a response to a KA.
                }
                logger.Debug("Incorrect response to 0x23, readMemoryByAddress.  Byte 2 was " + getCanData(data, 2).ToString("X2"));
                success = false;
                return retData;
            }
            //TODO: Check whether we need more than 2 bytes of data and wait for that many records after sending an ACK
            int rx_cnt = 0;
            byte frameIndex = 0x21;
            if (length > 3)
            {
                retData[rx_cnt++] = getCanData(data, 6);
                retData[rx_cnt++] = getCanData(data, 7);
                // in that case, we need more records from the ECU
                // Thread.Sleep(1);
                SendAckMessageT8(); // send ack to request more bytes
                //Thread.Sleep(1);
                // now we wait for the correct number of records to be received
                int m_nrFrameToReceive = ((length - 2) / 7);
                if ((len - 2) % 7 > 0) m_nrFrameToReceive++;
                //AddToCanTrace("Number of frames: " + m_nrFrameToReceive.ToString());
                while (m_nrFrameToReceive > 0)
                {
                    // response = new CANMessage();
                    //response.setData(0);
                    //response.setID(0);
                    // m_canListener.setupWaitMessage(0x7E8);
                    response = m_canListener.waitMessage(timeoutP2ct);
                    data = response.getData();
                    //AddToCanTrace("frame " + frameIndex.ToString("X2") + ": " + data.ToString("X16"));
                    if (frameIndex != getCanData(data, 0))
                    {
                        // sequence broken
                        logger.Debug("Received invalid sequenced frame " + frameIndex.ToString("X2") + ": " + data.ToString("X16"));
                        m_canListener.dumpQueue();
                        success = false;
                        return retData;
                    }
                    else if (data == 0)
                    {
                        logger.Debug("Received blank message while waiting for data");
                        success = false;
                        return retData;
                    }
                    frameIndex++;
                    // additional check for sequencing of frames
                    m_nrFrameToReceive--;
                    //AddToCanTrace("frames left: " + m_nrFrameToReceive.ToString());
                    // add the bytes to the receive buffer
                    //string checkLine = string.Empty;
                    for (uint fi = 1; fi < 8; fi++)
                    {
                        //checkLine += getCanData(data, fi).ToString("X2");
                        if (rx_cnt < retData.Length) // prevent overrun
                        {
                            retData[rx_cnt++] = getCanData(data, fi);
                        }
                    }
                    //AddToCanTrace("frame(2): " + checkLine);
                    //Thread.Sleep(1);

                }

            }
            else
            {
                if (retData.Length > rx_cnt) retData[rx_cnt++] = getCanData(data, 5);
                if (retData.Length > rx_cnt) retData[rx_cnt++] = getCanData(data, 6);
                if (retData.Length > rx_cnt) retData[rx_cnt++] = getCanData(data, 7);
                //AddToCanTrace("received data: " + retData[0].ToString("X2"));
            }
            /*string line = address.ToString("X8") + " ";
            foreach (byte b in retData)
            {
                line += b.ToString("X2") + " ";
            }
            AddToCanTrace(line);*/
            success = true;

            return retData;
        }

        // This method _WILL_RETURN_NULL_ if it failed!
        // Todo: Worth implementing that ELM 2.0 expected count thing?
        private byte[] sendReadCommand_24bit(int address, int length, int blockSize, bool ShowProgress)
        {
            byte[] retData = new byte[length];
            int retryDropped = 3;
            int toReceive;
            int originPos = 0;
            int originAddress = address;
            int oldPercentage = 0;

            // Whether or not the ECU can handle it is up to you. This cap is only to prevent this code from overflowing the length
            if (blockSize > 0xff8)
                blockSize = 0xff8;

            if (ShowProgress == true)
                CastProgressReadEvent(0);

            if (canUsbDevice.isOpen() == false)
            {
                logger.Debug("Adapter is not connected");
                return null;
            }

            if (sw.IsRunning == false)
            {
                sw.Reset();
                sw.Start();
            }

            while (originPos < length)
            {
                if (sw.ElapsedMilliseconds > 500)
                {
                    SendKeepAlive();
                    sw.Restart();
                }

                CANMessage msg = new CANMessage(0x7E0, 0, 7);

                address = originAddress;
                int pos = originPos;
                int ExtraFrameCount = 0;

                if (ShowProgress == true)
                {
                    int percentage = (int)((double)((double)pos * 100.0) / length);

                    if (percentage > (oldPercentage + 4) || percentage < oldPercentage)
                    {
                        oldPercentage = percentage;
                        CastProgressReadEvent(percentage);
                    }
                }

                ulong cmd = 0x2306;
                cmd |= ((ulong)address & 0xff0000);
                cmd |= (((ulong)address & 0x00ff00) << 16);
                cmd |= (((ulong)address & 0x0000ff) << 32);

                // This is only the ASKED size. ECU may or may not throw a curved one in response to this size
                if (length > blockSize)
                {
                    cmd |= (((ulong)blockSize & 0x00ff) << 48);
                    cmd |= (((ulong)blockSize & 0xff00) << 32);
                }
                else
                {
                    cmd |= (((ulong)length & 0x00ff) << 48);
                    cmd |= (((ulong)length & 0xff00) << 32);
                }

                // Dump queue (Causes crash)
                // m_canListener.dumpQueue();
                // m_canListener.FlushQueue();

                msg.setData(cmd);
                m_canListener.setupWaitMessage(0x7E8);
                if (!canUsbDevice.sendMessage(msg))
                {
                    logger.Debug("Couldn't send message");
                    return null;
                }

                CANMessage response = m_canListener.waitMessage(timeoutP2ct);
                ulong data = response.getData();

                // Skip junk
                while ((data & 0xff) == 0x30 ||
                       ((data & 0xff) < 4 && (data & 0xff00) == 0x7e00))
                {
                    response = m_canListener.waitMessage(timeoutP2ct);
                    data = response.getData();
                }

                // Multi-frame response
                if ((data & 0xf0) == 0x10)
                {
                    // 1x, xx, 63, [ addr 23:16, 15:8, 7:0 ], .., ..
                    // < 30, 00 >
                    // 2x, .., .., .., .., .., .., ..
                    toReceive = ((int)data & 0x0f) << 8;
                    toReceive |= (((int)data >> 8) & 0xff);
                    data >>= 16; // Skip PCI

                    // ECU must be drunk if it tries to send a multi-frame message where the total length is less than 7
                    // There would be no need to send it as multi in that case!
                    if (toReceive < 7)
                    {
                        logger.Debug("Unexpected length in response");
                        return null;
                    }

                    ExtraFrameCount = (toReceive - 6) / 7;
                    if (((toReceive - 6) % 7) > 0)  ExtraFrameCount++;
                }
                // Single-frame
                else if ((data & 0xff) < 8 && (data & 0xff) > 4)
                {
                    // 0x, 63, [ addr 23:16, 15:8, 7:0 ], .., .., ..
                    toReceive = ((int)data & 0x0f);
                    data >>= 8; // Skip PCI
                }
                // Header is malformed or no response
                else
                {
                    if (retryDropped <= 0)
                    {
                        if (data == 0)
                            logger.Debug("No data received");
                        else
                            logger.Debug("Unexpected PCI in frame");

                        return null;
                    }
                    Thread.Sleep(250);
                    m_canListener.FlushQueue();
                    retryDropped--;
                    continue;
                }

                // Response for something else??
                if ((data & 0xff) != 0x63 &&
                    (data & 0xffff) != 0x237f)
                {
                    if (retryDropped <= 0)
                    {
                        logger.Debug("Unexpected response");
                        return null;
                    }
                    Thread.Sleep(250);
                    m_canListener.FlushQueue();
                    retryDropped--;
                    continue;
                }
                // Actively refused by ECU
                else if ((data & 0xff) == 0x7f)
                {
                    logger.Debug("readMemoryByAddress failed with " + TranslateErrorCode((byte)((uint)data>>16)));
                    return null;
                }

                // Remove service response id from data
                data >>= 8;

                // Verify received address
                int receivedAddress = 0;
                for (int i = 0; i < 3; i++)
                {
                    receivedAddress <<= 8;
                    receivedAddress |= ((int)data & 0xff);
                    data >>= 8;
                }

                if (receivedAddress != address)
                {
                    logger.Debug("Unexpected address or length of response");
                    return null;
                }

                // Negate service response byte and 24-bit address bytes
                toReceive -= 4;

                // Single message
                if (ExtraFrameCount == 0)
                {
                    for (int i = 0; i < toReceive && pos < length; i++)
                    {
                        retData[pos++] = (byte)data;
                        data >>= 8;
                    }

                    originAddress += toReceive;
                    originPos += toReceive;
                }
                // Multi-frame
                else
                {
                    for (int i = 0; i < 2 && pos < length; i++)
                    {
                        retData[pos++] = (byte)data;
                        data >>= 8;
                    }

                    int originToRec = toReceive;
                    toReceive -= 2;
                    uint step = 0x21;

                    if ((canUsbDevice is CANELM327Device) == false)
                        SendMessage(0x7E0, 0x0000000000000030);

                    while (ExtraFrameCount > 0)
                    {
                        response = m_canListener.waitMessage(timeoutP2ct);
                        data = response.getData();
                        ExtraFrameCount--;

                        // Skip junk
                        while ((data & 0xff) == 0x30 ||
                               ((data & 0xff) < 4 && (data & 0xff00) == 0x7e00))
                        {
                            response = m_canListener.waitMessage(timeoutP2ct);
                            data = response.getData();
                        }

                        if ((data & 0xff) != step)
                        {
                            if (retryDropped <= 0)
                            {
                                logger.Debug("Unexpected stepper");
                                return null;
                            }

                            // Ought to be enough time to make sure no errant packets are received during retry
                            Thread.Sleep(250);
                            m_canListener.FlushQueue();
                            retryDropped--;
                            originToRec = 0;
                            break;
                        }

                        step++;
                        step &= 0x2f;

                        for (int i = 0; i < 7 && pos < length && i < toReceive; i++)
                        {
                            data >>= 8;
                            retData[pos++] = (byte)data;
                        }

                        // This value is thrashed any way so there is no harm in underflowing after the last data
                        toReceive -= 7;
                    }

                    originAddress += originToRec;
                    originPos += originToRec;
                }
            }

            if (ShowProgress == true)
                CastProgressReadEvent(100);

            return retData;
        }

        public bool atWriteByAddress(int address, byte[] memdata, int blockSize, bool ShowProgress)
        {
            CANMessage response;
            ulong data;
            int retries = 3;
            int oldPercentage = 0;
            int origPos = 0;
            int chunk;

            // The frame supports a 12-bit size but the req itself takes a secondary 8-bit size
            if (blockSize > 255)
                blockSize = 255;

            if (ShowProgress == true)
                CastProgressWriteEvent(0);

            if (canUsbDevice.isOpen() == false)
            {
                logger.Debug("Adapter is not connected");
                return false;
            }

            if (sw.IsRunning == false)
            {
                sw.Reset();
                sw.Start();
            }

            while (origPos < memdata.Length)
            {
                if (sw.ElapsedMilliseconds > 500)
                {
                    SendKeepAlive();
                    sw.Restart();
                }

                // m_canListener.FlushQueue();

                int pos = origPos;
                if ((memdata.Length - pos) >= blockSize)
                    chunk = blockSize;
                else
                    chunk = memdata.Length - pos;

                if (ShowProgress == true)
                {
                    int percentage = (int)((double)((double)pos * 100.0) / memdata.Length);

                    if (percentage > (oldPercentage + 4) || percentage < oldPercentage)
                    {
                        oldPercentage = percentage;
                        CastProgressWriteEvent(percentage);
                    }
                }

                if (chunk < 2)
                {
                    // Single
                    // 0x, 3b, 15, [ addr 23:16, 15:8, 7:0 ], 01, ..
                    ulong cmd = 0x01000000153b07;
                    cmd |= (((ulong)address & 0xff0000) << 8);
                    cmd |= (((ulong)address & 0x00ff00) << 24);
                    cmd |= (((ulong)address & 0x0000ff) << 40);
                    cmd |= ((ulong)memdata[pos++] << 56);

                    CANMessage msg = new CANMessage(0x7E0, 0, 8);
                    msg.setData(cmd);
                    if (!canUsbDevice.sendMessage(msg))
                    {
                        logger.Debug("Couldn't send message");
                        return false;
                    }
                }
                else
                {
                    // Multi
                    // Add REQ, subprm, 24-bit address and embedded size to total length
                    int framSize = chunk + 6;

                    // 1x, xx, 3b, 15, [ addr 23:16, 15:8, 7:0 ], xx
                    ulong cmd = 0x153b0010;
                    cmd |= (((ulong)framSize & 0xff) << 8);
                    cmd |= (((ulong)framSize >> 8) & 0x0f);
                    cmd |= (((ulong)address & 0xff0000) << 16);
                    cmd |= (((ulong)address & 0x00ff00) << 32);
                    cmd |= (((ulong)address & 0x0000ff) << 48);
                    cmd |= (((ulong)chunk & 0x0ff) << 56);

                    CANMessage msg = new CANMessage(0x7E0, 0, 8);
                    msg.setData(cmd);
                    if (!canUsbDevice.sendMessage(msg))
                    {
                        logger.Debug("Couldn't send message");
                        return false;
                    }

                    response = m_canListener.waitMessage(timeoutP2ct);
                    data = response.getData();

                    while (((data & 0xff) < 4 && (data & 0xff00) == 0x7e00))
                    {
                        response = m_canListener.waitMessage(timeoutP2ct);
                        data = response.getData();
                    }

                    if ((data & 0xff) != 0x30)
                    {
                        if (retries <= 0)
                        {
                            logger.Debug("No goAhead seen");
                            return false;
                        }
                        Thread.Sleep(250);
                        m_canListener.FlushQueue();
                        retries--;
                        continue;
                    }

                    // Now, finally time to send the rest of it
                    int frameCnt = chunk / 7;
                    if ((chunk % 7) > 0) frameCnt++;
                    uint step = 0x21;

                    while (frameCnt > 0)
                    {
                        frameCnt--;

                        cmd = step++;
                        step &= 0x2f;

                        for (int i = 0; i < 7 && i < chunk; i++)
                        {
                            cmd |= ((ulong)memdata[pos++] << ((i + 1) * 8));
                        }

                        // This one is thrashed so underflow is of no concern
                        chunk -= 7;

                        msg.setData(cmd);
                        if (!canUsbDevice.sendMessage(msg))
                        {
                            logger.Debug("Couldn't send message");
                            return false;
                        }
                        // What would be a sane value?
                        // It's already faster than the read procdeure so some extra shouldn't hurt
                        Thread.Sleep(5);
                    }
                }

                response = m_canListener.waitMessage(timeoutP2ct);
                data = response.getData();

                // The weirdo I played with had really found its calling in life...
                // There's goAheads for EVERYONE! ..and yet some
                while ((data & 0xff) == 0x30 ||
                    ((data & 0xff) < 3 && (data & 0xff00) == 0x7e00))
                {
                    response = m_canListener.waitMessage(timeoutP2ct);
                    data = response.getData();
                }

                if ((data & 0xff00) != 0x7b00)
                {
                    if (retries <= 0)
                    {
                        logger.Debug("Unexpected stepper");
                        return false;
                    }
                    retries--;
                    continue;
                }
                else
                {
                    address += (pos - origPos);
                    origPos = pos;
                }
            }

            if (ShowProgress == true)
                CastProgressWriteEvent(100);

            SendKeepAlive();
            return true;
        }

        private byte[] atReadSymByIdx(UInt16 idx)
        {
            int retryDropped = 3;

            if (canUsbDevice.isOpen() == false)
            {
                logger.Debug("Adapter is not connected");
                return null;
            }

            if (sw.IsRunning == false)
            {
                sw.Reset();
                sw.Start();
            }

            while (true)
            {
            retryByIndex:
                if (sw.ElapsedMilliseconds > 500)
                {
                    SendKeepAlive();
                    sw.Restart();
                }

                // 04, 1a, 19, [ idx 15:8, 7:0 ],
                ulong cmd = 0x191a04;
                cmd |= (((ulong)idx & 0xff00) << 16);
                cmd |= (((ulong)idx & 0x00ff) << 32);

                CANMessage msg = new CANMessage(0x7E0, 0, 8);
                msg.setData(cmd);
                m_canListener.setupWaitMessage(0x7E8);
                if (!canUsbDevice.sendMessage(msg))
                {
                    logger.Debug("Couldn't send message");
                    return null;
                }

                CANMessage response = m_canListener.waitMessage(timeoutP2ct);
                ulong data = response.getData();

                // Skip junk
                while ((data & 0xff) == 0x30 ||
                       ((data & 0xff) < 4 && (data & 0xff00) == 0x7e00))
                {
                    response = m_canListener.waitMessage(timeoutP2ct);
                    data = response.getData();
                }

                int toReceive = 0;
                int ExtraFrameCount = 0;

                // Multi-frame response
                if ((data & 0xf0) == 0x10)
                {
                    // 1x, xx, 5A, 19, [ idx 15:8, 7:0 ], .., ..
                    // < 30, 00 >
                    // 2x, .., .., .., .., .., .., ..
                    toReceive = ((int)data & 0x0f) << 8;
                    toReceive |= (((int)data >> 8) & 0xff);
                    data >>= 16; // Skip PCI

                    // ECU must be drunk if it tries to send a multi-frame message where the total length is less than 7
                    // There would be no need to send it as multi in that case!
                    if (toReceive < 7)
                    {
                        logger.Debug("Unexpected length in response");
                        return null;
                    }

                    ExtraFrameCount = (toReceive - 6) / 7;
                    if (((toReceive - 6) % 7) > 0) ExtraFrameCount++;
                }
                // Single-frame
                else if ((data & 0xff) < 8 && (data & 0xff) > 4)
                {
                    // 0x, 5A, 19, [ idx 15:8, 7:0 ], .., .., ..
                    toReceive = ((int)data & 0x0f);
                    data >>= 8; // Skip PCI
                }
                // Header is malformed or no response
                else
                {
                    if (retryDropped <= 0)
                    {
                        if (data == 0)
                            logger.Debug("No data received");
                        else
                            logger.Debug("Unexpected PCI in frame");

                        return null;
                    }
                    Thread.Sleep(250);
                    m_canListener.FlushQueue();
                    retryDropped--;
                    continue;
                }

                // Response for something else??
                if ((data & 0xff) != 0x5a &&
                    (data & 0xffff) != 0x1a7f)
                {
                    if (retryDropped <= 0)
                    {
                        logger.Debug("Unexpected response");
                        return null;
                    }
                    Thread.Sleep(250);
                    m_canListener.FlushQueue();
                    retryDropped--;
                    continue;
                }
                // Actively refused by ECU
                else if ((data & 0xff) == 0x7f)
                {
                    logger.Debug("readMemoryByAddress failed with " + TranslateErrorCode((byte)((uint)data >> 16)));
                    return null;
                }

                // Remove service response id and subprm from data
                data >>= 16;

                // None of these should ever happen:
                // Verify received idx and length
                int recIdx = 0;
                for (int i = 0; i < 2; i++)
                {
                    recIdx <<= 8;
                    recIdx |= ((int)data & 0xff);
                    data >>= 8;
                }

                if (toReceive < 5 || recIdx != idx)
                {
                    logger.Debug("Unexpected address or length of response");
                    return null;
                }

                // Negate service response byte, subprm and 16-bit idx
                toReceive -= 4;

                int pos = 0;
                byte[] retData = new byte[toReceive];

                // Single message
                if (ExtraFrameCount == 0)
                {
                    for (int i = 0; i < toReceive; i++)
                    {
                        retData[pos++] = (byte)data;
                        data >>= 8;
                    }

                    return retData;
                }
                // Multi-frame
                else
                {
                    for (int i = 0; i < 2; i++)
                    {
                        retData[pos++] = (byte)data;
                        data >>= 8;
                    }

                    uint step = 0x21;

                    if ((canUsbDevice is CANELM327Device) == false)
                        SendMessage(0x7E0, 0x0000000000000130);

                    while (ExtraFrameCount > 0)
                    {
                        response = m_canListener.waitMessage(timeoutP2ct);
                        data = response.getData();
                        ExtraFrameCount--;

                        // Skip junk
                        while ((data & 0xff) == 0x30 ||
                               ((data & 0xff) < 4 && (data & 0xff00) == 0x7e00))
                        {
                            response = m_canListener.waitMessage(timeoutP2ct);
                            data = response.getData();
                        }

                        if ((data & 0xff) != step)
                        {
                            if (retryDropped <= 0)
                            {
                                logger.Debug("Unexpected stepper");
                                return null;
                            }

                            Thread.Sleep(250);
                            m_canListener.FlushQueue();
                            retryDropped--;
                            goto retryByIndex;
                        }

                        step++;
                        step &= 0x2f;

                        for (int i = 0; i < 7 && pos < toReceive; i++)
                        {
                            data >>= 8;
                            retData[pos++] = (byte)data;
                        }
                    }
                    return retData;
                }
            }
        }

        //////////////////////////////////////////////////////////
        // The following methods are all used to retrieve symbol data via dynamic ids. -One of Trionic 8's many tricks
        // What must be known about this mode is its limitations:
        // 1: No more than 100 ids can be in that list at the same time.
        // 2: No matter the count. Total data size can not exceed 255 bytes

        /// <summary>
        /// 0x17 - Reset the ECU's local list of dynamic ids
        /// 3b [17] F0 (04)
        /// </summary>
        /// <returns></returns>
        private bool atResetDynList()
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 5);
            int retryDropped = 3;

            if (canUsbDevice.isOpen() == false)
            {
                logger.Debug("Adapter is not connected");
                return false;
            }

            m_canListener.setupWaitMessage(0x7E8);
            msg.setData(0x04f0173b04);

            while (retryDropped > 0)
            {
                if (!canUsbDevice.sendMessage(msg))
                {
                    logger.Debug("Couldn't send message");
                    return false;
                }

                CANMessage response = m_canListener.waitMessage(timeoutP2ct);
                ulong data = response.getData();

                if ((data & 0xffffff00) == 0xf0177b00)
                    return true;


                Thread.Sleep(25);
                retryDropped--;
                m_canListener.FlushQueue();
            }

            return false;
        }

        /// <summary>
        /// 0x17 - Add ONE dynamic symbol to the ECU's local list. Highly dangerous unless you have 100% control over your local list
        /// 3b [17] F0 [(80) ?? ?? ?? iH iL]
        /// 
        /// Note: There is no way to configure id of each symbol index so it's easy to get out of sync if you don't keep tabs on things
        /// Note2: It is STRONGLY adviced to reset the local state and send a fresh list to the ECU if this method fails 
        /// </summary>
        /// <returns></returns>
        private bool atAddDynSym_ByIdx(UInt16 idx)
        {
            if (canUsbDevice.isOpen() == false)
            {
                logger.Debug("Adapter is not connected");
                return false;
            }

            CANMessage msg = new CANMessage(0x7E0, 0, 8);
            ulong cmd = 0x80f0173b0910;
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                logger.Debug("Couldn't send message");
                return false;
            }

            CANMessage response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();

            if ((data & 0xff) != 0x30)
            {
                logger.Debug("No goAhead received");
                return false;
            }

            cmd = 0x21;
            cmd |= (((ulong)idx & 0xff00) << 8);
            cmd |= (((ulong)idx & 0x00ff) << 24);
            msg.setData(cmd);
            if (!canUsbDevice.sendMessage(msg))
            {
                logger.Debug("Couldn't send message");
                return false;
            }

            response = m_canListener.waitMessage(timeoutP2ct);
            data = response.getData();

            return ((data & 0xffffff00) == 0xf0177b00);
        }

        /// <summary>
        /// 0x17 - Configure dynamic symbols to be retrieved by atReadDynSyms. (By Index)
        /// 3b [17] F0 [(80) ?? ?? ?? iH iL] .. [(80) ?? ?? ?? iH iL] ..
        /// </summary>
        /// <param name="dynList">List of symbol indexes</param>
        /// <returns></returns>
        private bool atConfigureDynSyms_ByIdx(List<UInt16> dynList)
        {
            if (dynList == null || dynList.Count == 0 || dynList.Count > 99)
            {
                logger.Debug("Empty dynamic list or too many items");
                atResetDynList();
                return false;
            }

            if (canUsbDevice.isOpen() == false)
            {
                logger.Debug("Adapter is not connected");
                return false;
            }

            byte[] buffer = new byte[dynList.Count * 6];
            const int chunkCount = 9;
            int retries = 3;
            int origin = 0;
            int endPos;

            // [(80) ?? ?? ?? iH iL] ..
            for (int i = 0; i < buffer.Length; i += 6)
            {
                buffer[i] = 0x80;
                buffer[i+1] = buffer[i+2] = buffer[i+3] = 0;
                buffer[i+4] = (byte)(dynList[i/6] >> 8);
                buffer[i+5] = (byte)(dynList[i/6]);
            }

            atResetDynList();
            m_canListener.setupWaitMessage(0x7E8);

            if (sw.IsRunning == false)
            {
                sw.Reset();
                sw.Start();
            }

            while (origin < buffer.Length)
            {
                if (sw.ElapsedMilliseconds > 1000)
                {
                    SendKeepAlive();
                    sw.Restart();
                }

                // 10 xx 3b [17] F0
                ulong cmd = 0xf0173b0010;
                int pos = origin;

                // Only send "chunkCount" number of items in one go
                if (((buffer.Length - pos) / 6) > chunkCount)
                    endPos = pos + (chunkCount * 6);
                else
                    endPos = buffer.Length;

                // Append total size
                int reqSz = (endPos - pos) + 3;
                cmd |= (((ulong)reqSz & 0xff) << 8);
                cmd |= (((ulong)reqSz >> 8) & 0x0f);

                // Append first couple of bytes
                for (int i = 5; i < 8; i++)
                {
                    cmd |= ((ulong)buffer[pos++] << (i * 8));
                }

                CANMessage msg = new CANMessage(0x7E0, 0, 8);
                msg.setData(cmd);
                if (!canUsbDevice.sendMessage(msg))
                {
                    logger.Debug("Couldn't send message");
                    return false;
                }

                CANMessage response = m_canListener.waitMessage(timeoutP2ct);
                ulong data = response.getData();

                while (((data & 0xff) < 4 && (data & 0xff00) == 0x7e00))
                {
                    response = m_canListener.waitMessage(timeoutP2ct);
                    data = response.getData();
                }

                if ((data & 0xff) != 0x30)
                {
                    Thread.Sleep(250);
                    m_canListener.FlushQueue();
                    atResetDynList();
                    if (retries <= 0)
                    {
                        logger.Debug("No goAhead received");
                        return false;
                    }
                    origin = 0;
                    retries--;
                    continue;
                }

                uint step = 0x21;
                while (pos < endPos)
                {
                    cmd = step++;
                    step &= 0x2f;
                    for (int i = 1; i < 8 && pos < endPos; i++)
                    {
                        cmd |= ((ulong)buffer[pos++] << (i * 8));
                    }

                    // Slow and steady.
                    Thread.Sleep(5);

                    msg.setData(cmd);
                    if (!canUsbDevice.sendMessage(msg))
                    {
                        logger.Debug("Couldn't send message");
                        return false;
                    }
                }

                response = m_canListener.waitMessage(timeoutP2ct);
                data = response.getData();

                // Skip junk
                while ((data & 0xff) == 0x30 ||
                       ((data & 0xff) < 3 && (data & 0xff00) == 0x7e00))
                {
                    response = m_canListener.waitMessage(timeoutP2ct);
                    data = response.getData();
                }

                // Hard to know if it failed due to dropped packets or outright refusal so treat everything as dropped
                if ((data & 0xffffff00) != 0xf0177b00)
                {
                    Thread.Sleep(250);
                    m_canListener.FlushQueue();
                    atResetDynList();
                    if (retries <= 0)
                    {
                        logger.Debug("Unexpected answer");
                        return false;
                    }
                    origin = 0;
                    retries--;
                    continue;
                }

                origin = pos;
            }
            return true;
        }

        /// <summary>
        /// 0x17 - Add ONE dynamic symbol to the ECU's local list. Highly dangerous unless you have 100% control over your local list
        /// 3b [17] F0 [(03) ?? sZ aH aM aL]
        /// 
        /// Note: There is no way to configure id of each symbol index so it's easy to get out of sync if you don't keep tabs on things
        /// Note2: It is STRONGLY adviced to reset the local state and send a fresh list to the ECU if this method fails 
        /// </summary>
        /// <returns></returns>
        private bool atAddDynSym_ByAddr(int address, byte size)
        {
            if (canUsbDevice.isOpen() == false)
            {
                logger.Debug("Adapter is not connected");
                return false;
            }

            CANMessage msg = new CANMessage(0x7E0, 0, 8);
            // 10 xx 3b f0 03 ?? sZ aH -> 21 aM aL
            ulong cmd = 0x03f0173b0910;
            cmd |= (((ulong)address & 0xff0000) << 40);
            cmd |= (((ulong)size) << 48);
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                logger.Debug("Couldn't send message");
                return false;
            }

            
            CANMessage response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();

            if ((data & 0xff) != 0x30)
            {
                logger.Debug("No goAhead received");
                return false;
            }

            cmd = (0x0021 | ((ulong)address & 0x00ff00));
            cmd |= (((ulong)address & 0x0000ff) << 16);
            msg.setData(cmd);
            if (!canUsbDevice.sendMessage(msg))
            {
                logger.Debug("Couldn't send message");
                return false;
            }

            response = m_canListener.waitMessage(timeoutP2ct);
            data = response.getData();

            return ((data & 0xffffff00) == 0xf0177b00);
        }

        /// <summary>
        /// 0x17 - Configure dynamic symbols to be retrieved by atReadDynSyms. (By address)
        /// 3b [17] F0 [(03) ?? sZ aH aM aL] .. [(03) ?? sZ aH aM aL] ..
        /// </summary>
        /// <param name="dynList">List of symbol addresses and sizes</param>
        /// <returns></returns>
        private bool atConfigureDynSyms_ByAddr(List<dynAddrHelper> dynList)
        {
            if (dynList == null || dynList.Count == 0 || dynList.Count > 99)
            {
                logger.Debug("Empty dynamic list or too many items");
                atResetDynList();
                return false;
            }

            if (canUsbDevice.isOpen() == false)
            {
                logger.Debug("Adapter is not connected");
                return false;
            }

            byte[] buffer = new byte[dynList.Count * 6];
            const int chunkCount = 9;
            int retries = 3;
            int origin = 0;
            int endPos;

            // [(03) ?? sZ aH aM aL] ..
            for (int i = 0; i < buffer.Length; i += 6)
            {
                buffer[i] = 0x03;
                buffer[i+1] = 0;
                buffer[i+2] = dynList[i/6].size;
                buffer[i+3] = (byte)(dynList[i/6].address >> 16);
                buffer[i+4] = (byte)(dynList[i/6].address >> 8);
                buffer[i+5] = (byte)(dynList[i/6].address);
            }

            atResetDynList();
            m_canListener.setupWaitMessage(0x7E8);

            if (sw.IsRunning == false)
            {
                sw.Reset();
                sw.Start();
            }

            while (origin < buffer.Length)
            {
                if (sw.ElapsedMilliseconds > 1000)
                {
                    SendKeepAlive();
                    sw.Restart();
                }

                // 10 xx 3b [17] F0
                ulong cmd = 0xf0173b0010;
                int pos = origin;

                // Only send "chunkCount" number of items in one go
                if (((buffer.Length - pos) / 6) > chunkCount)
                    endPos = pos + (chunkCount * 6);
                else
                    endPos = buffer.Length;

                // Append total size
                int reqSz = (endPos - pos) + 3;
                cmd |= (((ulong)reqSz & 0xff) << 8);
                cmd |= (((ulong)reqSz >> 8) & 0x0f);

                // Append first couple of bytes
                for (int i = 5; i < 8; i++)
                {
                    cmd |= ((ulong)buffer[pos++] << (i * 8));
                }

                CANMessage msg = new CANMessage(0x7E0, 0, 8);
                msg.setData(cmd);
                if (!canUsbDevice.sendMessage(msg))
                {
                    logger.Debug("Couldn't send message");
                    return false;
                }

                CANMessage response = m_canListener.waitMessage(timeoutP2ct);
                ulong data = response.getData();

                while (((data & 0xff) < 4 && (data & 0xff00) == 0x7e00))
                {
                    response = m_canListener.waitMessage(timeoutP2ct);
                    data = response.getData();
                }

                if ((data & 0xff) != 0x30)
                {
                    Thread.Sleep(250);
                    m_canListener.FlushQueue();
                    atResetDynList();
                    if (retries <= 0)
                    {
                        logger.Debug("No goAhead received");
                        return false;
                    }
                    origin = 0;
                    retries--;
                    continue;
                }

                uint step = 0x21;
                while (pos < endPos)
                {
                    cmd = step++;
                    step &= 0x2f;
                    for (int i = 1; i < 8 && pos < endPos; i++)
                    {
                        cmd |= ((ulong)buffer[pos++] << (i * 8));
                    }

                    // Slow and steady.
                    Thread.Sleep(5);

                    msg.setData(cmd);
                    if (!canUsbDevice.sendMessage(msg))
                    {
                        logger.Debug("Couldn't send message");
                        return false;
                    }
                }

                response = m_canListener.waitMessage(timeoutP2ct);
                data = response.getData();

                // Skip junk
                while ((data & 0xff) == 0x30 ||
                       ((data & 0xff) < 3 && (data & 0xff00) == 0x7e00))
                {
                    response = m_canListener.waitMessage(timeoutP2ct);
                    data = response.getData();
                }

                // Hard to know if it failed due to dropped packets or outright refusal so treat everything as dropped
                if ((data & 0xffffff00) != 0xf0177b00)
                {
                    Thread.Sleep(250);
                    m_canListener.FlushQueue();
                    atResetDynList();
                    if (retries <= 0)
                    {
                        logger.Debug("Unexpected answer");
                        return false;
                    }
                    origin = 0;
                    retries--;
                    continue;
                }

                origin = pos;
            }
            return true;
        }

        /// <summary>
        /// 0x18 - Read symbol data that has been configured through the dynamic mode
        /// 03 1a [18] F0
        /// </summary>
        /// <returns>null if it for some reason failed. Raw symbol data of dynamic ids</returns>
        private byte[] atReadDynSyms()
        {
            if (canUsbDevice.isOpen() == false)
            {
                logger.Debug("Adapter is not connected");
                return null;
            }

            int retries = 3;

            m_canListener.setupWaitMessage(0x7E8);

            if (sw.IsRunning == false)
            {
                sw.Reset();
                sw.Start();
            }

            while (true)
            {
            retryDynamic:
                if (sw.ElapsedMilliseconds > 1000)
                {
                    SendKeepAlive();
                    sw.Restart();
                }

                // 03 1a [18] F0
                ulong cmd = 0xf0181a03;

                CANMessage msg = new CANMessage(0x7E0, 0, 4);
                msg.setData(cmd);
                if (!canUsbDevice.sendMessage(msg))
                {
                    logger.Debug("Couldn't send message");
                    return null;
                }

                CANMessage response = m_canListener.waitMessage(timeoutP2ct);
                ulong data = response.getData();

                // Skip junk
                while ((data & 0xff) == 0x30 ||
                       ((data & 0xff) < 4 && (data & 0xff00) == 0x7e00))
                {
                    response = m_canListener.waitMessage(timeoutP2ct);
                    data = response.getData();
                }

                int toReceive = 0;
                int ExtraFrameCount = 0;

                // Multi-frame response
                if ((data & 0xf0) == 0x10)
                {
                    // 1x, xx, 5A, 18 ..
                    // < 30, 00 >
                    // 2x, .., .., .., .., .., .., ..
                    toReceive = ((int)data & 0x0f) << 8;
                    toReceive |= (((int)data >> 8) & 0xff);
                    data >>= 16; // Skip PCI

                    if (toReceive < 7)
                    {
                        logger.Debug("Unexpected length in response");
                        return null;
                    }

                    // Can read 6 bytes from this message
                    ExtraFrameCount = (toReceive - 6) / 7;
                    if (((toReceive - 6) % 7) > 0) ExtraFrameCount++;
                }
                // Single-frame
                else if ((data & 0xff) < 8 && (data & 0xff) > 2)
                {
                    // 0x, 5A, 18 ..
                    toReceive = ((int)data & 0x0f);
                    data >>= 8; // Skip PCI
                }
                // Header is malformed or no response
                else
                {
                    if (retries <= 0)
                    {
                        if (data == 0)
                            logger.Debug("No data received");
                        else
                            logger.Debug("Unexpected PCI in frame");

                        return null;
                    }
                    Thread.Sleep(250);
                    m_canListener.FlushQueue();
                    retries--;
                    continue;
                }

                // Response for something else??
                if ((data & 0xffff) != 0x185a &&
                    (data & 0xffff) != 0x1a7f)
                {
                    if (retries <= 0)
                    {
                        logger.Debug("Unexpected response");
                        return null;
                    }
                    Thread.Sleep(250);
                    m_canListener.FlushQueue();
                    retries--;
                    continue;
                }
                // Actively refused by ECU
                else if ((data & 0xff) == 0x7f)
                {
                    logger.Debug("FetchDynamicList failed with " + TranslateErrorCode((byte)((uint)data >> 16)));
                    return null;
                }

                // Skip past service response ID and subprm
                data >>= 16;

                // Negate service response byte, subprm
                toReceive -= 2;

                int pos = 0;
                byte[] retData = new byte[toReceive];

                // Single message
                if (ExtraFrameCount == 0)
                {
                    for (int i = 0; i < toReceive; i++)
                    {
                        retData[pos++] = (byte)data;
                        data >>= 8;
                    }

                    return retData;
                }
                // Multi-frame
                else
                {
                    for (int i = 0; i < 4; i++)
                    {
                        retData[pos++] = (byte)data;
                        data >>= 8;
                    }

                    uint step = 0x21;

                    // Tell ECU to go full bore instead of demanding acks after EVERY. SINGLE. MESSAGE...
                    if ((canUsbDevice is CANELM327Device) == false)
                    {
                        cmd = 0x0030;
                        msg = new CANMessage(0x7E0, 0, 3);
                        msg.setData(cmd);
                        canUsbDevice.sendMessage(msg);
                    }

                    while (ExtraFrameCount > 0)
                    {
                        response = m_canListener.waitMessage(timeoutP2ct);
                        data = response.getData();
                        ExtraFrameCount--;

                        // Skip junk
                        while ((data & 0xff) == 0x30 ||
                               ((data & 0xff) < 4 && (data & 0xff00) == 0x7e00))
                        {
                            response = m_canListener.waitMessage(timeoutP2ct);
                            data = response.getData();
                        }

                        if ((data & 0xff) != step)
                        {
                            if (retries <= 0)
                            {
                                logger.Debug("Unexpected stepper");
                                return null;
                            }

                            Thread.Sleep(250);
                            m_canListener.FlushQueue();
                            retries--;
                            goto retryDynamic;
                        }

                        step++;
                        step &= 0x2f;

                        for (int i = 0; i < 7 && pos < toReceive; i++)
                        {
                            data >>= 8;
                            retData[pos++] = (byte)data;
                        }
                    }

                    return retData;
                }
            }
        }

        //KWP2000 can read more than 6 bytes at a time.. but for now we are happy with this
        private byte[] sendReadCommandAcDelco(int address, int length, out bool success)
        {
            success = false;
            byte[] retData = new byte[length];
            uint rx_cnt = 0;


            // if (!canUsbDevice.isOpen()) return retData;

            CANMessage msg = new CANMessage(0x7E0, 0, 8);
            //optimize reading speed for ELM
            /*if (length <= 3)
                msg.elmExpectedResponses = 1;*/
            //logger.Debug("Reading " + address.ToString("X8") + " len: " + length.ToString("X2"));
            ulong cmd = 0x0000000000002307; // always 2 bytes

            ulong SwappedAddr = ((ulong)address & 0xFF) << 24 |
                                (((ulong)address >> 8) & 0xFF) << 16 |
                                (((ulong)address >> 16) & 0xFF) << 8 |
                                (((ulong)address >> 24) & 0xFF);
            cmd |= SwappedAddr << 16;

            ulong SwappedLen = ((ulong)length & 0xFF) << 8 |
                              (((ulong)length >> 8) & 0xFF);
            cmd |= SwappedLen << 48;
            ulong len = (ulong)length;

            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                logger.Debug("Couldn't send message");

            }
            // wait for max two messages to get rid of the alive ack message
            CANMessage response = new CANMessage();
            response = m_canListener.waitMessage(100);
            ulong data = response.getData();



            // This is a single-frame response
            if ((getCanData(data, 0) & 0x10) == 0)
            {
                CastInfoEvent("Received single-frame. Implement!", ActivityType.ConvertingFile);
                return retData;
            }
            else
            {
                // 10 15 63 00fffdd8 7b

                // Check if this is a response to our requested address
                if (((data >> 24) & 0xFFFFFFFF) != SwappedAddr)
                {
                    CastInfoEvent("Received response to the wrong address!", ActivityType.ConvertingFile);
                    return retData;
                }

                // Write this the same way as in edc...
                // Note: Array length is hardcoded. Fix that too!
                else if (getCanData(data, 1) != length + 5)
                {
                    CastInfoEvent("Unexpected length", ActivityType.ConvertingFile);
                    return retData;
                }


                uint ExpStp = 0x21;
                retData[rx_cnt++] = getCanData(data, 7);

                // We got one byte from the first response..
                length--;

                // Some paranoia just in case the ECU is not behaving
                if (length > 0)
                {
                    // Setup a wait message BEFORE sending the goahead..
                    m_canListener.setupWaitMessage(0x7E8);
                    SendAckMessageT8();

                }

                while (length > 0)
                {
                    response = m_canListener.waitMessage(timeoutP2ct);
                    data = response.getData();

                    if (getCanData(data, 0) != ExpStp++)
                    {
                        CastInfoEvent("Dropped stepper", ActivityType.ConvertingFile);
                        Thread.Sleep(500);
                        m_canListener.FlushQueue();
                        return retData;
                    }
                    ExpStp &= 0x2F;

                    int toCopy = 7;
                    if (length < toCopy)
                        toCopy = length;

                    length -= toCopy;

                    for (uint i = 0; i < toCopy; i++)
                    {
                        retData[rx_cnt++] = getCanData(data, 1 + i);
                    }
                }

                success = true;
                return retData;

            }
        }

        public float readBatteryVoltageOBDII()
        {
            // send message to read DTCs pid 0x18
            float retval = 0;
            ulong cmd = 0x0000000000420102; // only stored DTCs
            //SendMessage(data);  // software version
            CANMessage msg = new CANMessage(0x7DF, 0, 8);
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return 0f;
            }
            // wait for a 0x58 or a 0x7F message
            CANMessage response = new CANMessage();
            ulong data = 0;
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            data = response.getData();
            retval = Convert.ToInt32(getCanData(data, 3)) * 256;
            retval += Convert.ToInt32(getCanData(data, 4));
            retval /= 1000;
            return retval;
        }

        // How to read DTC codes
        //A7 A6    First DTC character
        //-- --    -------------------
        // 0  0    P - Powertrain
        // 0  1    C - Chassis
        // 1  0    B - Body
        // 1  1    U - Network

        //A5 A4    Second DTC character
        //-- --    --------------------
        // 0  0    0
        // 0  1    1
        // 1  0    2
        // 1  1    3

        //A3 A2 A1 A0    Third/Fourth/Fifth DTC characters
        //-- -- -- --    -------------------
        // 0  0  0  0    0
        // 0  0  0  1    1
        // 0  0  1  0    2
        // 0  0  1  1    3
        // 0  1  0  0    4
        // 0  1  0  1    5
        // 0  1  1  0    6
        // 0  1  1  1    7
        // 1  0  0  0    8
        // 1  0  0  1    9
        // 1  0  1  0    A
        // 1  0  1  1    B
        // 1  1  0  0    C
        // 1  1  0  1    D
        // 1  1  1  0    E
        // 1  1  1  1    F

        // Example
        // E1 03 ->
        // 1110 0001 0000 0011
        // 11=U
        //   10=2
        //      0001=1
        //           0000=0
        //                0011=3
        //----------------------
        // U2103
        private static string GetDtcDescription(CANMessage responseDTC)
        {
            int firstDtcNum = (0xC0 & Convert.ToInt32(responseDTC.getCanData(1))) >> 6;
            char firstDtcChar = '-';
            if (firstDtcNum == 0)
            {
                firstDtcChar = 'P';
            }
            else if (firstDtcNum == 1)
            {
                firstDtcChar = 'C';
            }
            else if (firstDtcNum == 2)
            {
                firstDtcChar = 'B';
            }
            else if (firstDtcNum == 3)
            {
                firstDtcChar = 'U';
            }
            int secondDtcNum = (0x30 & Convert.ToInt32(responseDTC.getCanData(1))) >> 4;

            int thirdDtcNum = (0x0F & Convert.ToInt32(responseDTC.getCanData(1)));

            int forthDtcNum = (0xF0 & Convert.ToInt32(responseDTC.getCanData(2))) >> 4;

            int fifthDtcNum = (0x0F & Convert.ToInt32(responseDTC.getCanData(2)));

            // It seems Trionic8 return 00
            //byte failureTypeByte = responseDTC.getCanData(3);

            byte statusByte = responseDTC.getCanData(4);
            //Removed this output
            //String statusDescription = string.Empty;
            //if (0x80 == (0x80 & statusByte)) statusDescription += "warningIndicatorRequestedState ";
            //if (0x40 == (0x40 & statusByte)) statusDescription += "currentDTCSincePowerUp ";
            //if (0x20 == (0x20 & statusByte)) statusDescription += "testNotPassedSinceCurrentPowerUp ";
            //if (0x10 == (0x10 & statusByte)) statusDescription += "historyDTC ";
            //if (0x08 == (0x08 & statusByte)) statusDescription += "testFailedSinceDTCCleared ";
            //if (0x04 == (0x04 & statusByte)) statusDescription += "testNotPassedSinceDTCCleared ";
            //if (0x02 == (0x02 & statusByte)) statusDescription += "currentDTC ";
            //if (0x01 == (0x01 & statusByte)) statusDescription += "DTCSupportedByCalibration ";

            return "DTC: " + firstDtcChar + secondDtcNum.ToString("d") + thirdDtcNum.ToString("X") + forthDtcNum.ToString("X") + fifthDtcNum.ToString("X") + " StatusByte: " + statusByte.ToString("X2");
        }

        private bool addDTC(CANMessage response)
        {
            // Read until response: EndOfDTCReport
            if (response.getCanData(1) == 0 && response.getCanData(2) == 0 && response.getCanData(3) == 0)
            {
                listDTC.Add("No more errors!");
                return false;
            }
            else
            {
                string dtcDescription = GetDtcDescription(response);
                logger.Debug(dtcDescription);
                listDTC.Add(dtcDescription);
                return true;
            }
        }

        List<string> listDTC;

        public string[] ReadDTC()
        {
            // send message to read DTC
            StartSession10();

            listDTC = new List<string>();

            // ReadDiagnosticInformation $A9 Service
            //  readStatusOfDTCByStatusMask $81 Request
            //      DTCStatusMask $12= 0001 0010
            //        0 Bit 7 warningIndicatorRequestedState
            //        0 Bit 6 currentDTCSincePowerUp
            //        0 Bit 5 testNotPassedSinceCurrentPowerUp
            //        1 Bit 4 historyDTC
            //        0 Bit 3 testFailedSinceDTCCleared
            //        0 Bit 2 testNotPassedSinceDTCCleared
            //        1 Bit 1 currentDTC
            //        0 Bit 0 DTCSupportedByCalibration
            ulong cmd = 0x000000001281A903; // 7E0 03 A9 81 12 00 00 00 00

            CANMessage msg = new CANMessage(0x7E0, 0, 4);
            msg.setData(cmd);
            msg.elmExpectedResponses = 15;
            m_canListener.setupWaitMessage(0x7E8, 0x5E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return listDTC.ToArray();
            }

            CANMessage response = new CANMessage();
            ulong data = 0;
            // Wait for response 
            // 7E8 03 7F A9 78 00 00 00 00
            // or the first DTC
            // 5E8 81 07 03 00 7F 00 00 00
            response = m_canListener.waitMessage(timeoutP2ct);
            data = response.getData();

            if (response.getID() == 0x5E8 && response.getCanData(0) == 0x81)
            {
                // Now wait for all DTCs
                m_canListener.setupWaitMessage(0x7E8, 0x5E8);

                bool more_errors = addDTC(response);

                while (more_errors)
                {
                    CANMessage responseDTC = new CANMessage();
                    responseDTC = m_canListener.waitMessage(timeoutP2ct);
                    more_errors = addDTC(responseDTC);
                }
            }
            // RequestCorrectlyReceived-ResponsePending ($78, RC_RCR-RP)
            else if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0xA9 && response.getCanData(3) == 0x78) 
            {
                logger.Debug("RequestCorrectlyReceived-ResponsePending", ActivityType.UploadingFlash);
                // Now wait for all DTCs
                m_canListener.setupWaitMessage(0x7E8, 0x5E8);

                bool more_errors = true;
                while (more_errors)
                {
                    CANMessage responseDTC = new CANMessage();
                    responseDTC = m_canListener.waitMessage(timeoutP2ct);
                    more_errors = addDTC(responseDTC);
                }
            }
            else if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0xA9)
            {
                string info = TranslateErrorCode(response.getCanData(3));
                CastInfoEvent("Error: " + info, ActivityType.ConvertingFile);
            }

            Send0120();

            return listDTC.ToArray();
        }

        public bool ClearDTCCodes()
        {
            bool retval = false;

            // ClearDiagnosticInformation ($04) Service
            ulong cmd = 0x0000000000000401; // 7DF 01 04 00 00 00 00 00 00

            CANMessage msg = new CANMessage(0x7DF, 0, 2);
            msg.setData(cmd);
            msg.elmExpectedResponses = 15;
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }

            CANMessage response = new CANMessage();
            ulong data = 0;
            // Wait for response 
            // 7E8 01 44 00 00 00 00 00 00
            response = m_canListener.waitMessage(timeoutP2ct);
            data = response.getData();

            // Positive Response
            if (response.getID() == 0x7E8 && response.getCanData(1) == 0x44)
            {
                retval = true;
            }
            // RequestCorrectlyReceived-ResponsePending ($78, RC_RCR-RP)
            else if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x04 && response.getCanData(3) == 0x78)
            {
                //CastInfoEvent("RequestCorrectlyReceived-ResponsePending", ActivityType.UploadingFlash);
                // Wait one more second
                m_canListener.setupWaitMessage(0x7E8);
                m_canListener.waitMessage(timeoutP2ct);
                if (response.getID() == 0x7E8 && response.getCanData(1) == 0x44)
                {
                    retval = true;
                }
            }
            // Other errors
            else if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x04)
            {
                string info = TranslateErrorCode(response.getCanData(3));
                CastInfoEvent("Error: " + info, ActivityType.ConvertingFile);
            }

            Send0120();
            return retval;
        }

        // MattiasC, this one is probably not working, need a car to test
        // look at readDTCCodes() how it was improved.
        public string[] readDTCCodesCIM()
        {
            // test code
            //ulong c = 0x0000006F00070181;//81 01 07 00 6F 00 00 00
            //ulong c = 0x000000FD00220181; //81 01 22 00 FD 00 00 00
            //CANMessage test = new CANMessage();
            //test.setData(c);
            //AddToCanTrace(GetDtcDescription(test));

            // send message to read DTC
            StartSession10();

            List<string> list = new List<string>();

            ulong cmd = 0x000000001281A903; // 245 03 A9 81 12 00 00 00 00

            CANMessage msg = new CANMessage(0x245, 0, 8);
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x545);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return list.ToArray();
            }

            CANMessage response = new CANMessage();
            ulong data = 0;
            // Wait for response 
            // 545 03 7F A9 78 00 00 00 00
            response = m_canListener.waitMessage(timeoutP2ct);
            data = response.getData();

            if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0xA9 && response.getCanData(3) == 0x78)
            {
                // Now wait for all DTCs
                m_canListener.setupWaitMessage(0x545);

                bool more_errors = true;
                while (more_errors)
                {
                    CANMessage responseDTC = new CANMessage();
                    responseDTC = m_canListener.waitMessage(timeoutP2ct);

                    // Read until response:   No more errors, status == 0xFF
                    int dtcStatus = Convert.ToInt32(responseDTC.getCanData(4));
                    if (dtcStatus == 0xFF)
                    {
                        more_errors = false;
                        list.Add("0xFF No more errors!");
                    }
                    else if (dtcStatus == 0x97)
                    {
                        more_errors = false;
                        list.Add("0x17 No more errors!");
                    }
                    else
                    {
                        string dtcDescription = GetDtcDescription(responseDTC);
                        list.Add(dtcDescription);
                    }
                }
            }

            Send0120();

            return list.ToArray();
        }

        public bool TestCIMAccess()
        {
            return RequestSecurityAccessCIM(0);
        }

        private void SendDeviceControlMessageWithCode(byte command, string secretcode /*ulong code*/)
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 8);
            ulong cmd = 0x000000000000AE07;
            ulong lcommand = command;
            cmd |= (lcommand * 0x10000);
            ulong code = 0;
            //0x4D4E415100000000
            code |= Convert.ToByte(secretcode[3]);
            code = code << 8;
            code |= Convert.ToByte(secretcode[2]);
            code = code << 8;
            code |= Convert.ToByte(secretcode[1]);
            code = code << 8;
            code |= Convert.ToByte(secretcode[0]);
            code = code << 4 * 8;

            cmd |= code;
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return;
            }
            CANMessage ECMresponse = new CANMessage();
            ECMresponse = m_canListener.waitMessage(timeoutP2ct);
        }

        private bool ReadDataByPacketIdentifier(byte command, uint responseID)
        {
            //SendCommandNoResponse(0x7E0, 0x000000006201AA03);
            CANMessage msg = new CANMessage(0x7E0, 0, 4);
            ulong cmd = 0x000000000001AA03;
            ulong lcommand = command;
            cmd |= (lcommand * 0x1000000);
            msg.setData(cmd);
            m_canListener.setupWaitMessage(responseID);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage ECMresponse = new CANMessage();
            ECMresponse = m_canListener.waitMessage(timeoutP2ct);
            return true;
        }

        private int GetProgrammingStateNormal()
        {
            logger.Debug("Get programming state");
            CANMessage msg = new CANMessage(0x7E0, 0, 2);
            ulong cmd = 0x000000000000A201; // 0x02 0x10 0x02
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return 0;
            }
            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            logger.Debug("Get programming state response: " + data.ToString("X16"));
            //\__ 00 00 03 11 02 e2 01 00 00 00 00 00 Magic reply, T8 replies with 0311 and programming state 01(recovery state?)
            if (getCanData(data, 1) != 0xE2 || getCanData(data, 0) != 0x02)
            {
                return 0;
            }
            return Convert.ToInt32(getCanData(data, 2));
        }

        public bool ProgramVINProcedure(string vinNumber, string secretcode)
        {
            bool retval = false;
            CastInfoEvent("Start program VIN process", ActivityType.ConvertingFile);
            _stallKeepAlive = true;
            //BroadcastKeepAlive();
            //Thread.Sleep(1000);
            //GetDiagnosticDataIdentifier();
            //Thread.Sleep(1000);
            string currentVIN = GetVehicleVIN();

            //TODO: Add logging of VIN in hexcodes here.

            if (currentVIN.Trim() == "") currentVIN = " is empty";
            CastInfoEvent("Current VIN " + currentVIN, ActivityType.ConvertingFile);
            BroadcastKeepAlive();
            Thread.Sleep(500);
            GetProgrammingStateNormal();
            //BroadcastRequest(0xA2);
            Thread.Sleep(500);
            CastInfoEvent("Request diag ID: " + GetDiagnosticDataIdentifier(), ActivityType.ConvertingFile);
            Thread.Sleep(500);
            BroadcastKeepAlive();
            Thread.Sleep(500);
            BroadcastKeepAlive();
            Thread.Sleep(500);
            BroadcastKeepAlive();
            Thread.Sleep(500);
            BroadcastKeepAlive();
            Thread.Sleep(500);
            _securityLevel = AccessLevel.AccessLevel01;

            if (RequestSecurityAccess(0))
            {
                BroadcastKeepAlive();
                Thread.Sleep(500);
                BroadcastKeepAlive();
                Thread.Sleep(500);
                BroadcastKeepAlive();
                Thread.Sleep(500);
                BroadcastKeepAlive();
                Thread.Sleep(500);

                Send0120(); // cancel diag session
                GetDiagnosticDataIdentifier();

                CastInfoEvent("Request 0xAA", ActivityType.ConvertingFile);
                ReadDataByPacketIdentifier(0x62, 0x5E8);
                //SendCommandNoResponse(0x7E0, 0x000000006201AA03);
                for (int tel = 0; tel < 10; tel++)
                {
                    CastInfoEvent("Waiting... " + tel.ToString() + "/10", ActivityType.ConvertingFile);
                    BroadcastKeepAlive();
                    Thread.Sleep(3000);
                }
                //CastInfoEvent("Request 0xA0", ActivityType.ConvertingFile);
                //RequestECUInfo(0xA0);
                CastInfoEvent("Request 0xAA", ActivityType.ConvertingFile);
                //SendCommandNoResponse(0x7E0, 0x000000000201AA03);
                ReadDataByPacketIdentifier(0x02, 0x5E8);
                BroadcastKeepAlive();
                Thread.Sleep(500);
                RequestECUInfo(0x90); // read VIN again
                Thread.Sleep(500);
                SendDeviceControlMessageWithCode(0x60, /*0x4D4E415100000000*/ secretcode);

                CastInfoEvent("Waiting...", ActivityType.ConvertingFile);
                Thread.Sleep(1000);
                BroadcastKeepAlive();
                CastInfoEvent("Waiting...", ActivityType.ConvertingFile);
                Thread.Sleep(1000);
                BroadcastKeepAlive();
                CastInfoEvent("Waiting...", ActivityType.ConvertingFile);
                Thread.Sleep(1000);
                BroadcastKeepAlive();
                CastInfoEvent("Waiting...", ActivityType.ConvertingFile);
                Thread.Sleep(1000);

                SendDeviceControlMessageWithCode(0x6e, /*0x4D4E415100000000*/ secretcode);

                //CastInfoEvent("Clearing VIN...", ActivityType.ConvertingFile);
                //ProgramVIN("                 ");
                CastInfoEvent("Programming VIN...", ActivityType.ConvertingFile);
                retval = ProgramVIN(vinNumber);
            }
            else
            {
                retval = false;
            }
            _stallKeepAlive = false;
            return retval;
        }

        /// <summary>
        /// Marries the ECM to a car
        /// </summary>
        /// <returns></returns>
        public bool MarryECM()
        {
            CastInfoEvent("Start marry process", ActivityType.ConvertingFile);
            BroadcastKeepAlive();
            Thread.Sleep(1000);
            BroadcastRequestDiagnoseID();
            Thread.Sleep(1000);
            string currentVIN = GetVehicleVIN();

            //TODO: Add logging of VIN in hexcodes here.

            if (currentVIN.Trim() == "") currentVIN = " is empty";
            CastInfoEvent("Current VIN " + currentVIN, ActivityType.ConvertingFile);
            BroadcastKeepAlive();
            BroadcastRequest(0xA2);
            Thread.Sleep(500);
            BroadcastRequestDiagnoseID();
            BroadcastKeepAlive();
            Thread.Sleep(1000);
            SendCommandNoResponse(0x7E0, 0x000000006201AA03);
            BroadcastKeepAlive();
            Thread.Sleep(1000);
            RequestECUInfo(0xA0);
            SendCommandNoResponse(0x7E0, 0x000000000201AA03);
            BroadcastKeepAlive();
            Thread.Sleep(1000);
            CastInfoEvent("Getting security access to CIM", ActivityType.ConvertingFile);

            if (RequestSecurityAccessCIM(0))
            {
                CastInfoEvent("Security access to CIM OK", ActivityType.ConvertingFile);
                BroadcastKeepAlive();
                string VINFromCIM = RequestCIMInfo(0x90);
                BroadcastKeepAlive();
                CastInfoEvent("Current VIN in CIM: " + VINFromCIM, ActivityType.ConvertingFile);
                if (ProgramVIN(VINFromCIM))
                {
                    CastInfoEvent("Programmed VIN into ECU", ActivityType.ConvertingFile);
                    BroadcastKeepAlive();
                    VINFromCIM = RequestCIMInfo(0x90);
                    if (SendSecretCodetoCIM())
                    {
                        CastInfoEvent("Sending marry command", ActivityType.ConvertingFile);
                        BroadcastKeepAlive();
                        VINFromCIM = RequestCIMInfo(0x90);
                        if (MarryCIMAndECU())
                        {
                            CastInfoEvent("Married ECU to car, finalizing procedure...", ActivityType.ConvertingFile);
                            BroadcastKeepAlive();
                            Thread.Sleep(1000);
                            CastInfoEvent("Waiting... (1/10)", ActivityType.ConvertingFile);
                            BroadcastKeepAlive();
                            Thread.Sleep(1000);
                            CastInfoEvent("Waiting... (2/10)", ActivityType.ConvertingFile);
                            BroadcastKeepAlive();
                            Thread.Sleep(1000);
                            CastInfoEvent("Waiting... (3/10)", ActivityType.ConvertingFile);
                            BroadcastKeepAlive();
                            Thread.Sleep(1000);
                            CastInfoEvent("Waiting... (4/10)", ActivityType.ConvertingFile);
                            BroadcastKeepAlive();
                            Thread.Sleep(1000);
                            CastInfoEvent("Waiting... (5/10)", ActivityType.ConvertingFile);
                            BroadcastKeepAlive();
                            Thread.Sleep(1000);
                            CastInfoEvent("Waiting... (6/10)", ActivityType.ConvertingFile);
                            BroadcastKeepAlive();
                            Thread.Sleep(1000);
                            CastInfoEvent("Waiting... (7/10)", ActivityType.ConvertingFile);
                            BroadcastKeepAlive();
                            Thread.Sleep(1000);
                            CastInfoEvent("Waiting... (8/10)", ActivityType.ConvertingFile);
                            BroadcastKeepAlive();
                            Thread.Sleep(1000);
                            CastInfoEvent("Waiting... (9/10)", ActivityType.ConvertingFile);
                            BroadcastKeepAlive();
                            Thread.Sleep(1000);
                            CastInfoEvent("Waiting... (10/10)", ActivityType.ConvertingFile);
                            BroadcastKeepAlive();
                            Thread.Sleep(1000);
                            CastInfoEvent("Getting security access to ECU", ActivityType.ConvertingFile);
                            _securityLevel = AccessLevel.AccessLevel01;
                            if (RequestSecurityAccess(0))
                            {
                                CastInfoEvent("Security access to ECU OK", ActivityType.ConvertingFile);
                                BroadcastRequestDiagnoseID();
                                BroadcastKeepAlive();
                                string vehicleVIN = GetVehicleVIN();
                                CastInfoEvent("Current VIN: " + vehicleVIN, ActivityType.ConvertingFile);
                                BroadcastKeepAlive();
                                CastInfoEvent("Sending F1 F2 F3...", ActivityType.ConvertingFile);
                                Thread.Sleep(1000);
                                DynamicallyDefineLocalIdentifier(0xF1, 0x40);
                                DynamicallyDefineLocalIdentifier(0xF2, 0x41);
                                DynamicallyDefineLocalIdentifier(0xF3, 0x42);
                                BroadcastKeepAlive();
                                CastInfoEvent("Sending 0xAA commands with F1 F2 F3...", ActivityType.ConvertingFile);
                                SendCommandNoResponse(0x7E0, 0x00000000F101AA03);
                                SendCommandNoResponse(0x7E0, 0x00000000F201AA03);
                                SendCommandNoResponse(0x7E0, 0x00000000F301AA03);

                                // set original oil quality indicator
                                if (_oilQualityRead <= 0 || _oilQualityRead > 100) _oilQualityRead = 50; // set to 50% by default
                                SetOilQuality(_oilQualityRead);

                                Thread.Sleep(200);
                                CastInfoEvent("Ending session (1/5)", ActivityType.ConvertingFile);
                                SendDeviceControlMessage(0x16);
                                Thread.Sleep(1000);
                                BroadcastKeepAlive();
                                CastInfoEvent("Ending session (2/5)", ActivityType.ConvertingFile);
                                Broadcast0401();
                                Thread.Sleep(1000);
                                CastInfoEvent("Ending session (3/5)", ActivityType.ConvertingFile);
                                BroadcastKeepAlive();
                                Thread.Sleep(1000);
                                CastInfoEvent("Ending session (4/5)", ActivityType.ConvertingFile);
                                BroadcastKeepAlive();
                                Broadcast0401();
                                BroadcastKeepAlive();
                                Thread.Sleep(1000);
                                CastInfoEvent("Ending session (5/5)", ActivityType.ConvertingFile);
                                return true;
                            }


                        }
                        else
                        {
                            CastInfoEvent("Failed to marry ECU to car", ActivityType.ConvertingFile);
                        }

                    }
                }
                else
                {
                    CastInfoEvent("Failed to program VIN into ECU: " + VINFromCIM, ActivityType.ConvertingFile);
                }

            }



            return false;
        }

        private bool Broadcast0401()
        {
            CANMessage msg = new CANMessage(0x11, 0, 8);
            ulong cmd = 0x0000000000000401;
            msg.setData(cmd);
            // ECU should respond with 0000000000004401
            // CIM responds with 0000000078047F03 and 0000000000004401

            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage ECMresponse = new CANMessage();
            ECMresponse = m_canListener.waitMessage(timeoutP2ct);
            ulong rxdata = ECMresponse.getData();
            m_canListener.setupWaitMessage(0x645);
            int waitMsgCount = 0;
            while (waitMsgCount < 10)
            {

                CANMessage CIMresponse = new CANMessage();
                CIMresponse = m_canListener.waitMessage(timeoutP2ct);
                rxdata = CIMresponse.getData();
                if (getCanData(rxdata, 1) == 0x44)
                {
                    return true;
                }

                else if (getCanData(rxdata, 1) == 0x7F && getCanData(rxdata, 2) == 0x04 && getCanData(rxdata, 3) == 0x78)
                {
                    CastInfoEvent("Waiting for process to finish in CIM", ActivityType.ConvertingFile);
                }
                waitMsgCount++;
            }
            return false;
        }

        private bool MarryCIMAndECU()
        {
            //0000000000633B02
            CANMessage msg = new CANMessage(0x245, 0, 8);
            ulong cmd = 0x0000000000633B02;
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x645);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            int waitMsgCount = 0;
            while (waitMsgCount < 10)
            {

                CANMessage ECMresponse = new CANMessage();
                ECMresponse = m_canListener.waitMessage(timeoutP2ct);
                ulong rxdata = ECMresponse.getData();
                // response might be 00000000783B7F03 for some time
                // final result should be 0000000000637B02
                if (getCanData(rxdata, 1) == 0x7B && getCanData(rxdata, 2) == 0x63)
                {
                    return true;
                }

                else if (getCanData(rxdata, 1) == 0x7F && getCanData(rxdata, 2) == 0x3B && getCanData(rxdata, 3) == 0x78)
                {
                    CastInfoEvent("Waiting for marry process to complete between CIM and car", ActivityType.ConvertingFile);
                }
                waitMsgCount++;
            }
            return false;
        }

        /// <summary>
        /// Divorces the ECM from the Car
        /// </summary>
        /// <returns></returns>
        public bool DivorceECM()
        {
            CastInfoEvent("Start divorce process", ActivityType.ConvertingFile);
            BroadcastKeepAlive();
            Thread.Sleep(1000);
            BroadcastRequestDiagnoseID();
            Thread.Sleep(1000);
            string currentVIN = GetVehicleVIN();
            CastInfoEvent("Current VIN " + currentVIN, ActivityType.ConvertingFile);
            Thread.Sleep(1000);
            BroadcastKeepAlive();
            // now, request security access to the CIM
            Send0120(); // start a session
            SendCommandNoResponse(0x7E0, 0x000000006201AA03);
            Thread.Sleep(1000);
            BroadcastKeepAlive();
            SendCommandNoResponse(0x7E0, 0x000000000201AA03);
            if (SendSecretCode1())
            {
                Thread.Sleep(1000);
                BroadcastKeepAlive();
                if (SendSecretCode2())
                {
                    Thread.Sleep(1000);
                    BroadcastKeepAlive();
                    // now write spaces into the VIN in trionic
                    CastInfoEvent("Clearing VIN", ActivityType.ConvertingFile);
                    if (ProgramVIN("                 "))
                    {
                        CastInfoEvent("VIN cleared", ActivityType.ConvertingFile);
                        DynamicallyDefineLocalIdentifier(0xF1, 0x40);
                        DynamicallyDefineLocalIdentifier(0xF2, 0x41);
                        DynamicallyDefineLocalIdentifier(0xF3, 0x42);
                        BroadcastKeepAlive();
                        SendCommandNoResponse(0x7E0, 0x00000000F101AA03);
                        SendCommandNoResponse(0x7E0, 0x00000000F201AA03);
                        SendCommandNoResponse(0x7E0, 0x00000000F301AA03);
                        Thread.Sleep(200);
                        RequestECUInfo(0x29);
                        _oilQualityRead = GetOilQuality();
                        CastInfoEvent("Oil quality indicator: " + _oilQualityRead.ToString("F2") + " %", ActivityType.ConvertingFile);
                        RequestECUInfo(0x2A);
                        Thread.Sleep(1000);
                        SendDeviceControlMessage(0x16);
                        BroadcastKeepAlive();
                        return true;
                    }
                }
            }
            return false;
        }

        private void SendDeviceControlMessage(byte command)
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 3);
            ulong cmd = 0x000000000000AE02;
            ulong lcommand = command;
            cmd |= (lcommand * 0x10000);
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return;
            }
            CANMessage ECMresponse = new CANMessage();
            ECMresponse = m_canListener.waitMessage(timeoutP2ct);
        }

        private void DynamicallyDefineLocalIdentifier(byte id, byte type)
        {
            //0000004006F12C04
            //0000004106F22C04
            //0000004206F32C04
            CANMessage msg = new CANMessage(0x7E0, 0, 5);
            ulong cmd = 0x0000000006002C04;
            ulong lid = id;
            ulong ltype = type;
            cmd |= (ltype * 0x100000000);
            cmd |= (lid * 0x10000);
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return;
            }
            CANMessage ECMresponse = new CANMessage();
            ECMresponse = m_canListener.waitMessage(timeoutP2ct);
            //ulong rxdata = ECMresponse.getData();
        }
        private ulong AddByteToCommand(ulong cmd, byte b2add, int position)
        {
            ulong retval = cmd;
            ulong lbyte = b2add;
            switch (position)
            {
                case 0:
                    retval |= lbyte;
                    break;
                case 1:
                    retval |= lbyte * 0x100;
                    break;
                case 2:
                    retval |= lbyte * 0x10000;
                    break;
                case 3:
                    retval |= lbyte * 0x1000000;
                    break;
                case 4:
                    retval |= lbyte * 0x100000000;
                    break;
                case 5:
                    retval |= lbyte * 0x10000000000;
                    break;
                case 6:
                    retval |= lbyte * 0x1000000000000;
                    break;
                case 7:
                    retval |= lbyte * 0x100000000000000;
                    break;
            }
            return retval;
        }

        public bool ProgramVIN(string VINNumber)
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 8);
            ulong cmd = 0x00000000903B1310;
            if (VINNumber.Length > 17) VINNumber = VINNumber.Substring(0, 17);// lose more than 17 digits
            if (VINNumber.Length < 17) VINNumber = VINNumber.PadRight(17, '0');
            if (VINNumber.Length != 17) return false;

            cmd = AddByteToCommand(cmd, Convert.ToByte(VINNumber[0]), 4);
            cmd = AddByteToCommand(cmd, Convert.ToByte(VINNumber[1]), 5);
            cmd = AddByteToCommand(cmd, Convert.ToByte(VINNumber[2]), 6);
            cmd = AddByteToCommand(cmd, Convert.ToByte(VINNumber[3]), 7);

            msg.setData(cmd);

            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }

            ulong rxdata = m_canListener.waitMessage(timeoutP2ct).getData();
            if (getCanData(rxdata, 0) == 0x30)
            {
                //2020202020202021
                //0020202020202022
                cmd = 0x0000000000000021;
                cmd = AddByteToCommand(cmd, Convert.ToByte(VINNumber[4]), 1);
                cmd = AddByteToCommand(cmd, Convert.ToByte(VINNumber[5]), 2);
                cmd = AddByteToCommand(cmd, Convert.ToByte(VINNumber[6]), 3);
                cmd = AddByteToCommand(cmd, Convert.ToByte(VINNumber[7]), 4);
                cmd = AddByteToCommand(cmd, Convert.ToByte(VINNumber[8]), 5);
                cmd = AddByteToCommand(cmd, Convert.ToByte(VINNumber[9]), 6);
                cmd = AddByteToCommand(cmd, Convert.ToByte(VINNumber[10]), 7);

                msg.setData(cmd);
                m_canListener.setupWaitMessage(0x7E8);
                canUsbDevice.sendMessage(msg);

                cmd = 0x0000000000000022;
                cmd = AddByteToCommand(cmd, Convert.ToByte(VINNumber[11]), 1);
                cmd = AddByteToCommand(cmd, Convert.ToByte(VINNumber[12]), 2);
                cmd = AddByteToCommand(cmd, Convert.ToByte(VINNumber[13]), 3);
                cmd = AddByteToCommand(cmd, Convert.ToByte(VINNumber[14]), 4);
                cmd = AddByteToCommand(cmd, Convert.ToByte(VINNumber[15]), 5);
                cmd = AddByteToCommand(cmd, Convert.ToByte(VINNumber[16]), 6);
                //msg.setLength(7); // only 7 bytes for the last message
                msg.setData(cmd);
                m_canListener.setupWaitMessage(0x7E8);
                canUsbDevice.sendMessage(msg);

                rxdata = m_canListener.waitMessage(timeoutP2ct).getData();
                // RequestCorrectlyReceived-ResponsePending ($78, RC_RCR-RP)
                if (getCanData(rxdata, 1) == 0x7F && getCanData(rxdata, 2) == 0x3B && getCanData(rxdata, 3) == 0x78) 
                {
                    // wait for ack
                    //0000000000907B02
                    rxdata = m_canListener.waitMessage(timeoutP2ct).getData();
                    if (getCanData(rxdata, 1) == 0x7B && getCanData(rxdata, 2) == 0x90)
                    {
                        return true;
                    }
                }
                // wait for ack
                //0000000000907B02
                else if (getCanData(rxdata, 1) == 0x7B && getCanData(rxdata, 2) == 0x90)
                {
                    return true;
                }

            }
            return false;
        }

        private bool SendSecretCode2()
        {
            //44585349006EAE07
            CANMessage msg = new CANMessage(0x7E0, 0, 8);
            ulong cmd = 0x44585349006EAE07;
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage ECMresponse = new CANMessage();
            ECMresponse = m_canListener.waitMessage(timeoutP2ct);
            ulong rxdata = ECMresponse.getData();
            if (getCanData(rxdata, 1) == 0xEE && getCanData(rxdata, 2) == 0x6E)
            {
                return true;
            }
            return false;

        }

        private bool SendSecretCodetoCIM()
        {
            //0044585349603B06
            CANMessage msg = new CANMessage(0x245, 0, 8);
            ulong cmd = 0x0044585349603B06;
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x645);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage ECMresponse = new CANMessage();
            ECMresponse = m_canListener.waitMessage(timeoutP2ct);
            ulong rxdata = ECMresponse.getData();
            if (getCanData(rxdata, 1) == 0x7B && getCanData(rxdata, 2) == 0x60)
            {
                return true;
            }
            return false;

        }

        private bool SendSecretCode1()
        {
            //445853490060AE07
            CANMessage msg = new CANMessage(0x7E0, 0, 8);
            ulong cmd = 0x445853490060AE07;
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            CANMessage ECMresponse = new CANMessage();
            ECMresponse = m_canListener.waitMessage(timeoutP2ct);
            ulong rxdata = ECMresponse.getData();
            if (getCanData(rxdata, 1) == 0xEE && getCanData(rxdata, 2) == 0x60)
            {
                return true;
            }
            return false;

        }

        private void SendCommandNoResponse(uint destID, ulong data)
        {
            CANMessage msg = new CANMessage(destID, 0, 8);
            ulong cmd = data;
            msg.setData(cmd);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return;
            }
        }

        private void BroadcastRequest(byte id)
        {
            ulong lid = id;
            CANMessage msg = new CANMessage(0x11, 0, 8);
            ulong cmd = 0x0000000000000001;
            cmd |= (lid * 0x100);
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return;
            }
            CANMessage ECMresponse = new CANMessage();
            ECMresponse = m_canListener.waitMessage(timeoutP2ct);
            m_canListener.setupWaitMessage(0x645);
            CANMessage CIMresponse = new CANMessage();
            CIMresponse = m_canListener.waitMessage(timeoutP2ct);
        }

        private void BroadcastRequestDiagnoseID()
        {
            //0101 000000009A1A02FE	
            CANMessage msg = new CANMessage(0x11, 0, 8);
            ulong cmd = 0x00000000009A1A02;
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
            }
            CANMessage ECMresponse = new CANMessage();
            ECMresponse = m_canListener.waitMessage(timeoutP2ct);
            m_canListener.setupWaitMessage(0x645);
            CANMessage CIMresponse = new CANMessage();
            CIMresponse = m_canListener.waitMessage(timeoutP2ct);
            // wait for response of CIM and ECU
        }

        private void BroadcastKeepAlive()
        {
            //0101 00000000003E01FE
            CANMessage msg = new CANMessage(0x11, 0, 2);
            ulong cmd = 0x0000000000003E01;
            msg.setData(cmd);
            msg.elmExpectedResponses = 1;
            m_canListener.setupWaitMessage(0x311);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return;
            }
            CANMessage ECMresponse = new CANMessage();
            ECMresponse = m_canListener.waitMessage(timeoutP2ct);
            //07E8 00000084019A5A04
            //0645 00000008039A5A04
        }

        private void BroadcastKeepAlive101()
        {
            //0101 00000000003E01FE
            CANMessage msg = new CANMessage(0x101, 0, 2);
            ulong cmd = 0x0000000000003E01;
            msg.setData(cmd);
            msg.elmExpectedResponses = 1;

            // m_canListener.setupWaitMessage(0x311);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return;
            }
            // CANMessage ECMresponse = new CANMessage();
            // ECMresponse = m_canListener.waitMessage(timeoutP2ct);
            //07E8 00000084019A5A04
            //0645 00000008039A5A04
        }

        private bool UploadBootloaderRead()
        {
            int startAddress = 0x102400;
            Bootloader btloaderdata = new Bootloader();

            int txpnt = 0;
            byte iFrameNumber = 0x21;
            int saved_progress = 0;
            if (requestDownload(false))
            {
                for (int i = 0; i < 0x46; i++)
                {
                    iFrameNumber = 0x21;
                    //10 F0 36 00 00 10 24 00
                    //logger.Debug("Sending bootloader: " + startAddress.ToString("X8"));
                    // cast event
                    int percentage = (int)(((float)i * 100) / 70F);
                    if (percentage > saved_progress)
                    {
                        CastProgressWriteEvent(percentage);
                        saved_progress = percentage;
                    }

                    if (SendTransferData(0xF0, startAddress, 0x7E8))
                    {
                        canUsbDevice.RequestDeviceReady();
                        // send 0x22 (34) frames with data from bootloader
                        CANMessage msg = new CANMessage(0x7E0, 0, 8);
                        for (int j = 0; j < 0x22; j++)
                        {
                            var cmd = BitTools.GetFrameBytes(iFrameNumber, btloaderdata.BootloaderBytes, txpnt);
                            msg.setData(cmd);
                            txpnt += 7;
                            iFrameNumber++;

                            if (iFrameNumber > 0x2F) iFrameNumber = 0x20;
                            msg.elmExpectedResponses = j == 0x21 ? 1 : 0;//on last command (iFrameNumber 22 expect 1 message)
                            if (j == 0x21)
                                m_canListener.ClearQueue();

                            if (!canUsbDevice.sendMessage(msg))
                            {
                                logger.Debug("Couldn't send message");
                            }
                            Application.DoEvents();
                            if (m_sleepTime > 0)
                                Thread.Sleep(m_sleepTime);

                        }
                        var data = m_canListener.waitMessage(timeoutP2ct, 0x7E8).getData();
                        if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x76)
                        {
                            return false;
                        }
                        canUsbDevice.RequestDeviceReady();
                        SendKeepAlive();
                        startAddress += 0xEA;

                    }
                    else
                    {
                        logger.Debug("Did not receive correct response from SendTransferData");
                    }
                }

                iFrameNumber = 0x21;
                if (SendTransferData(0x0A, startAddress, 0x7E8))
                {
                    // send 0x22 (34) frames with data from bootloader
                    CANMessage msg = new CANMessage(0x7E0, 0, 8);
                    var cmd = BitTools.GetFrameBytes(iFrameNumber, btloaderdata.BootloaderBytes, txpnt);
                    msg.setData(cmd);
                    txpnt += 7;
                    iFrameNumber++;
                    if (!canUsbDevice.sendMessage(msg))
                    {
                        logger.Debug("Couldn't send message");
                    }
                    if (m_sleepTime > 0)
                        Thread.Sleep(m_sleepTime);

                    // now wait for 01 76 00 00 00 00 00 00 
                    CANMessage response = m_canListener.waitMessage(timeoutP2ct, 0x7E8);
                    ulong data = response.getData();
                    if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x76)
                    {
                        return false;
                    }
                    SendKeepAlive();
                    startAddress += 0x06;
                }
                else
                {
                    logger.Debug("Did not receive correct response from SendTransferData");
                }

                CastProgressWriteEvent(100);
            }
            return true;
        }
       
        private bool UploadBootloaderWrite()
        {
            int startAddress = 0x102400;
            Bootloader btloaderdata = new Bootloader();

            int txpnt = 0;
            byte iFrameNumber = 0x21;
            int saved_progress = 0;
            if (requestDownload(false))
            {
                for (int i = 0; i < 0x46; i++)
                {
                    iFrameNumber = 0x21;
                    //10 F0 36 00 00 10 24 00
                    //logger.Debug("Sending bootloader: " + startAddress.ToString("X8"));
                    // cast event
                    int percentage = (int)(((float)i * 100) / 70F);
                    if (percentage > saved_progress)
                    {
                        CastProgressWriteEvent(percentage);
                        saved_progress = percentage;
                    }

                    if (SendTransferData(0xF0, startAddress, 0x7E8))
                    {
                        canUsbDevice.RequestDeviceReady();
                        // send 0x22 (34) frames with data from bootloader
                        CANMessage msg = new CANMessage(0x7E0, 0, 8);
                        for (int j = 0; j < 0x22; j++)
                        {
                            var cmd = BitTools.GetFrameBytes(iFrameNumber, btloaderdata.BootloaderProgBytes, txpnt);
                            msg.setData(cmd);
                            txpnt += 7;
                            iFrameNumber++;

                            if (iFrameNumber > 0x2F) iFrameNumber = 0x20;
                            msg.elmExpectedResponses = j == 0x21 ? 1 : 0;//on last command (iFrameNumber 22 expect 1 message)
                            if (j == 0x21)
                                m_canListener.ClearQueue();

                            if (!canUsbDevice.sendMessage(msg))
                            {
                                logger.Debug("Couldn't send message");
                            }
                            Thread.Sleep(m_sleepTime);
                        }
                        // now wait for 01 76 00 00 00 00 00 00 
                        ulong data = m_canListener.waitMessage(timeoutP2ct, 0x7E8).getData();
                        if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x76)
                        {
                            return false;
                        }
                        canUsbDevice.RequestDeviceReady();
                        SendKeepAlive();
                        startAddress += 0xEA;

                    }
                    else
                    {
                        logger.Debug("Did not receive correct response from SendTransferData");
                    }
                }

                iFrameNumber = 0x21;
                if (SendTransferData(0x0A, startAddress, 0x7E8))
                {
                    // send 0x22 (34) frames with data from bootloader
                    CANMessage msg = new CANMessage(0x7E0, 0, 8);
                    var cmd = BitTools.GetFrameBytes(iFrameNumber, btloaderdata.BootloaderBytes, txpnt);
                    msg.setData(cmd);
                    txpnt += 7;
                    iFrameNumber++;
                    if (iFrameNumber > 0x2F) iFrameNumber = 0x20;
                    if (!canUsbDevice.sendMessage(msg))
                    {
                        logger.Debug("Couldn't send message");
                    }
                    if (m_sleepTime > 0)
                        Thread.Sleep(m_sleepTime);

                    ulong data = m_canListener.waitMessage(timeoutP2ct, 0x7E8).getData();
                    if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x76)
                    {
                        return false;
                    }
                    SendKeepAlive();
                    startAddress += 0x06;
                }
                else
                {
                    logger.Debug("Did not receive correct response from SendTransferData");
                }

                CastProgressWriteEvent(100);
            }
            else
            {
                logger.Debug("requestDownload() failed");
                return false;
            }
            return true;
        }

        private bool UploadBootloaderRecover(bool LegionMode)
        {
            int startAddress = 0x102400;
            Bootloader btloaderdata = new Bootloader();
            Bootloader_Leg btloaderdata_Leg = new Bootloader_Leg();
            int txpnt = 0;
            byte iFrameNumber = 0x21;
            int saved_progress = 0;
            if (requestDownload011())
            {
                for (int i = 0; i < 0x46; i++)
                {
                    iFrameNumber = 0x21;
                    //10 F0 36 00 00 10 24 00
                    //logger.Debug("Sending bootloader: " + startAddress.ToString("X8"));
                    // cast event
                    int percentage = (int)(((float)i * 100) / 70F);
                    if (percentage > saved_progress)
                    {
                        CastProgressWriteEvent(percentage);
                        saved_progress = percentage;
                    }

                    if (SendTransferData011(0xF0, startAddress, 0x311))
                    {
                        //canUsbDevice.RequestDeviceReady();
                        // send 0x22 (34) frames with data from bootloader
                        CANMessage msg = new CANMessage(0x11, 0, 8);
                        for (int j = 0; j < 0x22; j++)
                        {
                            if (LegionMode)
                                msg.setData(BitTools.GetFrameBytes(iFrameNumber, btloaderdata_Leg.BootloaderLegionBytes, txpnt));
                            else
                                msg.setData(BitTools.GetFrameBytes(iFrameNumber, btloaderdata.BootloaderProgBytes, txpnt));

                            txpnt += 7;
                            iFrameNumber++;
                            if (iFrameNumber > 0x2F) iFrameNumber = 0x20;
                            msg.elmExpectedResponses = j == 0x21 ? 1 : 0;//on last command (iFrameNumber 22 expect 1 message)
                            if (!canUsbDevice.sendMessage(msg))
                            {
                                logger.Debug("Couldn't send message");
                            }
                            Application.DoEvents();
                            if (m_sleepTime > 0)
                                Thread.Sleep(m_sleepTime);
                        }
                        // now wait for 01 76 00 00 00 00 00 00 
                        ulong data = m_canListener.waitMessage(timeoutP2ct, 0x311).getData();
                        if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x76)
                        {
                            return false;
                        }
                        //canUsbDevice.RequestDeviceReady();
                        BroadcastKeepAlive();
                        startAddress += 0xEA;

                    }
                    else
                    {
                        logger.Debug("Did not receive correct response from SendTransferData");
                    }
                }

                iFrameNumber = 0x21;
                if (SendTransferData011(0x0A, startAddress, 0x311))
                {
                    // send 0x22 (34) frames with data from bootloader
                    CANMessage msg = new CANMessage(0x11, 0, 8);

                    var cmd = BitTools.GetFrameBytes(iFrameNumber, btloaderdata.BootloaderProgBytes, txpnt);
                    if (LegionMode)
                        cmd = BitTools.GetFrameBytes(iFrameNumber, btloaderdata_Leg.BootloaderLegionBytes, txpnt);
                    msg.setData(cmd);
                    txpnt += 7;
                    iFrameNumber++;
                    if (iFrameNumber > 0x2F) iFrameNumber = 0x20;
                    if (!canUsbDevice.sendMessage(msg))
                    {
                        logger.Debug("Couldn't send message");
                    }
                    if (m_sleepTime > 0)
                        Thread.Sleep(m_sleepTime);

                    // now wait for 01 76 00 00 00 00 00 00 
                    ulong data = m_canListener.waitMessage(timeoutP2ct, 0x311).getData();
                    if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x76)
                    {
                        return false;
                    }
                    BroadcastKeepAlive();
                    startAddress += 0x06;
                }
                else
                {
                    logger.Debug("Did not receive correct response from SendTransferData");
                }

                CastProgressWriteEvent(100);
            }
            return true;
        }


        public void RecoverECU_Def(object sender, DoWorkEventArgs workEvent)
        {
            RecoverECU(false, sender, workEvent);
        }


        public void RecoverECU_Leg(object sender, DoWorkEventArgs workEvent)
        {
            RecoverECU(true, sender, workEvent);
        }

        private void RecoverECU(bool LegionMode, object sender, DoWorkEventArgs workEvent)
        {
            BackgroundWorker bw = sender as BackgroundWorker;
            string filename = (string)workEvent.Argument;
            string diagDataID = GetDiagnosticDataIdentifier0101();
            logger.Debug("DataID: " + diagDataID);
            if (diagDataID == string.Empty)
            {
                BlockManager bm = new BlockManager();
                bm.SetFilename(filename);

                sw.Reset();
                sw.Start();

                _stallKeepAlive = true;

                CastInfoEvent("Recovery needed...", ActivityType.UploadingBootloader);
                BroadcastKeepAlive();
                Thread.Sleep(200);  // was 1
                BroadcastKeepAlive();
                Thread.Sleep(500);
                CastInfoEvent("Starting session", ActivityType.UploadingBootloader);
                BroadcastSession10();
                Thread.Sleep(200);  // was 1
                CastInfoEvent("Telling ECU to clear CANbus", ActivityType.UploadingBootloader);
                BroadcastShutup();
                Thread.Sleep(200);  // was 1
                int progState = GetProgrammingState(0x311);
                if (progState == 0x01)
                {
                    CastInfoEvent("Recovery needed phase 1", ActivityType.UploadingBootloader);
                    BroadcastShutup011();
                    if (GetProgrammingState011() == 0x01)
                    {
                        CastInfoEvent("Recovery needed phase 2", ActivityType.UploadingBootloader);
                        SendA5011();
                        Thread.Sleep(100);
                        SendA503011();
                        Thread.Sleep(100);
                        BroadcastKeepAlive();
                        Thread.Sleep(100);
                        CastInfoEvent("Requesting security access...", ActivityType.UploadingBootloader);
                        if (RequestSecurityAccess011(0))
                        {
                            CastInfoEvent("Security access granted, uploading bootloader", ActivityType.UploadingBootloader);
                            UploadBootloaderRecover(LegionMode);
                            CastInfoEvent("Starting bootloader", ActivityType.UploadingBootloader);
                            Thread.Sleep(500);

                            StartBootloader011(LegionMode);
                            Thread.Sleep(500);
                            if (LegionMode)
                            {
                                WriteFlashLegion(EcuByte_T8, 0x100000, false, sender, workEvent);
                                return;
                            }
                            else
                            {
                                CastInfoEvent("Erasing FLASH", ActivityType.StartErasingFlash);
                            }

                            
                            if (SendrequestDownload(6, true, LegionMode))
                            {
                                _needRecovery = true;
                                CastInfoEvent("Programming FLASH", ActivityType.UploadingFlash);
                                bool success = WriteFlashRecover(bm);

                                sw.Stop();
                                _needRecovery = false;
                                // what else to do?
                                Send0120();
                                if (success)
                                {
                                    CastInfoEvent("Recovery completed", ActivityType.ConvertingFile);
                                    CastInfoEvent("Session ended", ActivityType.FinishedFlashing);
                                    workEvent.Result = true;
                                    return;
                                }
                                else
                                {
                                    CastInfoEvent("Recovery failed", ActivityType.ConvertingFile);
                                    workEvent.Result = false;
                                    return;
                                }
                            }
                            else
                            {
                                sw.Stop();
                                _needRecovery = false;
                                _stallKeepAlive = false;
                                CastInfoEvent("Failed to erase FLASH", ActivityType.ConvertingFile);
                                Send0120();
                                CastInfoEvent("Session ended", ActivityType.FinishedFlashing);
                                workEvent.Result = false;
                                return;

                            }
                        }
                    }
                    else
                    {
                        CastInfoEvent("Recovery not needed...", ActivityType.UploadingBootloader);
                    }
                }
                else if (progState == 0x00)
                {
                    CastInfoEvent("Recovery not needed...", ActivityType.UploadingBootloader);
                }
                else if (progState == -1)
                {
                    CastInfoEvent("Unable to communicate with the ECU...", ActivityType.UploadingBootloader);
                }
                sw.Stop();
            }
            else
            {
                CastInfoEvent("Recovery not needed...", ActivityType.UploadingBootloader);
            }
            workEvent.Result = false;
            return;
        }

        private bool WriteFlashRecover(BlockManager bm)
        {
            int startAddress = 0x020000;
            int saved_progress = 0;
            for (int blockNumber = 0; blockNumber <= 0xF50; blockNumber++)
            {
                int percentage = (int)(((float)blockNumber * 100) / 3920F);
                if (percentage > saved_progress)
                {
                    CastProgressWriteEvent(percentage);
                    saved_progress = percentage;
                }
                byte[] data2Send = bm.GetNextBlock();
                int length = 0xF0;
                if (blockNumber == 0xF50) length = 0xE6;
                if (SendTransferData(length, startAddress + (blockNumber * 0xEA), 0x311))
                {
                    //canUsbDevice.RequestDeviceReady();
                    // send the data from the block
                    // calculate number of frames
                    int numberOfFrames = (int)data2Send.Length / 7; // remnants?
                    if (((int)data2Send.Length % 7) > 0) numberOfFrames++;
                    byte iFrameNumber = 0x21;
                    int txpnt = 0;
                    CANMessage msg = new CANMessage(0x7E0, 0, 8);
                    for (int frame = 0; frame < numberOfFrames; frame++)
                    {
                        var cmd = BitTools.GetFrameBytes(iFrameNumber, data2Send, txpnt);
                        msg.setData(cmd);
                        txpnt += 7;
                        iFrameNumber++;
                        if (iFrameNumber > 0x2F) iFrameNumber = 0x20;
                        msg.elmExpectedResponses = frame == numberOfFrames - 1 ? 1 : 0;

                        if (frame == numberOfFrames - 1)
                            m_canListener.ClearQueue();
                        if (!canUsbDevice.sendMessage(msg))
                        {
                            logger.Debug("Couldn't send message");
                        }
                        if (m_sleepTime > 0)
                            Thread.Sleep(m_sleepTime);
                    }
                    // now wait for 01 76 00 00 00 00 00 00 
                    ulong data = m_canListener.waitMessage(timeoutP2ct, 0x7E8).getData();
                    if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x76)
                    {
                        _stallKeepAlive = false;
                        return false;
                    }
                    //canUsbDevice.RequestDeviceReady();
                    BroadcastKeepAlive();

                }
            }
            return true;
        }

        public void WriteFlash(object sender, DoWorkEventArgs workEvent)
        {
            BackgroundWorker bw = sender as BackgroundWorker;
            string filename = (string)workEvent.Argument;

            if (!canUsbDevice.isOpen()) return;
            _needRecovery = false;
            BlockManager bm = new BlockManager();
            bm.SetFilename(filename);

            _stallKeepAlive = true;

            SendKeepAlive();
            sw.Reset();
            sw.Start();
            CastInfoEvent("Starting session", ActivityType.UploadingBootloader);
            StartSession10();
            CastInfoEvent("Telling ECU to clear CANbus", ActivityType.UploadingBootloader);
            SendShutup();
            SendA2();
            SendA5();
            SendA503();
            Thread.Sleep(50);
            SendKeepAlive();

            // verified upto here

            _securityLevel = AccessLevel.AccessLevel01;
            //CastInfoEvent("Requesting security access", ActivityType.UploadingBootloader);
            if (!RequestSecurityAccess(0))   // was 2000 milli-seconds
            {
                CastInfoEvent("Failed to get security access", ActivityType.UploadingFlash);
                _stallKeepAlive = false;
                workEvent.Result = false;
                return;
            }
            Thread.Sleep(50);
            CastInfoEvent("Uploading bootloader", ActivityType.UploadingBootloader);
            if (!UploadBootloaderWrite())
            {
                CastInfoEvent("Failed to upload bootloader", ActivityType.UploadingFlash);
                _stallKeepAlive = false;
                workEvent.Result = false;
                return;
            }
            CastInfoEvent("Starting bootloader", ActivityType.UploadingBootloader);
            // start bootloader in ECU
            //SendKeepAlive();
            Thread.Sleep(50);
            if (!StartBootloader(0x102460))
            {
                CastInfoEvent("Failed to start bootloader", ActivityType.UploadingFlash);
                _stallKeepAlive = false;
                workEvent.Result = false;
                return;
            }
            Thread.Sleep(100);
            SendKeepAlive();
            Thread.Sleep(50);

            CastInfoEvent("Erasing FLASH", ActivityType.StartErasingFlash);
            if (SendrequestDownload(6, false, false))
            {
                _needRecovery = true;
                SendShutup();
                CastInfoEvent("Programming FLASH", ActivityType.UploadingFlash);
                bool success = ProgramFlash(bm);

                if (success)
                    CastInfoEvent("FLASH upload completed", ActivityType.ConvertingFile);
                else
                    CastInfoEvent("FLASH upload failed", ActivityType.ConvertingFile);

                sw.Stop();
                _needRecovery = false;

                // what else to do?
                Send0120();
                CastInfoEvent("Session ended", ActivityType.FinishedFlashing);
            }
            else
            {
                sw.Stop();
                _needRecovery = false;
                _stallKeepAlive = false;
                CastInfoEvent("Failed to erase FLASH", ActivityType.ConvertingFile);
                Send0120();
                CastInfoEvent("Session ended", ActivityType.FinishedFlashing);
                workEvent.Result = false;
                return;

            }
            _stallKeepAlive = false;
            workEvent.Result = true;
        }

        private bool ProgramFlash(BlockManager bm)
        {
            const int startAddress = 0x020000;
            int lastBlockNumber = bm.GetLastBlockNumber();
            int saved_progress = 0;

            for (int blockNumber = 0; blockNumber <= lastBlockNumber; blockNumber++) // All blocks == 0xF50
            {
                int percentage = (int)(((float)blockNumber * 100) / (float)lastBlockNumber);
                if (percentage > saved_progress)
                {
                    CastProgressWriteEvent(percentage);
                    saved_progress = percentage;
                }
                byte[] data2Send = bm.GetNextBlock();
                int length = 0xF0;
                if (blockNumber == 0xF50) length = 0xE6;

                int currentAddress = startAddress + (blockNumber * 0xEA);
                sw.Reset();
                sw.Start();
                if (SendTransferData(length, currentAddress, 0x7E8))
                {
                    canUsbDevice.RequestDeviceReady();
                    // calculate number of frames
                    int numberOfFrames = (int)data2Send.Length / 7; // remnants?
                    if (((int)data2Send.Length % 7) > 0) numberOfFrames++;
                    byte iFrameNumber = 0x21;
                    int txpnt = 0;
                    CANMessage msg = new CANMessage(0x7E0, 0, 8);
                    for (int frame = 0; frame < numberOfFrames; frame++)
                    {
                        var cmd = BitTools.GetFrameBytes(iFrameNumber, data2Send, txpnt);
                        msg.setData(cmd);
                        txpnt += 7;
                        iFrameNumber++;
                        if (iFrameNumber > 0x2F) iFrameNumber = 0x20;
                        msg.elmExpectedResponses = (frame == numberOfFrames - 1) ? 1 : 0;

                        if (frame == numberOfFrames - 1)
                            m_canListener.ClearQueue();

                        if (!canUsbDevice.sendMessage(msg))
                        {
                            logger.Debug("Couldn't send message");
                        }
                        if (m_sleepTime > 0)
                            Thread.Sleep(m_sleepTime);
                    }
                    Application.DoEvents();

                    // now wait for 01 76 00 00 00 00 00 00 
                    ulong data = m_canListener.waitMessage(timeoutP2ct, 0x7E8).getData();
                    if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x76)
                    {
                        CastInfoEvent("Got incorrect response " + data.ToString("X16"), ActivityType.UploadingFlash);
                        _stallKeepAlive = false;
                        return false;
                    }
                    canUsbDevice.RequestDeviceReady();
                    SendKeepAlive();
                }
                sw.Stop();
            }
            return true;
        }

        public void ReadFlash(object sender, DoWorkEventArgs workEvent)
        {
            BackgroundWorker bw = sender as BackgroundWorker;
            string filename = (string)workEvent.Argument;

            _stallKeepAlive = true;
            bool success = false;
            int retryCount = 0;
            int startAddress = 0x000000;
            int lastAddress = 0x100000;
            int blockSize = 0x80; // defined in bootloader... keep it that way!
            int bufpnt = 0;
            byte[] buf = new byte[lastAddress];
            // Pre-fill buffer with 0xFF (unprogrammed FLASH chip value)
            for (int i = 0; i < lastAddress; i++) buf[i] = 0xFF;
            SendKeepAlive();
            sw.Reset();
            sw.Start();
            CastInfoEvent("Starting session", ActivityType.UploadingBootloader);
            StartSession1081();
            StartSession10();
            CastInfoEvent("Telling ECU to clear CANbus", ActivityType.UploadingBootloader);
            SendShutup();
            SendA2();
            SendA5();
            SendA503();
            Thread.Sleep(50);
            SendKeepAlive();
            _securityLevel = AccessLevel.AccessLevel01;
            CastInfoEvent("Requesting security access", ActivityType.UploadingBootloader);
            if(!RequestSecurityAccess(0))
                return;
            Thread.Sleep(50);

            CastInfoEvent("Uploading bootloader", ActivityType.UploadingBootloader);
            if (!UploadBootloaderRead())
            {
                CastInfoEvent("Uploading bootloader FAILED", ActivityType.UploadingBootloader);
                workEvent.Result = false;
                return;
            }
            CastInfoEvent("Starting bootloader", ActivityType.UploadingBootloader);
            // start bootloader in ECU
            Thread.Sleep(50);
            StartBootloader(0x102460);
            SendKeepAlive();
            Thread.Sleep(100);

            CastInfoEvent("Downloading FLASH", ActivityType.DownloadingFlash);

            Stopwatch keepAliveSw = new Stopwatch();
            keepAliveSw.Start();

            int saved_progress = 0;

            // Determine last part of the FLASH chip that is used (to save time when reading (DUMPing))
            // Address 0x020140 stores a pointer to the BIN file Header which is the last used area in FLASH
            byte[] buffer = readDataByLocalIdentifier(false, 6, 0x020140, blockSize, out success);
            if (success)
            {
                if (buffer.Length == blockSize)
                {
                    lastAddress = (int)buffer[1] << 16 | (int)buffer[2] << 8 | (int)buffer[3];
                    // Add another 512 bytes to include header region (with margin)!!!
                    lastAddress += 0x200;
                }
            }
            CastInfoEvent("Downloading " + lastAddress.ToString("D") + " Bytes.", ActivityType.DownloadingFlash);

            // now start sending commands:
            //06 21 80 00 00 00 00 00 
            // response: 
            //10 82 61 80 00 10 0C 00 // 4 bytes data already

            while (startAddress < lastAddress)
                {
                if (!canUsbDevice.isOpen())
                {
                    _stallKeepAlive = false;
                    workEvent.Result = false;
                    return;
                }

                byte[] readbuf = readDataByLocalIdentifier(false, 6, startAddress, blockSize, out success);
                if (success)
                {
                    if (readbuf.Length == blockSize)
                    {
                        for (int j = 0; j < blockSize; j++)
                        {
                            buf[bufpnt++] = readbuf[j];
                        }
                    }
                    //string infoStr = "Address: " + startAddress.ToString("X8"); //+ " ";
                    int percentage = (int)((bufpnt * 100) / (float)lastAddress);
                    if (percentage > saved_progress)
                    {
                        CastProgressReadEvent(percentage);
                        saved_progress = percentage;
                    }
                    startAddress += blockSize;
                    retryCount = 0;
                }
                else
                {
                    CastInfoEvent("Frame dropped, retrying " + startAddress.ToString("X8") + " " + retryCount.ToString(), ActivityType.DownloadingFlash);
                    retryCount++;
                    // read all available message from the bus now

                    for (int i = 0; i < 10; i++)
                    {
                        CANMessage response = new CANMessage();
                        ulong data = 0;
                        response = new CANMessage();
                        response = m_canListener.waitMessage(10);
                        data = response.getData();
                    }
                    if (retryCount == maxRetries)
                    {
                        CastInfoEvent("Failed to download FLASH content", ActivityType.ConvertingFile);
                        _stallKeepAlive = false;
                        workEvent.Result = false;
                        return;
                    }
                }
                if (keepAliveSw.ElapsedMilliseconds > 3000) // once every 3 seconds
                {
                    keepAliveSw.Stop();
                    keepAliveSw.Reset();
                    SendKeepAlive();
                    keepAliveSw.Start();
                }
                Application.DoEvents();
            }
            sw.Stop();
            _stallKeepAlive = false;

            if (buf != null)
            {
                try
                {
                    File.WriteAllBytes(filename, buf);
                    Md5Tools.WriteMd5HashFromByteBuffer(filename, buf);
                    ChecksumResult checksumResult = ChecksumT8.VerifyChecksum(filename, false, m_ShouldUpdateChecksum);
                    if (checksumResult != ChecksumResult.Ok)
                    {
                        CastInfoEvent("Checksum check failed: " + checksumResult, ActivityType.ConvertingFile);
                        workEvent.Result = false;
                    }
                    else
                    {
                        CastInfoEvent("Download done", ActivityType.FinishedDownloadingFlash);
                        workEvent.Result = true;
                    }
                }
                catch (Exception e)
                {
                    CastInfoEvent("Could not write file... " + e.Message, ActivityType.ConvertingFile);
                    workEvent.Result = false;
                }
            }
            else
            {
                workEvent.Result = false;
            }
            return;
        }

        private bool updateChecksum(string layer, string filechecksum, string realchecksum)
        {
            CastInfoEvent(layer, ActivityType.ConvertingFile);
            CastInfoEvent("File Checksum: " + filechecksum, ActivityType.ConvertingFile);
            CastInfoEvent("Real Checksum: " + realchecksum, ActivityType.ConvertingFile);

            // Danger do not ever change return to true
            return false;
        }

        private bool SendrequestDownload(byte PCI, bool recoveryMode, bool LegionMode)
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 7);
            //06 34 01 00 00 00 00 00
            //ulong cmd = 0x0000000000013406;
            ulong tmp = ((formatmask & 0xff) << 8 | (formatmask >> 8) & 0xFF);
            ulong cmd = LegionMode ? tmp << 40 | ((~tmp) & 0xFFFF) << 24 | 0x13400 : 0x0000000000013400;
            cmd += PCI;

            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            eraseSw.Reset();
            eraseSw.Start();
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            bool eraseDone = false;
            int eraseCount = 0;
            bool Firstpass = true;
            int retryCount = 0;

            while (!eraseDone)
            {
                // ELM327 is one annoying adapter; It's hard to fix the stock loader but it is at least possible to work around it in the new loader
                if (!LegionMode)
                {
                    m_canListener.setupWaitMessage(0x7E8);
                    CANMessage response = m_canListener.waitMessage(500);
                    ulong data = response.getData();

                    // CastInfoEvent("Data1: " + data.ToString("X16"), ActivityType.ErasingFlash);
                    if (data == 0)
                    {
                        m_canListener.setupWaitMessage(0x311);
                        response = m_canListener.waitMessage(500);
                        data = response.getData();
                        // CastInfoEvent("Data2: " + data.ToString("X16"), ActivityType.ErasingFlash);
                    }

                    // response will be 03 7F 34 78 00 00 00 00 a couple of times while erasing
                    if (getCanData(data, 0) == 0x03 && getCanData(data, 1) == 0x7F && getCanData(data, 2) == 0x34 && getCanData(data, 3) == 0x78)
                    {
                        if (recoveryMode) BroadcastKeepAlive();
                        else SendKeepAlive();
                        eraseCount++;
                        string info = "Erasing FLASH";
                        for (int i = 0; i < eraseCount; i++) info += ".";
                        CastInfoEvent(info, ActivityType.ErasingFlash);
                    }
                    else if (getCanData(data, 0) == 0x01 && getCanData(data, 1) == 0x74)
                    {
                        if (recoveryMode) BroadcastKeepAlive();
                        else SendKeepAlive();
                        eraseDone = true;
                        eraseSw.Stop();
                        CastInfoEvent(string.Format("Erase completed after: {0} minutes and {1} seconds", eraseSw.Elapsed.Minutes, eraseSw.Elapsed.Seconds), ActivityType.ErasingFlash);
                        return true;
                    }
                    else if (getCanData(data, 0) == 0x03 && getCanData(data, 1) == 0x7F && getCanData(data, 2) == 0x34 && getCanData(data, 3) == 0x11)
                    {
                        eraseSw.Stop();
                        CastInfoEvent("Erase cannot be performed", ActivityType.ErasingFlash);
                        return false;
                    }
                    else
                    {
                        logger.Debug("Rx: " + data.ToString("X16"));
                        if (canUsbDevice is CANELM327Device)
                        {
                            if (recoveryMode) BroadcastKeepAlive();
                            else SendKeepAlive();
                        }
                    }

                    if (eraseSw.Elapsed.Minutes >= 2)
                    {
                        eraseSw.Stop();
                        if (canUsbDevice is CANELM327Device)
                        {
                            CastInfoEvent("Erase completed", ActivityType.ErasingFlash);
                            // ELM327 seem to be unable to wait long enough for this response
                            // Instead we assume its finished ok now
                            return true;
                        }
                        else
                        {
                            CastInfoEvent("Erase timed out after 2 minutes", ActivityType.ErasingFlash);
                            return false;
                        }
                    }

                    if (eraseSw.Elapsed.Minutes == 0)
                    {
                        CastInfoEvent(string.Format("Erasing FLASH waited: {0} seconds", eraseSw.Elapsed.Seconds), ActivityType.ErasingFlash);
                    }
                    else
                    {
                        CastInfoEvent(string.Format("Erasing FLASH waited: {0} minutes and {1} seconds", eraseSw.Elapsed.Minutes, eraseSw.Elapsed.Seconds), ActivityType.ErasingFlash);
                    }
                }
                else
                {
                    // Send this information as a data frame to make elm behave
                    bool success;
                    byte[] formatbuf = readDataByLocalIdentifier(LegionMode, 0xF0, 0, 4, out success);
                    if (success)
                    {
                        retryCount = 0;
                        // Check if the loader missed the request, we should see a number higher than 0 in byte[7] ( Byte[3] after it has been read by readDataByLocalIdentifier() )
                        if (Firstpass)
                        {
                            if (formatbuf[3] != PCI)
                            {
                                Thread.Sleep(5);
                                msg.setData(cmd);
                                m_canListener.setupWaitMessage(0x7E8);

                                if (!canUsbDevice.sendMessage(msg))
                                {
                                    CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                                    return false;
                                }
                                CastInfoEvent("Wrong response. Resending request.. ", ActivityType.ErasingFlash);
                            }
                            else
                            {
                                CastInfoEvent(string.Format("Erasing device {0}.. ", formatbuf[3]), ActivityType.ErasingFlash);
                                Firstpass = false;
                            }
                        }
                        else
                        {
                            // Avoid confusion; Don't show the response if it's 1
                            if (formatbuf[3] > 1) {

                                eraseCount++;
                                string info = "";
                                for (int i = 0; i < eraseCount; i++) info += ".";
                                if((eraseCount&3)==2)
                                    CastInfoEvent(info, ActivityType.ErasingFlash);

                            }
                            if (formatbuf[3] == 1)
                            {
                                if (eraseSw.Elapsed.Minutes == 0)
                                {
                                    CastInfoEvent(String.Format("Erase completed after {0} seconds", eraseSw.Elapsed.Seconds), ActivityType.ErasingFlash);
                                }
                                else
                                {
                                    CastInfoEvent(string.Format("Erase completed after: {0} minutes and {1} seconds", eraseSw.Elapsed.Minutes, eraseSw.Elapsed.Seconds), ActivityType.ErasingFlash);
                                }
                                eraseDone = true;
                                return true;
                            }
                        }
                    }
                    else
                        retryCount++;

                    Thread.Sleep(500);

                    if (retryCount > 20)
                    {
                        CastInfoEvent("Erase failed; No response", ActivityType.ErasingFlash);
                        return false;
                    }
                    
                }
                Thread.Sleep(m_sleepTime);
            }
            return true;
        }

        private bool SendTransferData011(int length, int address, uint waitforResponseID)
        {
            CANMessage msg = new CANMessage(0x11, 0, 8); // <GS-24052011> test for ELM327, set length to 16 (0x10)
            ulong cmd = 0x0000000000360010; // 0x36 = transferData
            ulong addressHigh = (uint)address & 0x0000000000FF0000;
            addressHigh /= 0x10000;
            ulong addressMiddle = (uint)address & 0x000000000000FF00;
            addressMiddle /= 0x100;
            ulong addressLow = (uint)address & 0x00000000000000FF;
            ulong len = (ulong)length;

            cmd |= (addressLow * 0x100000000000000);
            cmd |= (addressMiddle * 0x1000000000000);
            cmd |= (addressHigh * 0x10000000000);
            cmd |= (len * 0x100);
            //logger.Debug("send: " + cmd.ToString("X16"));
            msg.elmExpectedResponses = 1;
            msg.setData(cmd);
            m_canListener.setupWaitMessage(waitforResponseID);
            if (!canUsbDevice.sendMessage(msg))
            {
                logger.Debug("Couldn't send message");
            }

            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            //logger.Debug("Received in SendTransferData: " + data.ToString("X16"));
            if (getCanData(data, 0) != 0x30 || getCanData(data, 1) != 0x00)
            {
                return false;
            }
            return true;
        }

        private bool SendTransferData(int length, int address, uint waitforResponseID)
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 8); // <GS-24052011> test for ELM327, set length to 16 (0x10)
            ulong cmd = 0x0000000000360010; // 0x36 = transferData
            ulong addressHigh = (uint)address & 0x0000000000FF0000;
            addressHigh /= 0x10000;
            ulong addressMiddle = (uint)address & 0x000000000000FF00;
            addressMiddle /= 0x100;
            ulong addressLow = (uint)address & 0x00000000000000FF;
            ulong len = (ulong)length;

            cmd |= (addressLow * 0x100000000000000);
            cmd |= (addressMiddle * 0x1000000000000);
            cmd |= (addressHigh * 0x10000000000);
            cmd |= (len * 0x100);
            //logger.Debug("send: " + cmd.ToString("X16"));

            msg.setData(cmd);
            msg.elmExpectedResponses = 1;
            m_canListener.setupWaitMessage(waitforResponseID);
            if (!canUsbDevice.sendMessage(msg))
            {
                logger.Debug("Couldn't send message");
            }

            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            //logger.Debug("Received in SendTransferData: " + data.ToString("X16"));
            if (getCanData(data, 0) != 0x30 || getCanData(data, 1) != 0x00)
            {
                return false;
            }
            return true;
        }


        ///////////////////////////////////////////////////////////////////////////
        /// Flash and read methods specifically for Motronic ME9.6 with GMLAN
        ///

        public void ReadFlashME96(object sender, DoWorkEventArgs workEvent)
        {
            BackgroundWorker bw = sender as BackgroundWorker;
            FlashReadArguments args = (FlashReadArguments)workEvent.Argument;
            string filename = args.FileName;
            int start = args.start;
            int end = args.end;

            _stallKeepAlive = true;
            bool success = false;
            bool readSecondary = end == 0x280000;
            int retryCount = 0;
            int startAddress = start;
            int range = end - start;
            int blockSize = 0x80;
            int bufpnt = startAddress;
            byte[] buf = new byte[(end <= 0x200000) ? 0x200000 : 0x280000];
            // Pre-fill buffer with 0xFF (unprogrammed FLASH chip value)
            for (int i = 0; i < buf.Length; i++)
            {
                buf[i] = 0xFF;
            }
            SendKeepAlive();
            sw.Reset();
            sw.Start();
            CastInfoEvent("Starting session", ActivityType.UploadingBootloader);
            StartSession10();
            CastInfoEvent("Telling ECU to clear CANbus", ActivityType.UploadingBootloader);
            SendShutup();
            Thread.Sleep(50);
            SendKeepAlive();
            _securityLevel = AccessLevel.AccessLevel01;
            CastInfoEvent("Requesting security access", ActivityType.UploadingBootloader);
            if (!RequestSecurityAccess(0))
                return;
            Thread.Sleep(50);

            CastInfoEvent("Downloading FLASH", ActivityType.DownloadingFlash);

            _stallKeepAlive = true;
            int saved_progress = 0;
            success = false;
            while (bufpnt < end)
            {
                if (!canUsbDevice.isOpen())
                {
                    _stallKeepAlive = false;
                    workEvent.Result = false;
                    return;
                }

                byte[] readbuf = sendReadCommandME96(startAddress, blockSize, out success);
                Thread.Sleep(1);
                if (success)
                {
                    if (readbuf.Length == blockSize)
                    {
                        for (int j = 0; j < blockSize; j++)
                        {
                            buf[bufpnt++] = readbuf[j];
                        }
                    }
                    int percentage = (int)((float)100*(bufpnt-start) / (float)range);
                    if (percentage > saved_progress)
                    {
                        CastProgressReadEvent(percentage);
                        saved_progress = percentage;
                    }
                    retryCount = 0;
                    startAddress += blockSize;
                }
                else
                {
                    CastInfoEvent("Frame dropped, retrying", ActivityType.DownloadingFlash);
                    retryCount++;
                    if (retryCount == maxRetries)
                    {
                        CastInfoEvent("Failed to download Flash content", ActivityType.ConvertingFile);
                        _stallKeepAlive = false;
                        workEvent.Result = false;
                        return;
                    }
                }

                // Handle address gap between main and secondary OS
                if (readSecondary && bufpnt == 0x1F0000)
                {
                    bufpnt = 0x200000;
                    startAddress = 0x400000;
                }

                SendKeepAlive();
            }
            _stallKeepAlive = false;

            if (buf != null)
            {
                try
                {
                    File.WriteAllBytes(filename, buf);
                    Md5Tools.WriteMd5HashFromByteBuffer(filename, buf);

                    Dictionary<uint, byte[]> dids = ReadDid();
                    WriteDidFile(filename, dids);

                    CastInfoEvent("Download done", ActivityType.FinishedDownloadingFlash);
                    workEvent.Result = true;
                }
                catch (Exception e)
                {
                    CastInfoEvent("Could not write file... " + e.Message, ActivityType.ConvertingFile);
                    workEvent.Result = false;
                }
            }
            else
            {
                workEvent.Result = false;
            }
            return;
        }

        public Dictionary<uint, byte[]> ReadDid()
        {
            CastInfoEvent("Start DID read", ActivityType.ConvertingFile);
            Dictionary<uint, byte[]> dids = new Dictionary<uint, byte[]>();
            for (uint i = 0; i < 0xFF; i++)
            {
                byte[] did = RequestECUInfo(i);
                if (did[0] != 0)
                {
                    //CastInfoEvent("Read ID 0x" + i.ToString("X"), ActivityType.ConvertingFile);
                    dids.Add(i, did);
                }
            }
            CastInfoEvent("Completed DID read", ActivityType.ConvertingFile);

            return dids;
        }

        //KWP2000 can read more than 6 bytes at a time.. but for now we are happy with this
        private byte[] sendReadCommandME96(int address, int length, out bool success)
        {

            success = false;
            byte[] retData = new byte[length];
            if (!canUsbDevice.isOpen()) return retData;

            CANMessage msg = new CANMessage(0x7E0, 0, 8);
            //optimize reading speed for ELM
            if (length <= 3)
                msg.elmExpectedResponses = 1;
            //logger.Debug("Reading " + address.ToString("X8") + " len: " + length.ToString("X2"));
            ulong cmd = 0x0000000000002307; // always 2 bytes
            ulong addressHigh = (uint)address & 0x0000000000FF0000;
            addressHigh /= 0x10000;
            ulong addressMiddle = (uint)address & 0x000000000000FF00;
            addressMiddle /= 0x100;
            ulong addressLow = (uint)address & 0x00000000000000FF;
            ulong len = (ulong)length;


            cmd |= (addressLow * 0x10000000000);
            cmd |= (addressMiddle * 0x100000000);
            cmd |= (addressHigh * 0x1000000);
            cmd |= (len * 0x100000000000000);
            //logger.Debug("send: " + cmd.ToString("X16"));
            /*cmd |= (ulong)(byte)(address & 0x000000FF) << 4 * 8;
            cmd |= (ulong)(byte)((address & 0x0000FF00) >> 8) << 3 * 8;
            cmd |= (ulong)(byte)((address & 0x00FF0000) >> 2 * 8) << 2 * 8;
            cmd |= (ulong)(byte)((address & 0xFF000000) >> 3 * 8) << 8;*/
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                logger.Debug("Couldn't send message");

            }
            // wait for max two messages to get rid of the alive ack message
            CANMessage response = new CANMessage();
            ulong data = 0;
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            data = response.getData();

            if (getCanData(data, 0) == 0x7E)
            {
                logger.Debug("Got 0x7E message as response to 0x23, readMemoryByAddress command");
                success = false;
                return retData;
            }
            else if (response.getData() == 0x00000000)
            {
                logger.Debug("Get blank response message to 0x23, readMemoryByAddress");
                success = false;
                return retData;
            }
            else if (getCanData(data, 0) == 0x03 && getCanData(data, 1) == 0x7F && getCanData(data, 2) == 0x23 && getCanData(data, 3) == 0x31)
            {
                // reason was 0x31 RequestOutOfRange
                // memory address is either: invalid, restricted, secure + ECU locked
                // memory size: is greater than max
                logger.Debug("RequestOutOfRange. No security access granted");
                RequestSecurityAccess(0);
                success = false;
                return retData;
            }
            else if (getCanData(data, 0) == 0x03 && getCanData(data, 1) == 0x7F && getCanData(data, 2) == 0x23)
            {
                logger.Debug("readMemoryByAddress " + TranslateErrorCode(getCanData(data, 3)));
                success = false;
                return retData;
            }
            /*else if (getCanData(data, 0) != 0x10)
            {
                AddToCanTrace("Incorrect response message to 0x23, readMemoryByAddress. Byte 0 was " + getCanData(data, 0).ToString("X2"));
                success = false;
                return retData;
            }
            else if (getCanData(data, 1) != len + 4)
            {
                AddToCanTrace("Incorrect length data message to 0x23, readMemoryByAddress.  Byte 1 was " + getCanData(data, 1).ToString("X2"));
                success = false;
                return retData;
            }*/
            else if (getCanData(data, 2) != 0x63 && getCanData(data, 1) != 0x63)
            {
                if (data == 0x0000000000007E01)
                {
                    // was a response to a KA.
                }
                logger.Debug("Incorrect response to 0x23, readMemoryByAddress.  Byte 2 was " + getCanData(data, 2).ToString("X2"));
                success = false;
                return retData;
            }
            //TODO: Check whether we need more than 2 bytes of data and wait for that many records after sending an ACK
            int rx_cnt = 0;
            byte frameIndex = 0x21;
            if (length > 3)
            {
                retData[rx_cnt++] = getCanData(data, 7);
                // in that case, we need more records from the ECU
                // Thread.Sleep(1);
                SendAckMessageT8(); // send ack to request more bytes
                //Thread.Sleep(1);
                // now we wait for the correct number of records to be received
                int m_nrFrameToReceive = (length / 7);
                if (len % 7 > 0)
                {
                    m_nrFrameToReceive++;
                }
                logger.Debug("Number of frames: " + m_nrFrameToReceive.ToString());
                while (m_nrFrameToReceive > 0)
                {
                    // response = new CANMessage();
                    //response.setData(0);
                    //response.setID(0);
                    // m_canListener.setupWaitMessage(0x7E8);
                    response = m_canListener.waitMessage(timeoutP2ct);
                    data = response.getData();
                    logger.Debug("frame " + frameIndex.ToString("X2") + ": " + data.ToString("X16"));
                    if (frameIndex != getCanData(data, 0))
                    {
                        // sequence broken
                        logger.Debug("Received invalid sequenced frame " + frameIndex.ToString("X2") + ": " + data.ToString("X16"));
                        //m_canListener.dumpQueue();
                        success = false;
                        return retData;
                    }
                    else if (data == 0)
                    {
                        logger.Debug("Received blank message while waiting for data");
                        success = false;
                        return retData;
                    }
                    frameIndex++;
                    if (frameIndex == 0x30)
                    {
                        // reset index
                        frameIndex = 0x20;
                    }
                    // additional check for sequencing of frames
                    m_nrFrameToReceive--;
                    logger.Debug("frames left: " + m_nrFrameToReceive.ToString());
                    // add the bytes to the receive buffer
                    //string checkLine = string.Empty;
                    for (uint fi = 1; fi < 8; fi++)
                    {
                        //checkLine += getCanData(data, fi).ToString("X2");
                        if (rx_cnt < retData.Length) // prevent overrun
                        {
                            retData[rx_cnt++] = getCanData(data, fi);
                        }
                    }
                    //logger.Debug("frame(2): " + checkLine);
                    //Thread.Sleep(1);

                }

            }
            else
            {
                if (retData.Length > rx_cnt) retData[rx_cnt++] = getCanData(data, 5);
                if (retData.Length > rx_cnt) retData[rx_cnt++] = getCanData(data, 6);
                if (retData.Length > rx_cnt) retData[rx_cnt++] = getCanData(data, 7);
                logger.Debug("received data: " + retData[0].ToString("X2"));
            }
            /*string line = address.ToString("X8") + " ";
            foreach (byte b in retData)
            {
                line += b.ToString("X2") + " ";
            }
            AddToCanTrace(line);*/
            success = true;

            return retData;
        }

        public void WriteFlashME96(object sender, DoWorkEventArgs workEvent)
        {
            BackgroundWorker bw = sender as BackgroundWorker;
            FlashReadArguments args = (FlashReadArguments)workEvent.Argument;
            string filename = args.FileName;
            int start = args.start;
            int end = args.end;

            if (!canUsbDevice.isOpen()) return;
            _needRecovery = false;

            _stallKeepAlive = true;

            SendKeepAlive();
            sw.Reset();
            sw.Start();
            CastInfoEvent("Starting session", ActivityType.UploadingBootloader);
            StartSession10();
            CastInfoEvent("Telling ECU to clear CANbus", ActivityType.UploadingBootloader);
            SendShutup();
            SendA2();
            SendA5();
            SendA503();
            Thread.Sleep(50);
            SendKeepAlive();

            // verified upto here

            _securityLevel = AccessLevel.AccessLevel01;
            //CastInfoEvent("Requesting security access", ActivityType.UploadingBootloader);
            if (!RequestSecurityAccess(0))   // was 2000 milli-seconds
            {
                CastInfoEvent("Failed to get security access", ActivityType.UploadingFlash);
                _stallKeepAlive = false;
                workEvent.Result = false;
                return;
            }
            Thread.Sleep(50);

            CastInfoEvent("Erasing FLASH", ActivityType.StartErasingFlash);
            if (SendrequestDownloadME96(false))
            {
                _needRecovery = true;
                SendShutup();
                CastInfoEvent("Programming FLASH", ActivityType.UploadingFlash);
                bool success = ProgramFlashME96(filename, start, end);

                if (success)
                    CastInfoEvent("FLASH upload completed", ActivityType.ConvertingFile);
                else
                    CastInfoEvent("FLASH upload failed", ActivityType.ConvertingFile);

                sw.Stop();
                _needRecovery = false;

                // what else to do?
                Send0120();
                CastInfoEvent("Session ended", ActivityType.FinishedFlashing);
            }
            else
            {
                sw.Stop();
                _needRecovery = false;
                _stallKeepAlive = false;
                CastInfoEvent("Failed to erase FLASH", ActivityType.ConvertingFile);
                Send0120();
                CastInfoEvent("Session ended", ActivityType.FinishedFlashing);
                workEvent.Result = false;
                return;

            }
            _stallKeepAlive = false;
            workEvent.Result = true;
        }

        private bool SendrequestDownloadME96(bool recoveryMode)
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 7);
            //      no enc/no compress
            //      |  MSB numberofbytes
            //      |  |     LSB
            //      |  |     |  -- --
            //05 34 00 01 E0 00 00 00
            // 0x01E000=122 880 bytes
            //ulong cmd = 0x000000E001003405;
            // 0x180000=1 572 864 bytes
            ulong cmd = 0x0000000018003405;
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }
            bool eraseDone = false;
            int eraseCount = 0;
            int waitCount = 0;
            while (!eraseDone)
            {
                m_canListener.setupWaitMessage(0x7E8); // TEST ELM327 31082011
                CANMessage response = new CANMessage();
                response = m_canListener.waitMessage(500); // 1 seconds!
                ulong data = response.getData();
                if (data == 0)
                {
                    m_canListener.setupWaitMessage(0x311); // TEST ELM327 31082011
                    response = new CANMessage();
                    response = m_canListener.waitMessage(500); // 1 seconds!
                    data = response.getData();
                }
                // response will be 03 7F 34 78 00 00 00 00 a couple of times while erasing
                if (getCanData(data, 0) == 0x03 && getCanData(data, 1) == 0x7F && getCanData(data, 2) == 0x34 && getCanData(data, 3) == 0x78)
                {
                    if (recoveryMode) BroadcastKeepAlive();
                    else SendKeepAlive();
                    eraseCount++;
                    string info = "Erasing FLASH";
                    for (int i = 0; i < eraseCount; i++) info += ".";
                    CastInfoEvent(info, ActivityType.ErasingFlash);
                }
                else if (getCanData(data, 0) == 0x01 && getCanData(data, 1) == 0x74)
                {
                    if (recoveryMode) BroadcastKeepAlive();
                    else SendKeepAlive();
                    eraseDone = true;
                    return true;
                }
                else if (getCanData(data, 0) == 0x03 && getCanData(data, 1) == 0x7F && getCanData(data, 2) == 0x34 && getCanData(data, 3) == 0x11)
                {
                    CastInfoEvent("Erase cannot be performed", ActivityType.ErasingFlash);
                    return false;
                }
                else
                {
                    logger.Debug("Rx: " + data.ToString("X16"));
                    if (canUsbDevice is CANELM327Device)
                    {
                        if (recoveryMode) BroadcastKeepAlive();
                        else SendKeepAlive();
                    }
                }
                waitCount++;
                if (waitCount > 35)
                {
                    if (canUsbDevice is CANELM327Device)
                    {
                        CastInfoEvent("Erase completed", ActivityType.ErasingFlash);
                        // ELM327 seem to be unable to wait long enough for this response
                        // Instead we assume its finished ok after 35 seconds
                        return true;
                    }
                    else
                    {
                        CastInfoEvent("Erase timed out after 35 seconds", ActivityType.ErasingFlash);
                        return false;
                    }
                }
                Thread.Sleep(m_sleepTime);

            }
            return true;
        }

        private bool ProgramFlashME96(string filename, int start, int end)
        {
            bool result = false;

            int startAddress = start;
            int range = end - start;
            int blockSize = 0xFF8;//4088
            int bufsize = 0xFFF;//4095
            int bufpnt = startAddress;
            int saved_progress = 0;
            byte[] filebytes = File.ReadAllBytes(filename);
            bool writeSecondary = (end == 0x280000 && filebytes.Length == 0x280000);

            while (bufpnt < end)
            {
                int percentage = (int)((float)100 * (bufpnt - start) / (float)range);
                if (percentage > saved_progress)
                {
                    CastProgressWriteEvent(percentage);
                    saved_progress = percentage;
                }

                if (end - bufpnt < blockSize)
                {
                    blockSize = end - bufpnt;
                }
                if (startAddress == 0x1BF400)
                {
                    blockSize = 0xC00;
                }
                if (startAddress == 0x1DFF10)
                {
                    blockSize = 0x0F0;
                }
                byte[] data2Send = new byte[bufsize];
                for (int j = 0; j < blockSize; j++)
                {
                    data2Send[j] = filebytes[bufpnt];
                    bufpnt++;
                }

                sw.Reset();
                sw.Start();

                int count = 0;
                bool status = false;
                while (count < 5 && !status)
                {
                    status = SendTransferDataME96(blockSize, startAddress, 0x7E8, data2Send[0]);
                    logger.Debug("SendTransferData status:" + status);
                    count++;
                }

                if (status)
                {
                    canUsbDevice.RequestDeviceReady();
                    // calculate number of frames
                    int numberOfFrames = (int)(blockSize - 1) / 7; // remnants?
                    if (((int)(blockSize - 1) % 7) > 0) numberOfFrames++;
                    byte iFrameNumber = 0x21;
                    int txpnt = 1; // First data byte allready sent in SendTransferData
                    CANMessage msg = new CANMessage(0x7E0, 0, 8);
                    for (int frame = 0; frame < numberOfFrames; frame++)
                    {
                        var cmd = BitTools.GetFrameBytes(iFrameNumber, data2Send, txpnt);
                        msg.setData(cmd);
                        txpnt += 7;
                        iFrameNumber++;
                        if (iFrameNumber > 0x2F) iFrameNumber = 0x20;
                        msg.elmExpectedResponses = (frame == numberOfFrames - 1) ? 1 : 0;

                        if (frame == numberOfFrames - 1)
                            m_canListener.ClearQueue();

                        if (!canUsbDevice.sendMessage(msg))
                        {
                            logger.Debug("Couldn't send message");
                        }
                        if (m_sleepTime > 0)
                            Thread.Sleep(m_sleepTime);
                    }
                    Application.DoEvents();

                    ulong data = m_canListener.waitMessage(timeoutP2ce, 0x7E8).getData();
                    while (true)
                    {
                        // RequestCorrectlyReceived-ResponsePending ($78, RC_RCR-RP)
                        if (getCanData(data, 0) == 0x03 && getCanData(data, 1) == 0x7F && getCanData(data, 2) == 0x36 && getCanData(data, 3) == 0x78)
                        {
                            //CastInfoEvent("RequestCorrectlyReceived-ResponsePending", ActivityType.UploadingFlash);
                            if (canUsbDevice is CANELM327Device)
                            {
                                CastInfoEvent("Response timedout, ELM327 will wait 35 seconds", ActivityType.ErasingFlash);
                                for (int i = 0; i < 35; i++)
                                {
                                    SendKeepAlive();
                                    Thread.Sleep(1000);
                                }
                                break;
                            }
                        }
                        else if (getCanData(data, 0) == 0x03 && getCanData(data, 1) == 0x7F && getCanData(data, 2) == 0x36)
                        {
                            CastInfoEvent("Error: " + TranslateErrorCode(getCanData(data, 3)), ActivityType.ConvertingFile);
                        }
                        //wait for 01 76 00 00 00 00 00 00 
                        else if (getCanData(data, 0) == 0x01 || getCanData(data, 1) == 0x76)
                        {
                            break;
                        }
                        data = m_canListener.waitMessage(timeoutP2ce, 0x7E8).getData();
                    }
                    canUsbDevice.RequestDeviceReady();
                    SendKeepAlive();
                }
                else
                {
                    CastInfoEvent("Error SendTransferData, ran out of retries", ActivityType.FinishedFlashing);
                    break;
                }
                sw.Stop();

                startAddress += blockSize;
                
                // Handle gaps
                if (bufpnt == 0x1C0000)
                {
                    bufpnt = 0x1C2000;
                    startAddress = 0x1C2000;
                }
                // Handle address gap between main and secondary OS
                if (writeSecondary && bufpnt == 0x1E0000)
                {
                    bufpnt = 0x204000;
                    startAddress = 0x404000;
                }
            }

            if (bufpnt >= end)
            {
                CastProgressWriteEvent(100);
            }
            return true;
        }

        private bool SendTransferDataME96(int length, int address, uint waitforResponseID, byte firstByteToSend )
        {
            bool result = false;

            logger.Debug("SendTransferDataME96 address:" + address.ToString("X"));
            CANMessage msg = new CANMessage(0x7E0, 0, 8); // <GS-24052011> test for ELM327, set length to 16 (0x10)
            ulong cmd = 0x0000000000360010; // 0x36 = transferData
            ulong addressHigh = (uint)address & 0x0000000000FF0000;
            addressHigh /= 0x10000;
            ulong addressMiddle = (uint)address & 0x000000000000FF00;
            addressMiddle /= 0x100;
            ulong addressLow = (uint)address & 0x00000000000000FF;
            
            ulong total = (ulong)length + 5;  // The extra 5 comes from the Service ID plus the sub-function parameter byte plus the 3 byte startingAddress.
            ulong lenLow = total & 0xFF;
            ulong lenHigh = (total & 0xF00) >> 8;

            ulong payload = (ulong)firstByteToSend;

            cmd |= (payload * 0x100000000000000);
            cmd |= (addressLow * 0x1000000000000);
            cmd |= (addressMiddle * 0x10000000000);
            cmd |= (addressHigh * 0x100000000);
            cmd |= (lenLow * 0x100);
            cmd |= lenHigh;

            logger.Debug("send: " + cmd.ToString("X16"));

            msg.setData(cmd);
            msg.elmExpectedResponses = 1;
            m_canListener.setupWaitMessage(waitforResponseID);
            if (!canUsbDevice.sendMessage(msg))
            {
                logger.Debug("Couldn't send message");
            }

            CANMessage response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();
            while (true)
            {
                logger.Debug("Received in SendTransferData: " + data.ToString("X16"));
                
                // RequestCorrectlyReceived-ResponsePending ($78, RC_RCR-RP)
                if (getCanData(data, 0) == 0x03 && getCanData(data, 1) == 0x7F && getCanData(data, 2) == 0x36 && getCanData(data, 3) == 0x78)
                {
                    CastInfoEvent("RequestCorrectlyReceived-ResponsePending", ActivityType.UploadingFlash);
                    if (canUsbDevice is CANELM327Device)
                    {
                        CastInfoEvent("Response timedout, ELM327 will wait 5 seconds", ActivityType.ErasingFlash);
                        for (int i = 0; i < 5; i++)
                        {
                            SendKeepAlive();
                            Thread.Sleep(1000);
                        }
                        result = true;
                        break;
                    }
                }
                else if (response.getCanData(1) == 0x7F && response.getCanData(2) == 0x36)
                {
                    CastInfoEvent("Error: " + TranslateErrorCode(response.getCanData(3)), ActivityType.ConvertingFile);
                    break;
                }
                else if (getCanData(data, 0) == 0x30 || getCanData(data, 1) == 0x00)
                {
                    result = true;
                    break;
                }
                data = m_canListener.waitMessage(timeoutP2ce).getData();
            }
            return result;
        }

        public void RestoreT8(object sender, DoWorkEventArgs workEvent)
        {
            BackgroundWorker bw = sender as BackgroundWorker;
            string filename = (string)workEvent.Argument;

            if (!canUsbDevice.isOpen()) return;
            _needRecovery = false;
            BlockManager bm = new BlockManager();
            bm.SetFilename(filename);

            _stallKeepAlive = true;

            int waitCount = 0;
            bool restored = false;
            CastInfoEvent("Reset ECU now. Turn off and on power!", ActivityType.UploadingBootloader);
            while (waitCount < 300 && !restored)
            {
                restored = SendRestoreT8();
                waitCount++;
            }

            if (!restored)
            {
                CastInfoEvent("Failed to restore ECU", ActivityType.ConvertingFile);
                workEvent.Result = false;
                return;
            }

            SendKeepAlive();
            sw.Reset();
            sw.Start();
            CastInfoEvent("Starting session", ActivityType.UploadingBootloader);
            StartSession10();
            CastInfoEvent("Telling ECU to clear CANbus", ActivityType.UploadingBootloader);
            SendShutup();
            SendA2();
            SendA5();
            SendA503();
            Thread.Sleep(50);
            SendKeepAlive();

            // verified upto here

            _securityLevel = AccessLevel.AccessLevel01;
            //CastInfoEvent("Requesting security access", ActivityType.UploadingBootloader);
            if (!RequestSecurityAccess(0))   // was 2000 milli-seconds
            {
                CastInfoEvent("Failed to get security access", ActivityType.UploadingFlash);
                _stallKeepAlive = false;
                workEvent.Result = false;
                return;
            }
            Thread.Sleep(50);
            CastInfoEvent("Uploading bootloader", ActivityType.UploadingBootloader);
            if (!UploadBootloaderWrite())
            {
                CastInfoEvent("Failed to upload bootloader", ActivityType.UploadingFlash);
                _stallKeepAlive = false;
                workEvent.Result = false;
                return;
            }
            CastInfoEvent("Starting bootloader", ActivityType.UploadingBootloader);
            // start bootloader in ECU
            //SendKeepAlive();
            Thread.Sleep(50);
            if (!StartBootloader(0x102460))
            {
                CastInfoEvent("Failed to start bootloader", ActivityType.UploadingFlash);
                _stallKeepAlive = false;
                workEvent.Result = false;
                return;
            }
            Thread.Sleep(100);
            SendKeepAlive();
            Thread.Sleep(50);

            CastInfoEvent("Erasing FLASH", ActivityType.StartErasingFlash);
            if (SendrequestDownload(6, false, false))
            {
                _needRecovery = true;
                SendShutup();
                CastInfoEvent("Programming FLASH", ActivityType.UploadingFlash);
                bool success = ProgramFlash(bm);

                if (success)
                    CastInfoEvent("FLASH upload completed", ActivityType.ConvertingFile);
                else
                    CastInfoEvent("FLASH upload failed", ActivityType.ConvertingFile);

                sw.Stop();
                _needRecovery = false;

                // what else to do?
                Send0120();
                CastInfoEvent("Session ended", ActivityType.FinishedFlashing);
            }
            else
            {
                sw.Stop();
                _needRecovery = false;
                _stallKeepAlive = false;
                CastInfoEvent("Failed to erase FLASH", ActivityType.ConvertingFile);
                Send0120();
                CastInfoEvent("Session ended", ActivityType.FinishedFlashing);
                workEvent.Result = false;
                return;

            }
            _stallKeepAlive = false;
            workEvent.Result = true;
        }

        private bool SendRestoreT8()
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 3); 
            // 02 1A 79 00 00 00 00 00
            ulong cmd = 0x0000000000791A02;
            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                // Do not return here want to wait for the response
            }
            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(200);
            ulong data = response.getData();
            // 7E8 03 5A 79 01 00 00 00 00
            if (getCanData(data, 0) == 0x03 && getCanData(data, 1) == 0x5A && getCanData(data, 2) == 0x79 && getCanData(data, 3) == 0x01)
            {
                return true;
            }
            return false;
        }


        ///////////////////////////////////////////////////////////////////////////
        /// Flash and read methods specifically for T8 and MCP using Legion bootloader
        ///
        private bool UploadBootloaderLegion()
        {
            int startAddress = 0x102400;
            Bootloader_Leg btloaderdata = new Bootloader_Leg();

            // 238 bytes / unit.
            int Len = 9996 / 238;
            int txpnt = 0;
            byte iFrameNumber = 0x21;
            int saved_progress = 0;
            if (requestDownload(false))
            {
                for (int i = 0; i < Len; i++)
                {
                    iFrameNumber = 0x21;
                    //10 F0 36 00 00 10 24 00
                    //logger.Debug("Sending bootloader: " + startAddress.ToString("X8"));
                    // cast event
                    int percentage = (int)(((float)i * 100) / Len);
                    if (percentage > saved_progress)
                    {
                        CastProgressWriteEvent(percentage);
                        saved_progress = percentage;
                    }

                    if (SendTransferData(0xF0, startAddress, 0x7E8))
                    {
                        canUsbDevice.RequestDeviceReady();
                        // send 0x22 (34) frames with data from bootloader
                        CANMessage msg = new CANMessage(0x7E0, 0, 8);
                        for (int j = 0; j < 0x22; j++)
                        {
                            var cmd = BitTools.GetFrameBytes(iFrameNumber, btloaderdata.BootloaderLegionBytes, txpnt);
                            msg.setData(cmd);
                            txpnt += 7;
                            iFrameNumber++;

                            if (iFrameNumber > 0x2F) iFrameNumber = 0x20;
                            msg.elmExpectedResponses = j == 0x21 ? 1 : 0;//on last command (iFrameNumber 22 expect 1 message)
                            if (j == 0x21)
                                m_canListener.ClearQueue();

                            if (!canUsbDevice.sendMessage(msg))
                            {
                                logger.Debug("Couldn't send message");
                            }
                            Application.DoEvents();
                            if (m_sleepTime > 0)
                                Thread.Sleep(m_sleepTime);

                        }
                        var data = m_canListener.waitMessage(timeoutP2ct, 0x7E8).getData();
                        if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x76)
                        {
                            return false;
                        }
                        canUsbDevice.RequestDeviceReady();
                        SendKeepAlive();
                        startAddress += 0xEA;

                    }
                    else
                    {
                        logger.Debug("Did not receive correct response from SendTransferData");
                    }
                }

                iFrameNumber = 0x21;
                if (SendTransferData(0x0A, startAddress, 0x7E8))
                {
                    // send 0x22 (34) frames with data from bootloader
                    CANMessage msg = new CANMessage(0x7E0, 0, 8);
                    var cmd = BitTools.GetFrameBytes(iFrameNumber, btloaderdata.BootloaderLegionBytes, txpnt);
                    msg.setData(cmd);
                    txpnt += 7;
                    iFrameNumber++;
                    if (!canUsbDevice.sendMessage(msg))
                    {
                        logger.Debug("Couldn't send message");
                    }
                    if (m_sleepTime > 0)
                        Thread.Sleep(m_sleepTime);

                    // now wait for 01 76 00 00 00 00 00 00 
                    CANMessage response = m_canListener.waitMessage(timeoutP2ct, 0x7E8);
                    ulong data = response.getData();
                    if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x76)
                    {
                        return false;
                    }
                    SendKeepAlive();
                    startAddress += 0x06;
                }
                else
                {
                    logger.Debug("Did not receive correct response from SendTransferData");
                }

                CastProgressWriteEvent(100);
            }
            return true;
        }
        

        // Send a magic message to check if the loader is alive
        private bool LegionPing()
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 8);
            ulong cmd = 0x663300000000BEEF;

            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {

                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }

            CANMessage response = new CANMessage();
            response = new CANMessage();
            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();

            if (getCanData(data, 0) == 0xDE && getCanData(data, 1) == 0xAD && getCanData(data, 2) == 0xF0 && getCanData(data, 3) == 0x0F)
                return true;

            return false;
        }
        
        private bool StartCommon(byte Device, bool z22se)
        {
            bool LegionIsAlive = false;

            // This command will sometimes fail even though the loader is alive; Ugly workaround.
            for (int i = 0; i < 4; i++)
            {
                if (LegionPing())
                {
                    LegionIsAlive = true;
                    break;
                }
                Thread.Sleep(40);
            }
            // Don't bother with this if the loader is already up and running 
            if (!LegionIsAlive)
            {
                SendKeepAlive();
                sw.Reset();
                sw.Start();
                CastInfoEvent("Starting session", ActivityType.UploadingBootloader);
                StartSession10();
                CastInfoEvent("Telling ECU to clear CANbus", ActivityType.UploadingBootloader);
                SendShutup();
                SendA2();
                SendA5();
                SendA503();
                Thread.Sleep(50);
                SendKeepAlive();
                // verified upto here


                _securityLevel = AccessLevel.AccessLevel01;
                //CastInfoEvent("Requesting security access", ActivityType.UploadingBootloader);
                if (!RequestSecurityAccess(0))
                {   // was 2000 milli-seconds
                    CastInfoEvent("Failed to get security access", ActivityType.UploadingFlash);
                    return false;
                }

                Thread.Sleep(50);

                if (z22se)
                {
                    CastInfoEvent("Uploading preloader", ActivityType.UploadingBootloader);
                    if (!UploadZ22sePreloader())
                    {   // was 2000 milli-seconds
                        CastInfoEvent("Failed to upload preloader", ActivityType.UploadingFlash);
                        return false;
                    }
                    else
                    {
                        Thread.Sleep(500);
                        // Investigate; The firmware i tried did not respond to this nor tester present when in programming mode. (It's actually the preloader that sends the response.)
                        // Problem is that if one fw version actually decides to send a response there will be one message too much in the queue that could mess with, in particular, the md5 function.
                        CastInfoEvent("Starting preloader", ActivityType.UploadingBootloader);
                        if (!StartBootloader(0xFF2000))
                        {
                            CastInfoEvent("Failed to start preloader", ActivityType.UploadingFlash);
                            return false;
                        }
                        else
                        {
                            // Flush buffer
                            Thread.Sleep(500);
                            m_canListener.FlushQueue();
                        }
                    }
                }

                CastInfoEvent("Uploading bootloader", ActivityType.UploadingBootloader);
                if (!UploadBootloaderLegion())
                {
                    CastInfoEvent("Failed to upload bootloader", ActivityType.UploadingFlash);
                    return false;
                }

                CastInfoEvent("Starting bootloader", ActivityType.UploadingBootloader);
                // start bootloader in ECU
                SendKeepAlive();
                Thread.Sleep(50);

                if (!StartBootloader(0x102400))
                {
                    CastInfoEvent("Failed to start bootloader", ActivityType.UploadingFlash);
                    return false;
                }
            }
            else
                CastInfoEvent("Loader was left running. Starting over", ActivityType.UploadingBootloader);

            Thread.Sleep(500);


            bool success;

            if (LegionOptions.Faster)
            {
                CastInfoEvent("The \"Faster\" option might crash other devices", ActivityType.UploadingBootloader);
            }

            if (LegionOptions.InterframeDelay != 1200)
            {
                CastInfoEvent(("Setting inter-frame delays to: " + LegionOptions.InterframeDelay.ToString("D") + " micrsoec"), ActivityType.UploadingBootloader);
            }

            LegionIDemand(0, LegionOptions.InterframeDelay, out success);

            if (!success)
            {
                return false;
            }

 /*         CastInfoEvent("Reading battery voltage..", ActivityType.UploadingBootloader);
            byte[] pin = LegionIDemand(6, 11, out success);
            float Val1 = 11;
            float Val2 = 11;

            if (success)
            {
                Val1 = ((pin[0] << 8 | pin[1]) & 0x3FF) / (float)72.00;
                pin = LegionIDemand(6, 13, out success);
            }

            if (success)
            {
                Val2 = ((pin[0] << 8 | pin[1]) & 0x3FF) / (float)72.00;
                Val1 = Val1 > Val2 ? Val1 : Val2; // Only care about the highest reading

                CastInfoEvent(("Battery: " + Val1.ToString("F") + " V"), ActivityType.UploadingBootloader);
                if (Val1 < 11.0)
                {
                    DialogResult result = DialogResult.No;

                    result = MessageBox.Show("Your battery voltage is rather low.\nAre you sure you want to continue?",
                        "You have been warned", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

                    if (result == DialogResult.No)
                    {
                        CastInfoEvent("Aborting", ActivityType.UploadingBootloader);
                        LegionRequestexit();
                        return false;
                    }
                }
            }
            else
            {
                CastInfoEvent("Coult not read battery voltage", ActivityType.UploadingBootloader);
            }*/

            // Start the secondary bootloader
            if (Device == EcuByte_MCP)
            {
                CastInfoEvent("Starting secondary bootloader..", ActivityType.UploadingBootloader);
                LegionIDemand(4, 0, out success);
                return success;
            }
            return true;
       }

        // Partition bitmask
        private uint formatmask;

        public void ReadFlashLegMCP(object sender, DoWorkEventArgs workEvent)
        {
            ReadFlashLegion(EcuByte_MCP, 0x40100, false, sender, workEvent);
        }

        // This definitely needs some tweaking for ELM...
        // <returns>[0] = Number of frames before another wait, [1] delay in ms</returns>
        private uint[] flowControlSend(uint ID, out bool success)
        {
            success = false;
            while (true)
            {
                CANMessage response = new CANMessage();
                response = m_canListener.waitMessage(500);
                ulong data = response.getData();
                // CastInfoEvent("Got: " + data.ToString("X"), ActivityType.ConvertingFile);
                // 3x BS STMIN
                // BS: Block size, how many frames could we send before we have to wait for another 0x30. 0 means "no limit"
                // Delay
                uint firstRec = getCanData(data, 0);
                if ((firstRec & 0xF0) != 0x30)
                {
                    CastInfoEvent("sendRequest: Got no ACK", ActivityType.ConvertingFile);
                    return null;
                }
                else if ((firstRec & 0x0F) > 1)
                {
                    CastInfoEvent("sendRequest: Target had a buffer overflow or other problems", ActivityType.ConvertingFile);
                    return null;
                }
                else if ((firstRec & 0x0F) == 1)
                {
                    m_canListener.setupWaitMessage(ID);
                    CastInfoEvent("sendRequest: Target requested a delay", ActivityType.ConvertingFile);
                }
                else
                {
                    uint framesLeft = getCanData(data, 1);
                    uint frameDelay = getCanData(data, 2);
                    success = true;

                    if (framesLeft == 0)
                    {
                        framesLeft = 0xFFFF;
                    }

                    // 0 - 7f: delay in ms
                    // f1 - f9: delay in us (100 - 900 us)
                    if (frameDelay > 0x7f)
                    {
                        frameDelay = 1;
                    }
                    return new uint[] { framesLeft, frameDelay };
                }
            }
        }

        // This'll abstract away the need for you to think about single/multi-frames. Just store the request as is and it'll handle the rest
        // TODO: ELM327..
        private byte[] TransferUSDT(byte[] buf)
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 8);
            CANMessage response = new CANMessage();
            byte[] failRet = new byte[4] { 0, 0, 0, 0 };
            ulong data = 0, cmd = 0;
            uint bytesLeft = buf[0];
            uint stepper = 0x21;
            byte bufPntr = 0;

            if (bytesLeft == 0)
            {
                CastInfoEvent("TransferUSDT: Check lengths!", ActivityType.ConvertingFile);
                return failRet;
            }

            m_canListener.ClearQueue();

            // Convert to total length instead of payload length
            bytesLeft++;

            // Single-frame
            if (bytesLeft < 9)
            {
                for (byte i = 0; i < bytesLeft; i++)
                {
                    cmd |= (ulong)buf[bufPntr++] << (i * 8);
                }

                msg.setData(cmd);
                m_canListener.setupWaitMessage(0x7E8);
                if (!canUsbDevice.sendMessage(msg))
                {
                    CastInfoEvent("TransferUSDT: Couldn't send message", ActivityType.ConvertingFile);
                    return failRet;
                }

                // Only / first frame could take some time if it's performing a write etc (and I believe 500 is max what ELM327 handles?)
                response = m_canListener.waitMessage(500);
                data = response.getData();
            }

            // Multi-frame (beware; it's messy...)
            else
            {
                cmd = 0x0000000000000010;
                for (byte i = 1; i < 8; i++)
                {
                    cmd |= (ulong)buf[bufPntr++] << (i * 8);
                }
                bytesLeft-=7;

                msg.setData(cmd);
                // msg.elmExpectedResponses = 1;
                m_canListener.setupWaitMessage(0x7E8);

                if (!canUsbDevice.sendMessage(msg))
                {
                    CastInfoEvent("TransferUSDT: Couldn't send message", ActivityType.ConvertingFile);
                    return failRet;
                }
                
                // I've only been able to verify the functionality of delays.
                bool success;
                uint[] flowParameters = flowControlSend(0x7e8, out success);
                if (!success)
                {
                    return failRet;
                }

                uint framesLeft = flowParameters[0];
                int frameDelay = (int)flowParameters[1];

                canUsbDevice.RequestDeviceReady();

                while (bytesLeft > 0)
                {
                    cmd = stepper++;
                    stepper &= 0x2f;
                    uint toCopy = (bytesLeft > 7) ? 7 : bytesLeft;
                    bytesLeft -= toCopy;

                    for (byte i = 0; i < toCopy; i++)
                    {
                        cmd |= (ulong)buf[bufPntr++] << ((i + 1) * 8);
                    }

                    msg = new CANMessage(0x7E0, 0, (byte)(toCopy + 1));
                    msg.setData(cmd);

                    msg.elmExpectedResponses = (bytesLeft == 0) ? 1 : 0;

                    if (bytesLeft == 0)
                    {
                        m_canListener.ClearQueue();
                        m_canListener.setupWaitMessage(0x7E8);
                    }

                    if (!canUsbDevice.sendMessage(msg))
                    {
                        CastInfoEvent("TransferUSDT: Couldn't send message", ActivityType.ConvertingFile);
                        return failRet;
                    }

                    // Dang.. we have to wait for another GoAhead
                    if (framesLeft == 0 && bytesLeft > 0)
                    {
                        flowParameters = flowControlSend(0x7e8, out success);
                        if (!success)
                        {
                            return failRet;
                        }

                        framesLeft = flowParameters[0];
                        frameDelay = (int)flowParameters[1];
                    }
                    else if (bytesLeft > 0 && frameDelay > 0)
                    {
                        Thread.Sleep(frameDelay);
                    }
                }

                // Application.DoEvents();
                // Only / first frame could take some time if it's performing a write etc (and I believe 500 is max what ELM327 handles?)
                response = m_canListener.waitMessage(500);
                data = response.getData();
            }

            // Time to receive
            if (data != 0)
            {
                bytesLeft = getCanData(data, 0);
                uint datOffs = 0;
                uint maxRec = 8;

                // 10 xx req .. .. ..
                // Can't catch malformed response: 10 req .. ..
                // The length is actually encoded as 12 bits. Four lower bits of the first byte and all eight of the second one..
                if ((bytesLeft&0xF0) == 0x10)
                {
                    bytesLeft = ((uint)getCanData(data, 0) << 8 | getCanData(data, 1))&0xFFF;
                    if (bytesLeft > 255)
                    {
                        CastInfoEvent("TransferUSDT: Target tried to send more than 255 B in one go", ActivityType.ConvertingFile);
                        return failRet;
                    }
                    datOffs++;
                    maxRec--;
                }

                if (bytesLeft == 0)
                {
                    CastInfoEvent("TransferUSDT: Got reply of zero length", ActivityType.ConvertingFile);
                    return failRet;
                }

                // Pre-increment bytes left since we care about total length, not payload length
                byte[] retBuf = new byte[++bytesLeft];

                // No ECU should ever do this but let's aim for belts and braces!
                // 10 (06 or less) xx xx xx xx xx xx
                // Single-frame response
                if (bytesLeft <= maxRec)
                {
                    for (uint i = 0; i < bytesLeft; i++)
                    {
                        retBuf[i] = getCanData(data, i + datOffs);
                    }
                    return retBuf;
                }

                // Multi-frame response
                else
                {
                    bufPntr = 0;

                    for (uint i = datOffs; i < 8; i++)
                    {
                        retBuf[bufPntr++] = getCanData(data, i);
                        bytesLeft--;
                    }

                    stepper = 0x21;
                    m_canListener.setupWaitMessage(0x7E8);

                    if (!(canUsbDevice is CANELM327Device))
                    {
                        msg.setData(0x30);
                        if (!canUsbDevice.sendMessage(msg))
                        {
                            CastInfoEvent("TransferUSDT: Couldn't send message", ActivityType.ConvertingFile);
                            return failRet;
                        }
                    }

                    while (bytesLeft > 0)
                    {
                        response = m_canListener.waitMessage(timeoutP2ct);
                        data = response.getData();

                        if (getCanData(data, 0) != stepper++)
                        {
                            Thread.Sleep(500);
                            m_canListener.FlushQueue();
                            return failRet;
                        }
                        stepper &= 0x2f;

                        uint toCopy = (bytesLeft > 7) ? 7 : bytesLeft;
                        for (byte i = 1; i <= toCopy; i++)
                        {
                            retBuf[bufPntr++] = getCanData(data, i);
                            bytesLeft--;
                        }
                    }
                    return retBuf;
                }
            }

            CastInfoEvent("TransferUSDT: Got no reply", ActivityType.ConvertingFile);
            return failRet;
        }

        /// <summary>
        /// Response embedds a checksum-16
        /// </summary>
        /// <param name="address"></param>
        /// <param name="length"></param>
        /// <param name="status"></param>
        /// <returns></returns>
        private byte[] readMemoryByAddress32_Leg(uint address, uint length, out bool status)
        {
            byte[] req = new byte[] { 0x07, 0x23, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            status = false;

            req[2] = (byte)(address >> 24);
            req[3] = (byte)(address >> 16);
            req[4] = (byte)(address >> 8);
            req[5] = (byte)(address);
            req[6] = (byte)(length >> 8);
            req[7] = (byte)(length);

            byte[] resp = TransferUSDT(req);
            if (resp[0] != length + 7)
            {
                // Catch ff-skip
                // 05 63 FF FF FF FF
                if (resp[0] == 5)
                {
                    uint retAddr = (uint)(resp[2] << 24 | resp[3] << 16 | resp[4] << 8 | resp[5]);
                    if (resp[1] == 0x63 && retAddr == 0xFFFFFFFF)
                    {
                        byte[] data = new byte[length];
                        for (uint i = 0; i < length; i++)
                        {
                            data[i] = 0xFF;
                        }
                        status = true;
                        return data;
                    }
                }
            }
            else if (resp[1] != 0x63) { }
            else
            {
                // xx 63 AA AA AA AA
                uint retAddr = (uint)(resp[2] << 24 | resp[3] << 16 | resp[4] << 8 | resp[5]);
                uint checksum = (uint)(resp[length + 6] << 8 | resp[length + 7]);

                if (retAddr != address)
                {
                    return null;
                }

                uint tmpsum = 0;
                for (uint i = 0; i < length; i++)
                {
                    tmpsum += resp[6 + i];
                }
                tmpsum &= 0xFFFF;

                if (tmpsum == checksum)
                {
                    byte[] data = new byte[length];
                    for (uint i = 0; i < length; i++)
                    {
                        data[i] = resp[i + 6];
                    }
                    status = true;
                    return data;
                }
            }

            return null;
        }

        /// <summary>
        /// Bone stock request for ECU's requiring 32-bit address
        /// </summary>
        /// <param name="address"></param>
        /// <param name="length"></param>
        /// <param name="status"></param>
        /// <returns></returns>
        private byte[] readMemoryByAddress32(uint address, uint length, out bool status)
        {
            byte[] req = new byte[] { 0x07, 0x23, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            status = false;

            req[2] = (byte)(address >> 24);
            req[3] = (byte)(address >> 16);
            req[4] = (byte)(address >> 8);
            req[5] = (byte)(address);
            req[6] = (byte)(length >> 8);
            req[7] = (byte)(length);

            byte[] resp = TransferUSDT(req);

            if (resp[0] != length + 5) { }
            else if (resp[1] != 0x63) { }
            else
            {
                // xx 63 AA AA AA AA
                if ((uint)(resp[2] << 24 | resp[3] << 16 | resp[4] << 8 | resp[5]) == address)
                {
                    byte[] data = new byte[length];
                    for (uint i = 0; i < length; i++)
                    {
                        data[i] = resp[i + 6];
                    }
                    status = true;
                    return data;
                }
            }

            return null;
        }

        /// <summary>
        /// Bone stock request for ECUs requiring 32-bit address
        /// </summary>
        /// <param name="data"></param>
        /// <param name="execute"></param>
        /// <param name="address"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        private bool transferData32(byte[] data, bool execute, uint address, uint length)
        {
            byte[] req = new byte[length + 7];

            req[0] = (byte)(length + 6);
            req[1] = 0x36;
            req[2] = (byte)(execute ? 0x80 : 0x00);
            req[3] = (byte)(address >> 24);
            req[4] = (byte)(address >> 16);
            req[5] = (byte)(address >> 8);
            req[6] = (byte)(address);

            for (uint i = 0; i < length; i++)
            {
                req[7 + i] = data[i];
            }

            byte[] resp = TransferUSDT(req);

            // Expect at least 01 76
            // Could be longer depending on implementation
            if (resp[0] < 1)
            {
                // It's wise to let the bootloader rest
                if (execute)
                {
                    Thread.Sleep(100);
                }

                // No response is given to "execute" on some ECUs
                return execute;
            }
            else if (resp[1] != 0x76)
            {
                // Throw actual fault?
                return false;
            }

            return true;
        }

        // Very.. custom request for the mpc5566 loader framework   
        private bool transferLzData32(byte[] data, uint address, uint length)
        {
            byte[] req = new byte[256];
            uint tries = 5;
        retryLz:
            int lzLen = data.Length;
            int toCopy = lzLen > 0xF3 ? 0xF3 : lzLen; // 0xF3
            byte lzStep = 0;
            uint lzPtr = 0;

            req[0] = (byte)(toCopy + 12);
            req[1] = 0x37;
            req[2] = lzStep++;
            req[3] = (byte)(address >> 24);
            req[4] = (byte)(address >> 16);
            req[5] = (byte)(address >> 8);
            req[6] = (byte)(address);
            req[7] = (byte)(length >> 24);
            req[8] = (byte)(length >> 16);
            req[9] = (byte)(length >> 8);
            req[10] = (byte)(length);

            for (uint i = 0; i < toCopy; i++)
                req[i + 11] = data[lzPtr++];

            uint checksum = 0;
            for (uint i = 0; i < toCopy + 8; i++)
                checksum += req[i + 3];

            // Append checksum
            req[toCopy + 11] = (byte)(checksum >> 8);
            req[toCopy + 12] = (byte)(checksum);

            while (lzLen > 0)
            {
                lzLen -= toCopy;

                byte[] resp = TransferUSDT(req);
                // Expect at least 01 77
                if (resp[0] < 1 || resp[1] != 0x77)
                {
                    Thread.Sleep(500);
                    if (tries-- > 0)
                    {
                        CastInfoEvent("Lz retry", ActivityType.ConvertingFile);
                        goto retryLz;
                    }
                    return false;
                }

                // Programming error, abort asap
                // xx 7f 37 85
                else if (resp[0] > 2 && resp[1] == 0x7F && resp[2] == 0x37 && resp[3] == 0x85)
                {
                    return false;
                }

                toCopy = lzLen > 0xFB ? 0xFB : lzLen; // 0xFB
                req[0] = (byte)(toCopy + 4);
                req[2] = lzStep++;

                // 0 indicates new frame so keep stepping within 1 - FF
                if (lzStep == 0)
                {
                    lzStep = 1;
                }

                for (uint i = 0; i < toCopy; i++)
                    req[i + 3] = data[lzPtr++];

                // Append checksum
                checksum = 0;
                for (uint i = 0; i < toCopy + 1; i++)
                    checksum += req[i + 2];
                req[toCopy + 3] = (byte)(checksum >> 8);
                req[toCopy + 4] = (byte)(checksum);                
            }

            return true;
        }

        // Service md5    (0 : start : length)
        // Service format (1 : mask  : ~mask)
        private bool startRoutineById_leg(uint service, uint param1, uint param2)
        {
            byte[] req = new byte[11];
            uint tries = 5;

            req[0] = 10;
            req[1] = 0x31;
            req[2] = (byte)service;
            req[3] = (byte)(param1 >> 24);
            req[4] = (byte)(param1 >> 16);
            req[5] = (byte)(param1 >> 8);
            req[6] = (byte)(param1);
            req[7] = (byte)(param2 >> 24);
            req[8] = (byte)(param2 >> 16);
            req[9] = (byte)(param2 >> 8);
            req[10] = (byte)(param2);

            while (--tries > 0)
            {
                byte[] resp = TransferUSDT(req);

                if (resp[0] != 2) { }
                else if (resp[1] != 0x71) { }
                else if (resp[2] != service) { }
                else
                {
                    return true;
                }

                Thread.Sleep(500);
            }

            return false;
        }

        private byte[] requestRoutineResult_leg(byte service, out bool success)
        {
            success = false;
            uint tries = 5;

            while (--tries > 0)
            {
                byte[] resp = TransferUSDT(new byte[] { 0x02, 0x33, service });

                if (resp[0] < 2) { }
                else if (resp[1] != 0x73 && resp[0] == 3)
                {
                    // Busy, come again
                    if (resp[1] == 0x7f && resp[2] == 0x33 && resp[3] == 0x21)
                    {
                        tries = 5;
                    }
                }
                else if (resp[1] != 0x73) { }
                else if (resp[2] != service) { }
                else
                {
                    success = true;

                    // xx 73 ser
                    if (resp[0] > 2)
                    {
                        byte dataLen = (byte)(resp[0] - 2);
                        byte[] data = new byte[dataLen + 1];
                        data[0] = dataLen;

                        for (uint i = 0; i < dataLen; i++)
                        {
                            data[1 + i] = resp[3 + i];
                        }
                        return data;

                    }

                    return null;
                }

                Thread.Sleep(25);
            }

            return null;
        }

        private enum mpc5566Mode : uint
        {
            modeBAM = 0, // Defunct. It must be compiled in this mode
            modeE39 = 1,
            modeE78 = 2
        };

        private bool mpc5566SecAcc(mpc5566Mode mode, uint level)
        {
            CANMessage msg = new CANMessage(0x7e0, 0, 8);
            CANMessage response = new CANMessage();

            CastInfoEvent("Attempting security access", ActivityType.ConvertingFile);

            msg.setData(level << 16 | 0x2702);
            m_canListener.setupWaitMessage(0x7e8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Security access failed", ActivityType.ConvertingFile);
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }

            response = m_canListener.waitMessage(500);
            ulong data = response.getData();

            if (getCanData(data, 1) == 0x7f && getCanData(data, 2) == 0x27)
            {
                CastInfoEvent("Security access not granted", ActivityType.ConvertingFile);
                CastInfoEvent("Reason: " + TranslateErrorCode(response.getCanData(3)), ActivityType.ConvertingFile);
                return false;
            }
            else if (getCanData(data, 1) != 0x67)
            {
                CastInfoEvent("Security access failed due to no or incorrect response from ECU", ActivityType.ConvertingFile);
                return false;
            }
            else if (getCanData(data, 3) == 0 && 
                     getCanData(data, 4) == 0)
            {
                CastInfoEvent("Security access has already been granted", ActivityType.ConvertingFile);
                return true;
            }

            SeedToKey s2k = new SeedToKey();
            ulong key = 0;

            if (mode == mpc5566Mode.modeE39)
            {
                key = s2k.calculateKeyForE39((ushort)(getCanData(data, 3) << 8 | getCanData(data, 4)));
            }
            else if (mode == mpc5566Mode.modeE78)
            {
                key = s2k.calculateKeyForE78((ushort)(getCanData(data, 3) << 8 | getCanData(data, 4)));
            }
            else
            {
                // Unknown ECU!
                return false;
            }

            key = (key & 0xFF) << 8 | ((key >> 8) & 0xFF);

            msg = new CANMessage(0x7e0, 0, 8);
            response = new CANMessage();

            msg.setData(0x2704 | key << 24 | (level + 1) << 16);
            m_canListener.setupWaitMessage(0x7e8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Security access failed: Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }

            response = m_canListener.waitMessage(500);
            data = response.getData();

            if (getCanData(data, 1) == 0x67)
            {
                CastInfoEvent("Security access granted", ActivityType.ConvertingFile);
                return true;
            }
            else if (getCanData(data, 1) == 0x7f && getCanData(data, 2) == 0x27)
            {
                CastInfoEvent("Security access not granted", ActivityType.ConvertingFile);
                CastInfoEvent("Reason: " + TranslateErrorCode(response.getCanData(3)), ActivityType.ConvertingFile);
            }
            else
            {
                CastInfoEvent("Security access failed due to no or incorrect response from ECU", ActivityType.ConvertingFile);
            }

            return false;
        }

        private bool UploadMPC5566Loader(uint address, mpc5566Mode mode)
        {
            CANMessage msg = new CANMessage(0x7E0, 0, 8);
            CANMessage response = new CANMessage();
            uint m_mode = (uint)mode;
            int saved_progress = 0;
            uint blockSize = 0xF0;
            byte[] buf = new byte[blockSize];
            uint execAddr = address;
            uint bufPntr = 0;
            uint tries;
            bool status;
            Stopwatch stopWatch = new Stopwatch();
            int msElapsed = 0;
            TimeSpan tSpent;

            CastInfoEvent("Uploading bootloader..", ActivityType.ConvertingFile);
            CastProgressReadEvent(0);

            ulong cmd = 0x0000000000003405;
            uint size = 0;

            for (int i = 0; i < 4; i++)
            {
                cmd |= ((((ulong)size >> ((3-i)*8))&0xff) << (i*8)) << 24;
            }

            msg.setData(cmd);
            m_canListener.setupWaitMessage(0x7E8);
            if (!canUsbDevice.sendMessage(msg))
            {
                CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                return false;
            }

            response = m_canListener.waitMessage(timeoutP2ct);
            ulong data = response.getData();

            // I hear ya! Wait..
            // Elm needs a workaround..
            if (getCanData(data, 1) == 0x7F && getCanData(data, 2) == 0x34 && getCanData(data, 3) == 0x78)
            {
                m_canListener.setupWaitMessage(0x7E8);
                response = new CANMessage();
                response = m_canListener.waitMessage(timeoutP2ct);
                data = response.getData();
            }

            if (getCanData(data, 1) != 0x74)
            {
                CastInfoEvent("Incorrect response to request download", ActivityType.ConvertingFile);
                return false;
            }

            SendKeepAlive();
            CastInfoEvent("Transfer accepted", ActivityType.ConvertingFile);
            Thread.Sleep(50);

            Bootloader_mpc5566 m_bootloader = new TrionicCANLib.Bootloader_mpc5566();
            uint bytesLeft = (uint)m_bootloader.Bootloader_mpc5566Bytes.Length;
            uint totalBytes = bytesLeft;
            // Copy bootloader to local buffer so that we can modify the mode-word
            byte[] bootloaderBytes = new byte[bytesLeft];
            for (uint i = 0; i < bytesLeft; i++)
            {
                bootloaderBytes[i] = m_bootloader.Bootloader_mpc5566Bytes[i];
            }

            bootloaderBytes[4] = (byte)(m_mode >> 24);
            bootloaderBytes[5] = (byte)(m_mode >> 16);
            bootloaderBytes[6] = (byte)(m_mode >>  8);
            bootloaderBytes[7] = (byte)(m_mode);

            stopWatch.Start();

            while (bytesLeft > 0)
            {
                if (blockSize > bytesLeft)
                {
                    blockSize = bytesLeft;
                }

                tSpent = stopWatch.Elapsed;
                msElapsed = tSpent.Milliseconds;
                msElapsed += tSpent.Seconds * 1000;
                msElapsed += tSpent.Minutes * 60000;

                if (msElapsed >= 1000)
                {
                    stopWatch.Restart();
                    BroadcastKeepAlive101();
                }

                for (uint i = 0; i < blockSize; i++)
                {
                    buf[i] = bootloaderBytes[bufPntr++];
                }

                status = false;
                tries = 5;

                while (!status && --tries > 0)
                {
                    status = transferData32(buf, false, address, blockSize);
                }

                if (!status)
                {
                    CastInfoEvent("Bootloader upload failed", ActivityType.ConvertingFile);
                    stopWatch.Stop();
                    return false;
                }

                int percentage = (int)(((float)bufPntr * 100) / (float)totalBytes);
                if (percentage > saved_progress)
                {
                    CastProgressReadEvent(percentage);
                    saved_progress = percentage;
                }

                bytesLeft -= blockSize;
                address += blockSize;
            }

            status = transferData32(null, true, execAddr, 0);
            if (!status)
            {
                CastInfoEvent("ECU refused to start bootloader?", ActivityType.ConvertingFile);
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

                byte[] retdata = readMemoryByAddress32_Leg(address, blockSize, out success);

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
            /*
            CastInfoEvent("Verifying md5..", ActivityType.DownloadingFlash);

            System.Security.Cryptography.MD5CryptoServiceProvider md5 = new System.Security.Cryptography.MD5CryptoServiceProvider();
            md5.Initialize();

            byte[] bLoc = md5.ComputeHash(buf);
            ulong[] mLoc = { 0, 0 };
            ulong[] mRem = mpc5566md5(start, length);

            for (int i = 0; i < 8; i++)
            {
                mLoc[0] |= (ulong)bLoc[  i  ] << ((7 - i) * 8);
                mLoc[1] |= (ulong)bLoc[8 + i] << ((7 - i) * 8);
            }

            CastInfoEvent("Remote hash: " + mRem[0].ToString("X16") + mRem[1].ToString("X16"), ActivityType.ConvertingFile);
            CastInfoEvent("Local hash : " + mLoc[0].ToString("X16") + mLoc[1].ToString("X16"), ActivityType.ConvertingFile);

            if (mRem[0] == mLoc[0] && mRem[1] == mLoc[1])
            {
                status = true;
                return buf;
            }*/
            return null;
        }

        // Current implementation is not listening to returned length so data is repeated if the ECU is lazy or trying to save space.
        // It'll also freeze the flasher if the response is broken in the wrong way
        private byte[] newReadDataById_RAW(byte id)
        {
            byte[] ret = TransferUSDT(new byte[] { 0x02, 0x1A, id });

            // nn 5A id .. ..
            if (ret[0] < 3) { }
            else if (ret[1] != 0x5A || ret[2] != id) { }
            else
            {
                uint respLen = (uint)(ret[0] - 2);
                byte[] retData = new byte[respLen];

                for (uint i = 0; i < respLen; i++)
                {
                    retData[i] = ret[i + 3];
                }
                return retData;
            }
            return null;
        }

        private string newReadDataById(byte id)
        {
            byte[] ret = newReadDataById_RAW(id);
            return ret != null ? Encoding.UTF8.GetString(ret, 0, ret.Length) :  "";
        }

        private bool newWriteDataById(byte id, byte[] data, int length)
        {
            byte[] req = new byte[length + 3];
            req[0] = (byte)(length + 2);
            req[1] = 0x3B;
            req[2] = id;

            for (uint i = 0; i < length; i++)
            {
                req[i + 3] = data[i];
            }

            byte[] ret = TransferUSDT(req);

            // nn 7B id ..
            if (ret[0] < 2) { }
            else if (ret[1] != 0x7B || ret[2] != id) { }
            else
            {
                return true;
            }
            return false;
        }

        private bool newWriteDataById(byte id, byte[] data)
        {
            return newWriteDataById(id, data, data.Length);
        }

        private ulong[] mpc5566md5(uint address, uint length)
        {
            bool success = startRoutineById_leg(0, address, length);
            if (success)
            {
                byte[] data = requestRoutineResult_leg(0, out success);

                if (data != null && success)
                {
                    if (data[0] == 16)
                    {
                        ulong A = 0, B = 0;

                        for (int i = 0; i < 8; i++)
                        {
                            A |= (ulong)data[1 + i] << ((7 - i) * 8);
                            B |= (ulong)data[9 + i] << ((7 - i) * 8);
                        }
                        return new ulong[] { A, B };
                    }
                }
            }
            return new ulong[] { 0, 0 };
        }

        private void mpc5566ActDump(DoWorkEventArgs workEvent, mpc5566Mode mode)
        {
            string filename = (string)workEvent.Argument;
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
                LegionRequestexit();
                return;
            }

            for (uint i = 0; i < 0x300000; i++)
            {
                buf[bufPntr++] = flash[i];
            }

            if (mode == mpc5566Mode.modeE39 ||
                mode == mpc5566Mode.modeE78  )
            {
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
            }
            else
            {
                CastInfoEvent("Shadow could not be dumped due to compatibility problems", ActivityType.FinishedDownloadingFlash);
                CastInfoEvent("That part of the binary will be 0xFF'ed..", ActivityType.FinishedDownloadingFlash);
            }

            workEvent.Result = true;
            LegionRequestexit();

            try
            {
                File.WriteAllBytes(filename, buf);
                Md5Tools.WriteMd5HashFromByteBuffer(filename, buf);
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

        private const uint e39e78LoaderBase = 0x40004000;

        public void ReadE78(object sender, DoWorkEventArgs workEvent)
        {
            // Is this even necessary? C# ain't my strong forte 
            BackgroundWorker bw = sender as BackgroundWorker;

            _stallKeepAlive = true;
            workEvent.Result = false;

            CastInfoEvent("Polling id 90 to determine state", ActivityType.ConvertingFile);
            string ident = newReadDataById(0x90);

            if (ident != "MPC5566-LOADER: TXSUITE.ORG")
            {
                CastInfoEvent("Got: " + ident, ActivityType.ConvertingFile);
                SendKeepAlive();
                sw.Reset();
                sw.Start();
                StartSession10();

                Thread.Sleep(50);

                if (!mpc5566SecAcc(mpc5566Mode.modeE78, 1))
                {
                    return;
                }

                SendKeepAlive();
                SendShutup();
                SendA2();
                SendA5();
                SendA503();

                Thread.Sleep(100);

                SendKeepAlive();
                if (!UploadMPC5566Loader(e39e78LoaderBase, mpc5566Mode.modeE78))
                    return;

                ident = newReadDataById(0x90);
                if (ident != "MPC5566-LOADER: TXSUITE.ORG")
                {
                    CastInfoEvent("Loader did not start as expected (" + ident + ")", ActivityType.ConvertingFile);
                    return;
                }
            }
            else
            {
                CastInfoEvent("Loader left running", ActivityType.ConvertingFile);
            }

            uint delay = LegionOptions.InterframeDelay;
            newWriteDataById(0x91, new byte[] { (byte)(delay >> 8), (byte)delay });

            mpc5566ActDump(workEvent, mpc5566Mode.modeE78);
        }
        
        private byte[] readMemoryByAddress32(uint address, uint length)
        {
            byte[] req = new byte[8];

            req[0] = 7;
            req[1] = 0x23;
            req[2] = (byte)(address >> 24);
            req[3] = (byte)(address >> 16);
            req[4] = (byte)(address >> 8);
            req[5] = (byte)address;
            req[6] = (byte)(length >> 8);
            req[7] = (byte)length;

            byte[] resp = TransferUSDT(req);

            // xx 63 aa aa aa aa .. ..
            if (resp != null && resp.Length == length + 6 &&
                resp[0] == length + 5 &&
                resp[1] == 0x63)
            {
                byte[] actresp = new byte[length];

                for (uint i = 0; i < length; i++)
                {
                    actresp[i] = resp[i + 6];
                }

                return actresp;
            }

            return null;
        }

        private byte[] readMemoryByAddress32block(uint address, uint length, uint blockSize)
        {
            byte[] buf = new byte[length];
            uint firstaddress = address;
            uint lastaddress = address + length;
            uint bufPntr = 0;
            uint defaultBlockSize = blockSize;
            int saved_progress = 0;

            Stopwatch stopWatch = new Stopwatch();
            int msElapsed = 0;
            TimeSpan tSpent;

            for (uint i = 0; i < buf.Length; i++)
            {
                buf[i] = 0xFF;
            }

            stopWatch.Start();

            while (address < lastaddress)
            {
            SRAMRET:

                tSpent = stopWatch.Elapsed;
                msElapsed = tSpent.Milliseconds;
                msElapsed += tSpent.Seconds * 1000;
                msElapsed += tSpent.Minutes * 60000;

                if (msElapsed > 750)
                {
                    SendKeepAlive();
                    stopWatch.Restart();
                }

                byte[] data = readMemoryByAddress32(address, blockSize);

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

        private void readSRAMe39(DoWorkEventArgs workEvent, mpc5566Mode mode)
        {
            string filename = (string)workEvent.Argument;
            byte[] buf = new byte[128 * 1024];
            uint lowAddr/**/= 0x40000000;
            uint middleAddr = 0x40000000 + 0x3000;
            uint bufPntr = 0;
            bool status;

            workEvent.Result = false;

            for (uint i = 0; i < buf.Length; i++)
            {
                buf[i] = 0xFF;
            }

            CastInfoEvent("Downloading sram..", ActivityType.DownloadingFlash);

            CastInfoEvent("Reading secret area..", ActivityType.ConvertingFile);
            byte[] secret = readMemoryByAddress32block(0x40000000, 0x1000, 16);
 /*
            CastInfoEvent("Reading main area..", ActivityType.ConvertingFile);
            byte[] middleblock = readMemoryByAddress32block(0x40000000 + 0x3000, (0x40020000 - 0x40000000) - 0x3000, 16);

            if (!UploadMPC5566Loader(e39e78LoaderBase, mpc5566Mode.modeE39))
                return;

            string ident = ident = newReadDataById(0x90);
            if (ident != "MPC5566-LOADER: TXSUITE.ORG")
            {
                CastInfoEvent("Loader did not start as expected (" + ident + ")", ActivityType.ConvertingFile);
                return;
            }

            uint delay = LegionOptions.InterframeDelay;
            newWriteDataById(0x91, new byte[] { (byte)(delay >> 8), (byte)delay });

            CastInfoEvent("Reading low area..", ActivityType.ConvertingFile);
            byte[] lowBlock  = dumpEx(out status, 0x40000000 + 0x1000, 0x40000000 + 0x3000);
            if (!status) return;
            */
            for (uint i = 0; i < secret.Length; i++)
            {
                buf[bufPntr++] = secret[i];
            }

            /*
            for (uint i = 0; i < lowBlock.Length; i++)
            {
                buf[bufPntr++] = lowBlock[i];
            }
            for (uint i = 0; i < middleblock.Length; i++)
            {
                buf[bufPntr++] = middleblock[i];
            }*/

            try
            {
                File.WriteAllBytes(filename, buf);
                Md5Tools.WriteMd5HashFromByteBuffer(filename, buf);
                CastInfoEvent("Download done", ActivityType.FinishedDownloadingFlash);
                workEvent.Result = true;
            }
            catch (Exception e)
            {
                CastInfoEvent("Could not write file... " + e.Message, ActivityType.ConvertingFile);
                CastInfoEvent("Download failed", ActivityType.FinishedDownloadingFlash);
                
            }
        }



        public void ReadE39(object sender, DoWorkEventArgs workEvent)
        {
            // Is this even necessary? C# ain't my strong forte 
            BackgroundWorker bw = sender as BackgroundWorker;

            _stallKeepAlive = true;
            workEvent.Result = false;

            CastInfoEvent("Polling id 90 to determine state", ActivityType.ConvertingFile);
            string ident = newReadDataById(0x90);

            if (ident != "MPC5566-LOADER: TXSUITE.ORG")
            {
                CastInfoEvent("Got: " + ident, ActivityType.ConvertingFile);
                SendKeepAlive();
                sw.Reset();
                sw.Start();
                StartSession10();

                Thread.Sleep(50);

                if (!mpc5566SecAcc(mpc5566Mode.modeE39, 1))
                {
                    return;
                }

                SendKeepAlive();
                SendShutup();
                SendA2();
                SendA5();
                SendA503();

                Thread.Sleep(100);

                SendKeepAlive();

                readSRAMe39(workEvent, mpc5566Mode.modeE39);
                return;

                if (!UploadMPC5566Loader(e39e78LoaderBase, mpc5566Mode.modeE39))
                    return;

                ident = newReadDataById(0x90);
                if (ident != "MPC5566-LOADER: TXSUITE.ORG")
                {
                    CastInfoEvent("Loader did not start as expected (" + ident + ")", ActivityType.ConvertingFile);
                    return;
                }
            }
            else
            {
                CastInfoEvent("Loader left running", ActivityType.ConvertingFile);
            }

            uint delay = LegionOptions.InterframeDelay;
            newWriteDataById(0x91, new byte[] { (byte)(delay >> 8), (byte)delay });

            mpc5566ActDump(workEvent, mpc5566Mode.modeE39);
        }

        // ulong bamKEY = 0x7BC10CBD55EBB2DA; // E78
        // ulong bamKEY =       0xFEEDFACECAFEBEEF; // Public
        ulong bamKEY    = 0xFD13031FFB1D0521; // 881155AA
        //             0x________________; // Since the editor is ***...
        // ulong bamKEY  = 0xFFFFFFFFFFFFFFFF;
        ulong lazySWAP(ulong data)
        {
            ulong retval = 0;
            for (int i = 0; i < 8; i++)
            {
                retval <<= 8;
                retval |= (data & 0xff);
                data >>= 8;
            }
            return retval;
        }

        // To be moved somewhere else. It's only here while in the thrash branch
        private bool BAMflash(uint address)
        {
            CANMessage msg = new CANMessage(0x11, 0, 8);
            CANMessage response = new CANMessage();
            uint tries = 10;
            ulong cmd = lazySWAP(bamKEY);
            ulong respData = 0;
            do
            {
                msg.setData(cmd);
                m_canListener.setupWaitMessage(1);
                if (!canUsbDevice.sendMessage(msg))
                {
                    CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                    return false;
                }
                
                response = m_canListener.waitMessage(90);
                respData = response.getData();
                // tries--;
                if (tries == 0)
                {
                    CastInfoEvent("Could not start BAM", ActivityType.ConvertingFile);
                    return false;
                }
            } while (respData != lazySWAP(bamKEY));

            // if (respData == lazySWAP(bamKEY))
            {
                CastInfoEvent("Key accepted", ActivityType.ConvertingFile);
                Bootloader_mpc5566 m_bootloader = new TrionicCANLib.Bootloader_mpc5566();
                uint bytesLeft = (uint)m_bootloader.Bootloader_mpc5566Bytes.Length;
                byte[] allignedData = new byte[(bytesLeft&~7) + 8];
                uint i;
                for (i = 0; i < bytesLeft; i++)
                {
                    allignedData[i] = m_bootloader.Bootloader_mpc5566Bytes[i];
                }
                for (; i < allignedData.Length; i++)
                {
                    allignedData[i] = 0;
                }

                bytesLeft = (uint)(allignedData.Length);

                msg = new CANMessage(0x12, 0, 8);
                cmd = lazySWAP((ulong) address << 32 | bytesLeft);
                msg.setData(cmd);

                m_canListener.setupWaitMessage(2);
                if (!canUsbDevice.sendMessage(msg))
                {
                    CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                    // Do not return here want to wait for the response
                }

                response = m_canListener.waitMessage(200);
                respData = response.getData();
                uint bufPntr = 0;

                if (respData == cmd)
                {
                    CastInfoEvent("Address and length accepted", ActivityType.ConvertingFile);

                    msg = new CANMessage(0x13, 0, 8);
                    while (bytesLeft > 0)
                    {
                        cmd = 0;
                        for (int e = 0; e < 8; e++)
                        {
                            cmd |= (ulong)allignedData[bufPntr++] << (e * 8);
                        }

                        cmd = lazySWAP(cmd);
                        msg.setData(lazySWAP(cmd));

                        m_canListener.setupWaitMessage(3);
                        if (!canUsbDevice.sendMessage(msg))
                        {
                            CastInfoEvent("Couldn't send message", ActivityType.ConvertingFile);
                            // Do not return here want to wait for the response
                        }

                        response = m_canListener.waitMessage(200);
                        respData = response.getData();

                        if (respData != lazySWAP(cmd))
                        {
                            CastInfoEvent("Did not receive the same data: " + respData.ToString("X16"), ActivityType.ConvertingFile);
                            return false;
                        }

                        bytesLeft -= 8;
                    }

                    return true;
                }

            }
            return false;
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

            bool success = startRoutineById_leg(0, physStart, physEnd - physStart);

            if (!success || part[1] == 0)
            {
                CastInfoEvent("Could not start md5 service", ActivityType.ConvertingFile);
                return true;
            }

            byte[] data = requestRoutineResult_leg(0, out success);

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

        // lock mask has priority over force mask since it's a safety feature
        private void mpc5566ActFlash(DoWorkEventArgs workEvent, ECU target, uint lockMask, uint forceMask, bool recoverSes)
        {
            TargetParameters tparam = new TargetParameters(target);
            byte[] filebytes = File.ReadAllBytes((string)workEvent.Argument);
            
            // Placeholder to prevent myself from doing stupid sh..
            if (filebytes.Length != 0x300000 + 1024 && filebytes.Length != 0x300000)
            {
                CastInfoEvent("Incorrect file size", ActivityType.ConvertingFile);
                return;
            }

            CastProgressWriteEvent(0);

            // Some paranoia for ya..
            CastInfoEvent("Verifying target compatibility..", ActivityType.ConvertingFile);

            byte[] procBytes = newReadDataById_RAW(0x92);
            if (procBytes == null || procBytes.Length != 4)
            {
                CastInfoEvent("Could not verify. Aborting..", ActivityType.ConvertingFile);

                // Do not exit in case this is a recovered session. User must've picked the wrong target
                if (!recoverSes)
                {
                    LegionRequestexit();
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
                    LegionRequestexit();
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

            /*
            // Only used while figuring out which partitions are used!
            mask = (1 << 28) - 1;
            if (startRoutineById_leg(1, mask, ~mask))
            {
                requestRoutineResult_leg(1, out bool success);
                if (!success)
                {
                    CastInfoEvent("Could not erase partition to be written", ActivityType.ConvertingFile);
                    return;
                }
            }

            mask = 0x7;
            */

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
            if ((mask & 0x10000000) > 0)
            {
                CastInfoEvent("Shadow block is different!", ActivityType.ConvertingFile);
            }

            if (mask == 0)
            {
                CastInfoEvent("No need to flash, identical", ActivityType.ConvertingFile);
                LegionRequestexit();
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
            const uint defChunk = 128 * 1024;
            bool useCompression = true;
            bool restartLz = true;
            int saved_progress = 0;
            // uint inducedFault = 1;
            int partition = 0;
            uint totLoc = 0;

            LZ77 lzComp = new LZ77();

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

                    // Erase partitions one and one;
                    // Reason is I don't fully trust lz yet. No matter what happens, hardware locks prevents corruption of other partitions
                    CastInfoEvent("Erasing: " + physStart.ToString("X6") + " - " + (physEnd - 1).ToString("X6"), ActivityType.ConvertingFile);
                    if (startRoutineById_leg(1, (uint)(1 << partition), ~(uint)(1 << partition)))
                    {
                        requestRoutineResult_leg(1, out bool success);
                        if (!success)
                        {
                            CastInfoEvent("Could not erase partition to be written", ActivityType.ConvertingFile);
                            return;
                        }
                    }
                    else
                    {
                        CastInfoEvent("Could not erase partition to be written", ActivityType.ConvertingFile);
                        return;
                    }

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
                                        if (transferLzData32(compressed, oldstart, oldlen))
                                        {
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
                                    if (!transferLzData32(compressed, oldstart, oldlen)/* || inducedFault-- == 0*/)
                                    {
                                        CastInfoEvent("LZ write failed: Transfer failure", ActivityType.ConvertingFile);
                                        CastInfoEvent("Switching over to regular transfer", ActivityType.ConvertingFile);
                                        useCompression = false;
                                    }
                                    else
                                    {
                                        totLoc += oldlen;
                                        int percentage = (int)(((float)totLoc * 100) / (float)totLen);
                                        if (percentage != saved_progress)
                                        {
                                            CastProgressWriteEvent(percentage);
                                            saved_progress = percentage;
                                        }
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
                                    success = transferData32(flashBuf, false, oldStart, blockSize);
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

            LegionRequestexit();
            CastInfoEvent("Write done", ActivityType.ConvertingFile);
            workEvent.Result = true;
        }

        public void WriteFlashDELCOE78(object sender, DoWorkEventArgs workEvent)
        {
            BackgroundWorker bw = sender as BackgroundWorker;
            uint delay = LegionOptions.InterframeDelay;
            bool recoveredSession = false;
            uint lockedPartitions = 0;
            uint forcedPartitions = 0;

            CastInfoEvent("Currently disabled for your safety", ActivityType.ConvertingFile);
            return;

            // I know how to brick my ECU, believe me!
            // BAMflash(e39e78LoaderBase);
            // Thread.Sleep(1000);

            _stallKeepAlive = true;

            CastInfoEvent("Polling id 90 to determine state", ActivityType.ConvertingFile);
            string ident = newReadDataById(0x90);

            if (ident != "MPC5566-LOADER: TXSUITE.ORG")
            {
                CastInfoEvent("Got: " + ident, ActivityType.ConvertingFile);
                SendKeepAlive();
                sw.Reset();
                sw.Start();
                StartSession10();

                Thread.Sleep(50);

                if (!mpc5566SecAcc(mpc5566Mode.modeE78, 1))
                {
                    return;
                }

                SendKeepAlive();
                SendShutup();
                SendA2();
                SendA5();
                SendA503();

                Thread.Sleep(100);

                SendKeepAlive();
                if (!UploadMPC5566Loader(e39e78LoaderBase, mpc5566Mode.modeE78))
                    return;

                ident = newReadDataById(0x90);
                if (ident != "MPC5566-LOADER: TXSUITE.ORG")
                {
                    CastInfoEvent("Loader did not start as expected", ActivityType.ConvertingFile);
                    return;
                }
            }
            else
            {
                recoveredSession = true;
                CastInfoEvent("Loader left running", ActivityType.ConvertingFile);
            }

            newWriteDataById(0x91, new byte[] { (byte)(delay >> 8), (byte)delay });
            mpc5566ActFlash(workEvent, ECU.DELCOE78, lockedPartitions, forcedPartitions, recoveredSession);
        }
        
        // Remember to erase FF partitions!
        public void WriteFlashDELCOE39(object sender, DoWorkEventArgs workEvent)
        {
            BackgroundWorker bw = sender as BackgroundWorker;
            uint delay = LegionOptions.InterframeDelay;
            bool recoveredSession = false;
            uint lockedPartitions = 0; // Protect boot partitions
            uint forcedPartitions = 0;

            CastInfoEvent("Currently disabled for your safety", ActivityType.ConvertingFile);
            return;

            // I know how to brick my ECU, believe me!
            // BAMflash(e39e78LoaderBase);
            // Thread.Sleep(500);

            _stallKeepAlive = true;

            CastInfoEvent("Polling id 90 to determine state", ActivityType.ConvertingFile);
            string ident = newReadDataById(0x90);

            if (ident != "MPC5566-LOADER: TXSUITE.ORG")
            {
                CastInfoEvent("Got: " + ident, ActivityType.ConvertingFile);
                SendKeepAlive();
                sw.Reset();
                sw.Start();
                StartSession10_WakeUp();

                // Thread.Sleep(250);
                // mpc5566SecAcc(mpc5566Mode.modeE39, 1);
                // Thread.Sleep(250);

                if (!mpc5566SecAcc(mpc5566Mode.modeE39, 1))
                {
                    return;
                }

                SendKeepAlive();
                SendShutup();
                SendA2();
                SendA5();
                SendA503();

                Thread.Sleep(100);

                SendKeepAlive();
                if (!UploadMPC5566Loader(e39e78LoaderBase, mpc5566Mode.modeE39))
                    return;

                ident = newReadDataById(0x90);
                if (ident != "MPC5566-LOADER: TXSUITE.ORG")
                {
                    CastInfoEvent("Loader did not start as expected", ActivityType.ConvertingFile);
                    return;
                }
            }
            else
            {
                recoveredSession = true;
                CastInfoEvent("Loader left running", ActivityType.ConvertingFile);
            }

            newWriteDataById(0x91, new byte[] { (byte)(delay >> 8), (byte)delay });
            mpc5566ActFlash(workEvent, ECU.DELCOE39, lockedPartitions, forcedPartitions, recoveredSession);
        }

        public void ReadFlashLegT8(object sender, DoWorkEventArgs workEvent)
        {
            ReadFlashLegion(EcuByte_T8, 0x100000, false, sender, workEvent);
        }

        public void WriteFlashLegMCP(object sender, DoWorkEventArgs workEvent)
        {
            WriteFlashLegion(EcuByte_MCP, 0x40100, false, sender, workEvent);
        }

        public void WriteFlashLegT8(object sender, DoWorkEventArgs workEvent)
        {
            WriteFlashLegion(EcuByte_T8, 0x100000, false, sender, workEvent);
        }


        // Z22SE stuff. The loader will NOT marry the co-processor since their checksum algorithm is unknown. 
        public void ReadFlashLegZ22SE_Main(object sender, DoWorkEventArgs workEvent)
        {
            ReadFlashLegion(EcuByte_T8, 0x100000, true, sender, workEvent);
        }
        public void ReadFlashLegZ22SE_MCP(object sender, DoWorkEventArgs workEvent)
        {
            ReadFlashLegion(EcuByte_MCP, 0x40100, true, sender, workEvent);
        }

        public void WriteFlashLegZ22SE_Main(object sender, DoWorkEventArgs workEvent)
        {
            // They're not using the first partition as recovery in those softwares that i've looked into. Flash Everything!
            formatBootPartition = true;
            WriteFlashLegion(EcuByte_T8, 0x100000, true, sender, workEvent);
        }
        public void WriteFlashLegZ22SE_MCP(object sender, DoWorkEventArgs workEvent)
        {
            // It seems they use the same recovery on MCP as regular Trionic 8 but do this just to make sure.
            formatBootPartition = true;
            WriteFlashLegion(EcuByte_MCP, 0x40100, true, sender, workEvent);
        }

        private bool UploadZ22sePreloader()
        {
            int startAddress = 0xFF2000;
            Bootloader_z22se btloaderdata = new Bootloader_z22se();

            int txpnt = 0;
            byte iFrameNumber = 0x21;
            int saved_progress = 0;

            if (requestDownload(true))
            {
                // The bin is only 1230 B but I made the tool in such a way that it will pad the file to make things easy to work with.
                for (int i = 0; i < 6; i++)
                {
                    iFrameNumber = 0x21;
                    //10 F0 36 00 00 10 24 00
                    //logger.Debug("Sending bootloader: " + startAddress.ToString("X8"));
                    // cast event
                    int percentage = (int)(((float)i * 100) / 6);
                    if (percentage > saved_progress)
                    {
                        CastProgressWriteEvent(percentage);
                        saved_progress = percentage;
                    }

                    if (SendTransferData(0xF0, startAddress, 0x7E8))
                    {
                        canUsbDevice.RequestDeviceReady();
                        // send 0x22 (34) frames with data from bootloader
                        CANMessage msg = new CANMessage(0x7E0, 0, 8);
                        for (int j = 0; j < 0x22; j++)
                        {
                            var cmd = BitTools.GetFrameBytes(iFrameNumber, btloaderdata.Bootloaderz22seBytes, txpnt);
                            msg.setData(cmd);
                            txpnt += 7;
                            iFrameNumber++;

                            if (iFrameNumber > 0x2F) iFrameNumber = 0x20;
                            msg.elmExpectedResponses = j == 0x21 ? 1 : 0;//on last command (iFrameNumber 22 expect 1 message)
                            if (j == 0x21)
                                m_canListener.ClearQueue();

                            if (!canUsbDevice.sendMessage(msg))
                            {
                                logger.Debug("Couldn't send message");
                            }
                            Application.DoEvents();
                            if (m_sleepTime > 0)
                                Thread.Sleep(m_sleepTime);

                        }
                        var data = m_canListener.waitMessage(timeoutP2ct, 0x7E8).getData();
                        if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x76)
                        {
                            return false;
                        }
                        canUsbDevice.RequestDeviceReady();
                        SendKeepAlive();
                        startAddress += 0xEA;
                    }
                    else
                    {
                        CastInfoEvent("Did not receive correct response from SendTransferData", ActivityType.ConvertingFile);
                    }
                }
                CastProgressWriteEvent(100);
            }
            else
                return false;

            return true;
        }

        // Not used for anything as of yet.
        // TODO: Expand this function to verify if installed version is compatible with the main software
        private void PrintMCPVer()
        {
            bool success;
            byte[] Resp2 = new byte[10];

            // Start the secondary loader if required
            LegionIDemand(4, 0, out success);

            if (success)
            {
                CastInfoEvent(("MCP Firmware information"), ActivityType.DownloadingFlash);

                byte[] resp2 = readDataByLocalIdentifier(true, EcuByte_MCP, 0x8100, 0x80, out success);

                if (success)
                {
                    for (int i = 0; i < 10; i ++)
                    {
                        Resp2[i] = resp2[0xC + i];
                    }

                    string str2 = Encoding.ASCII.GetString(Resp2);
                    CastInfoEvent(("Version string: " + str2), ActivityType.DownloadingFlash);
                }
                else
                {
                    CastInfoEvent(("Version string: FAIL!"), ActivityType.DownloadingFlash);
                }
            }
            else
                CastInfoEvent(("Could not start the secondary loader to retreive version!"), ActivityType.DownloadingFlash);
        }


        // Let's stick to silly names
        // Needs fixing; Try and read several packets in case the first one is a reply from something else
        private byte[] LegionIDemand(uint command, uint wish, out bool success)
        {
            // Commands are as follows:
            // command 00: Configure packet delay.
            // wish: Delay ( default is 2000 )

            // command 01: Full Checksum-32
            // wish:
            // 00: Trionic 8.
            // 01: Trionic 8; MCP.

            // command 02: Trionic 8; md5.
            // wish:
            // 00: Full md5.
            // 01: Partition 1.
            // ..
            // 09: Partition 9.
            // Oddballs:
            // 10: Read from 0x00000 to last address of binary + 512 bytes
            // 11: Read from 0x04000 to last address of binary + 512 bytes
            // 12: Read from 0x20000 to last address of binary + 512 bytes

            // command 03: Trionic 8 MCP; md5.
            // wish:
            // 00: Full md5.
            // 01: Partition 1.
            // ..
            // 09: Partition 9 aka 'Shadow'.

            // command 04: Start secondary bootloader
            // wish: None, just wish.

            // command 05: Marry secondary processor
            // wish: None, just wish.        
            
            // Command 06: Read ADC pin
            // whish: Which pin to read.

            success = false;
            int Retries = 0;
            byte[] buf = new byte[16];

            CANMessage msg = new CANMessage(0x7E0, 0, 8);
            // Do some byteswapping
            ulong privatewish = ((wish & 0xff) << 24 | ((wish >> 8) & 0xff) << 16  );
            // Use ProgrammingMode as carrier 
            ulong cmd = (privatewish) << 32 | (command & 0xff) << 16 | 0xA502;

            do
            {
                msg.setData(cmd);
                m_canListener.setupWaitMessage(0x7E8);
                if (!canUsbDevice.sendMessage(msg))
                {
                    // Critical error; Abort...
                    CastInfoEvent("Couldn't send bootloader command", ActivityType.DownloadingFlash);
                    return buf;
                }

                CANMessage response = new CANMessage();
                response = new CANMessage();
                response = m_canListener.waitMessage(250);
                ulong data;
                data = response.getData();


                // elm327 strikes yet again; It has a fixed length for certain commands.
                // The loader will respond with something "slightly" out of spec to circumvent this.
                if (getCanData(data, 0) != 0x33 || getCanData(data, 1) != 0x55 || getCanData(data, 2) != (command & 0xFF) )
                {
                    CastInfoEvent("Retrying bootloader command..", ActivityType.DownloadingFlash);
                    logger.Debug(("(Legion) Retrying cmd " + command.ToString("X8") + " Wish "   + wish.ToString("X8")));
                    Retries++;
                }
                else
                {
                    // Settings correctly received.
                    if (command == 0 && getCanData(data, 3) == 1)
                    {
                        success = true;
                        return buf;
                    }

                    // Checksum32; complete
                    if (command == 1 && getCanData(data, 3) == 1)
                    {   
                        success = true;
                        for (uint i = 0; i < 4; i++)
                            buf[i] = getCanData(data, 4+i);

                        return buf;
                    }

                    // md5; complete
                    if ((command == 2 || command == 3) && getCanData(data, 3) == 1)
                    {
                        do
                        {   // ...
                            byte[] md5dbuf = readDataByLocalIdentifier(true, 7, 0, 16, out success);
                            if (success)
                                return md5dbuf;
                            else
                                Retries++;

                            Thread.Sleep(50);
                        } while (Retries < 20);

                        CastInfoEvent("Bootloader has generated md5 but it couldn't be fetched..", ActivityType.DownloadingFlash);
                    }

                    // Secondary loader is alive!
                    if (command == 4 && getCanData(data, 3) == 1)
                    {
                        success = true;
                        return buf;
                    }

                    // MCP marriage
                    if (command == 5)
                    {
                        // Critical error; Could not start the secondary loader!
                        if (getCanData(data, 3) == 0xFF)
                        {
                            CastInfoEvent("Failed to start the secondary loader!", ActivityType.UploadingFlash);
                            return buf;
                        }
                        // Failed to write!
                        else if (getCanData(data, 3) == 0xFD)
                        {
                            CastInfoEvent("Retrying write..", ActivityType.UploadingFlash);
                            Retries++;
                        }
                        // Failed to format!
                        else if (getCanData(data, 3) == 0xFE)
                        {
                            CastInfoEvent("Retrying format..", ActivityType.ErasingFlash);
                            Retries++;
                        }
                        // Marriage; Complete
                        else if (getCanData(data, 3) == 1)
                        {
                            success = true;
                            return buf;
                        }
                        // Busy
                        else
                        {
                            CastInfoEvent("..", ActivityType.UploadingFlash);
                        }

                        Thread.Sleep(750);
                    }

                    // ADC-read; complete
                    if (command == 6 && getCanData(data, 3) == 1)
                    {
                        success = true;
                        for (uint i = 0; i < 2; i++)
                            buf[i] = getCanData(data, 4 + i);

                        return buf;
                    }

                    // Something is wrong or we sent the wrong command.
                    if (getCanData(data, 3) == 0xFF)
                    {
                        CastInfoEvent("Bootloader did what it could and failed. Sorry", ActivityType.ConvertingFile);
                        return buf;
                    }
                }

                Thread.Sleep(50);

            } while (Retries < 20);

            // One should never get here unless something is wrong; Throw a generic error message. 
            CastInfoEvent("Gave up on bootloader command", ActivityType.ConvertingFile);
            return buf;
        }

        // Throw a warning if the user has selected format boot and it is different
        private bool LeaveRecoveryBe()
        {
            DialogResult result = DialogResult.No;

            result = MessageBox.Show("Do you REALLY want to write a new boot partition?!",
                "Point of no return", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (result == DialogResult.Yes)
            {
                CastInfoEvent("Warning, boot partition will be formated", ActivityType.ErasingFlash);
                return false;
            }

            CastInfoEvent("Skipped format of boot partition", ActivityType.ErasingFlash);
            return true;
        }

        // Throw a warning if the user has selected format sys and it is different
        private bool LeaveNVDMBe()
        {
            DialogResult result = DialogResult.No;

            result = MessageBox.Show("Do you REALLY want to flash new VIN and key data?!",
                "Point of no return", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (result == DialogResult.Yes)
            {
                CastInfoEvent("Warning, NVDM will be formated", ActivityType.ErasingFlash);
                return false;
            }

            CastInfoEvent("Skipped format of NVDM partitions", ActivityType.ErasingFlash);
            return true;
        }

        private byte[,] Partitionhashes = new byte[9,16];

        /// <summary>
        /// Fetch md5 of all partitions and store them in a local array
        /// </summary>
        /// <param name="workEvent"></param>
        /// <param name="device">Which device to hash</param>
        /// <returns>True as long as md5 could be fetched</returns>
        private bool FetchPartitionmd5(DoWorkEventArgs workEvent, byte device)
        {
            bool success;
            CastProgressReadEvent(0);

            for (byte i = 0; i < 9; i++)
            {
                byte[] resp = LegionIDemand(device == EcuByte_MCP ? (uint)3 : 2, (uint)(i + 1), out success);

                if (!success)
                {
                    return false;
                }

                for (byte m = 0; m < 16; m++)
                {
                    Partitionhashes[i, m] = resp[m];
                }
                CastProgressReadEvent((int)((i * 100) / (float)8));
            }
            return true;
        }

        /// <summary>
        /// Compare fetched hashes against file. Tag partitions accordingly
        /// </summary>
        /// <param name="workEvent"></param>
        /// <param name="device">Device to compare</param>
        /// <param name="z22se">Z22SE shall not skip boot or recovery partitions</param>
        private void DeterminePartitionmask(DoWorkEventArgs workEvent, byte device, bool z22se)
        {
            string filename = (string)workEvent.Argument;
            BlockManager bm = new BlockManager();
            bm.SetFilename(filename);

            byte toerase = 0;
            byte start   = 5;
            formatmask   = 0;

            // Determine where to start by checking current device and selected regions
            if ((formatBootPartition && formatSystemPartitions) || z22se)
                start = 1;
            else if (formatSystemPartitions || device == EcuByte_MCP)
                start = 2;

            for (byte i = start; i < 10; i++)
            {
                // Store bit location
                uint shift = (uint)(1 << (i - 1));
                bool identical = true;

                // Fetch partition md5
                byte[] Locmd5dbuf = bm.GetPartitionmd5(device, i);
                
                // Compare both md5's
                for (byte a = 0; a < 16; a++)
                {
                    if (Locmd5dbuf[a] != Partitionhashes[i - 1, a])
                        identical = false;
                }

                // Add partition to bitmask
                if (!identical)
                {
                    formatmask |= (uint)shift;
                    CastInfoEvent(("Partition " + i.ToString("X1") + ": Tagged for erase and write"), ActivityType.ConvertingFile);
                    logger.Debug(("(Legion) Partition " + i.ToString("X1") + ": Tagged for erase and write"));
                }
            }

            if (z22se == false)
            {
                // Warn about boot
                if ((formatmask & 1) > 0)
                {
                    if (LeaveRecoveryBe())
                        formatmask &= 0x1FE;
                }

                if (device == EcuByte_T8)
                {
                    // Warn about NVDM for T8 main
                    if ((formatmask & 6) > 0)
                    {
                        if (LeaveNVDMBe())
                            formatmask &= 0x1F9;
                    }

                    // Patch in additional partitions to make sure everything after the last used address contain 0xFF's
                    if (((formatmask & 0x1F0) > 0) && LegionOptions.UseLastMarker)
                    {
                        int EndAddress = (int)bm.GetLasAddress();
                        if (EndAddress < 0x100000)
                            formatmask |= 0x100; // Partition 9
                        if (EndAddress < 0x0C0000)
                            formatmask |= 0x080; // Partition 8
                        if (EndAddress < 0x080000)
                            formatmask |= 0x040; // Partition 7
                        if (EndAddress < 0x060000)
                            formatmask |= 0x020; // Partition 6
                        if (EndAddress < 0x040000)
                            formatmask |= 0x010; // Partition 5
                    }
                }
            }

            // MCP requires a few more checks..
            if (device == EcuByte_MCP)
            {
                // Reflect status of boot onto shadow
                formatmask &= 0xFF;
                formatmask |= ((formatmask & 1) << 8);

                // Mask off md5-partition since loader takes care of that in T8 mode
                if (z22se == false)
                    formatmask &= 0x1BF;
            }

            for (int i = 0; i < 9; i++)
            {
                if (((formatmask >> i) & 1) > 0)
                    toerase++;
            }

            // Only to lessen confusion
            CastInfoEvent("Patching mask according to current settings", ActivityType.UploadingFlash);
            CastInfoEvent(("Selected " + toerase.ToString("X1") + " out of 9 partitions for erase and flash"), ActivityType.StartErasingFlash);
            logger.Debug(("(Legion) Partition erase bitmask:" + formatmask.ToString("X3")));
        }

        /// <summary>
        /// Fetch md5 of every partition yet again. Compare unwritten ones against previously fetched hashes. Written ones against file.
        /// </summary>
        /// <param name="workEvent"></param>
        /// <returns>..</returns>
        private bool VerifyFlashIntegrity(DoWorkEventArgs workEvent, byte device, byte lastPartition)
        {
            string filename = (string)workEvent.Argument;
            BlockManager bm = new BlockManager();
            bm.SetFilename(filename);

            bool success = false;

            CastProgressReadEvent(0);

            for (byte i = 1; i <= lastPartition; i++)
            {
                uint shift = (uint)(1 << (i -1));
                bool Identical = true;

                // Fetch md5 from both locations
                byte[] RemoteMD = LegionIDemand(device == EcuByte_MCP ? (uint) 3 : 2, (uint) i, out success);
                byte[] LocalMD  = bm.GetPartitionmd5(device, i);

                // Could not fetch remote md5; Abort!
                if (!success)
                    return false;

                // Verify written data.
                if (((formatmask >> (i-1)) & 1) > 0)
                {
                    for (byte a = 0; a < 16; a++)
                    {
                        if (RemoteMD[a] != LocalMD[a])
                            Identical = false;
                    }

                    if (Identical)
                    {
                        formatmask &= ~shift;
                        logger.Debug(("(Legion) Written partition " + i.ToString("X1") + ": Verified"));
                    }

                    else
                    {
                        logger.Debug(("(Legion) Written partition " + i.ToString("X1") + ": VERIFICATION ERROR!"));
                        CastInfoEvent(("Written partition " + i.ToString("X1") + ": VERIFICATION ERROR!"), ActivityType.ConvertingFile);
                    }
                }

                // Verify old data
                else
                {
                    for (byte a = 0; a < 16; a++)
                    {
                        if (RemoteMD[a] != Partitionhashes[i - 1, a])
                            Identical = false;
                    }

                    if (Identical)
                    {
                        logger.Debug(("(Legion) Old partition " + i.ToString("X1") + ": Verified"));
                    }

                    else
                    {
                        formatmask |= shift;
                        logger.Debug(("(Legion) Old partition " + i.ToString("X1") + ": VERIFICATION ERROR!"));
                        CastInfoEvent(("Old partition " + i.ToString("X1") + ": VERIFICATION ERROR!"), ActivityType.ConvertingFile);
                    }
                }
                CastProgressReadEvent((int)((i * 100) / (float)lastPartition));
            }

            // Check for dangerous situation; Trionic 8 main
            if (device == EcuByte_T8 && ((formatmask & 1) > 0))
            {
                CastInfoEvent("\n\n", ActivityType.UploadingFlash);

                if (formatBootPartition == false)
                {
                    CastInfoEvent("An internal self-test has catched an unknown bug", ActivityType.UploadingFlash);
                    CastInfoEvent("Go to settings", ActivityType.UploadingFlash);
                    CastInfoEvent("Select 'I know what I am doing'", ActivityType.UploadingFlash);
                    CastInfoEvent("Select 'Unlock boot partition' and 'Unlock system partitions' and try again.", ActivityType.UploadingFlash);
                    CastInfoEvent("When asked if you want to write boot you MUST click YES!", ActivityType.UploadingFlash);

                    MessageBox.Show("DANGER: Read log window for further information\nFailure to follow instructions WILL brick the ECU",
                        "Boot is broken!!", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
                }
                else
                {
                    CastInfoEvent("Boot is broken. Do NOT power-cycle the ECU", ActivityType.UploadingFlash);
                    CastInfoEvent("Boot is broken. Do NOT power-cycle the ECU", ActivityType.UploadingFlash);
                }
            }

            // Check for dangerous situation; Trionic 8 / Z22SE MCP
            else if (device == EcuByte_MCP && ((formatmask & 0x101) > 0))
            {
                CastInfoEvent("\n\n", ActivityType.UploadingFlash);

                if (formatBootPartition == false)
                {
                    CastInfoEvent("An internal self-test has catched the dreaded MCP bug:", ActivityType.UploadingFlash);
                    CastInfoEvent("Go to settings", ActivityType.UploadingFlash);
                    CastInfoEvent("Select 'I know what I am doing'", ActivityType.UploadingFlash);
                    CastInfoEvent("Select 'Unlock boot partition' and try again.", ActivityType.UploadingFlash);
                    CastInfoEvent("When asked if you want to write boot you MUST click YES!", ActivityType.UploadingFlash);

                    MessageBox.Show("DANGER: Read log window for further information\nFailure to follow instructions WILL brick MCP",
                        "Boot is broken!!", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
                }
                else
                {
                    CastInfoEvent("Boot is broken. Do NOT power-cycle the ECU", ActivityType.UploadingFlash);
                    CastInfoEvent("Boot is broken. Do NOT power-cycle the ECU", ActivityType.UploadingFlash);
                }
            }

            CastInfoEvent("Done!", ActivityType.ConvertingFile);
            logger.Debug(("(Legion) Partition verify bitmask:" + formatmask.ToString("X3")));

            return true;
        }

        // Verify written boot, system and app partitions
        private bool CompareRegmd5(DoWorkEventArgs workEvent)
        {
            string filename = (string)workEvent.Argument;
            BlockManager bm = new BlockManager();
            bm.SetFilename(filename);
            bool success;

            // Verify lower partitions individually
            // boot, nvdm 1 / 2, hwio
            CastInfoEvent("Lower partitions..", ActivityType.ConvertingFile);
            if (!VerifyFlashIntegrity(workEvent, EcuByte_T8, 4))
                return false;

            // If any of the lower ones failed there's no need to continue
            if ((formatmask & 0xF) == 0)
            {
                CastInfoEvent("App data..", ActivityType.ConvertingFile);
                // hash from 0x20000 to last used address
                byte[] Locmd5dbuf = bm.GetPartitionmd5(EcuByte_T8, 12);
                byte[] Remd5dbuf = LegionIDemand(2, 12, out success);

                if (success)
                {
                    for (byte a = 0; a < 16; a++)
                    {
                        if (Locmd5dbuf[a] != Remd5dbuf[a])
                            success = false;
                    }
                    if (success)
                    {
                        CastInfoEvent("Done!", ActivityType.ConvertingFile);
                        formatmask = 0;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Perform a marriage of MCP
        /// </summary>
        /// <param name="z22se">The hash algorithm of this ECU is unknown</param>
        /// <returns></returns>
        bool MarryMCP(bool z22se)
        {
            bool success = true;

            if (!z22se)
            {
                CastInfoEvent("Proposing to MCP..", ActivityType.UploadingFlash);
                LegionIDemand(5, 0, out success);

                if (success)
                {
                    // Print firmware version just for reference
                    PrintMCPVer();
                }
            }

            return success;
        }

        private void LegionRequestexit()
        {
            byte i = 10;
            CastInfoEvent("Requesting bootloader exit..", ActivityType.ConvertingFile);
            do
            {
                if (Send0120())
                    return;
                Thread.Sleep(100);
            } while (--i > 0);

            CastInfoEvent("Bootloader did not respond to exit-request", ActivityType.ConvertingFile);
            CastInfoEvent("You may have to power-cycle the ECU", ActivityType.ConvertingFile);
        }

        private void WriteFlashLegion(byte Device, int EndAddress, bool z22se, object sender, DoWorkEventArgs workEvent)
        {
            if (!canUsbDevice.isOpen())
                return;

            BackgroundWorker bw = sender as BackgroundWorker;
            string filename = (string)workEvent.Argument;
            _stallKeepAlive = true;
            _needRecovery   = false;
            BlockManager bm = new BlockManager();
            bm.SetFilename(filename);

            // Init session and start loader 
            if (!StartCommon(Device, z22se))
            {
                _stallKeepAlive = false;
                workEvent.Result = false;
                return;
            }

            // Fetch md5 of all partitions
            CastInfoEvent("Comparing md5 for selective erase..", ActivityType.StartErasingFlash);
            if (!FetchPartitionmd5(workEvent, Device))
            {
                CastInfoEvent("Could not fetch md5!", ActivityType.StartErasingFlash);
                LegionRequestexit();
                _stallKeepAlive = false;
                workEvent.Result = false;
                return;
            }
            // Compare against file
            DeterminePartitionmask(workEvent, Device, z22se);

            if (formatmask > 0)
            {
                // Request format of selected partitions.
                if (SendrequestDownload(Device, false, true))
                {
                    _needRecovery = true;
                    CastInfoEvent("Programming FLASH", ActivityType.UploadingFlash);

                    if (ProgramFlashLeg(EndAddress, bm, Device))
                    {
                        CastInfoEvent("Verifying md5..", ActivityType.UploadingFlash);

                        // Simple method: verify individually
                        if (Device == EcuByte_MCP || z22se || !LegionOptions.UseLastMarker)
                        {
                            if (!VerifyFlashIntegrity(workEvent, Device, 9))
                            {
                                CastInfoEvent("Could not fetch MD5!", ActivityType.UploadingFlash);
                                formatmask = 0x1FF;
                            }
                        }
                        // Complex: check boot, nvdm 1/2 and hwio individually. App data as one region
                        else
                        {
                            if (!CompareRegmd5(workEvent))
                            {
                                CastInfoEvent("Could not fetch MD5!", ActivityType.UploadingFlash);
                                formatmask = 0x1FF;
                            }
                        }

                        if (formatmask == 0)
                        {
                            // It won't touch md5 on z22s
                            if(MarryMCP(z22se))
                                CastInfoEvent("FLASH upload completed and verified", ActivityType.FinishedFlashing);
                            else
                                for (int i = 0; i < 5; i++)
                                    CastInfoEvent("FLASH upload completed but the co-processor could not be married!", ActivityType.FinishedFlashing);

                            _needRecovery = false;
                            LegionRequestexit();
                        }
                        else
                        {
                            for (int i = 0; i < 5; i++)
                                CastInfoEvent("FLASH upload failed (Wrong checksum) Please try again!", ActivityType.FinishedFlashing);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < 5; i++)
                            CastInfoEvent("FLASH upload failed, please try again!", ActivityType.FinishedFlashing);
                    }

                    CastInfoEvent("Session ended", ActivityType.FinishedFlashing);
                    sw.Stop();
                }
                else
                {
                    sw.Stop();
                    _needRecovery = false;
                    _stallKeepAlive = false;
                    CastInfoEvent("Failed to erase FLASH", ActivityType.FinishedErasingFlash);
                    CastInfoEvent("Session ended but the bootloader is still active;", ActivityType.FinishedFlashing);
                    CastInfoEvent("You could try again", ActivityType.FinishedFlashing);
                    workEvent.Result = false;
                    return;
                }
            }
            // Tell loader to exit; Same data on both ends
            else
            {
                CastInfoEvent("Everything is identical", ActivityType.FinishedFlashing);
                if (!MarryMCP(z22se))
                    for (int i = 0; i < 5; i++)
                        CastInfoEvent("The co-processor could not be married!", ActivityType.UploadingFlash);

                LegionRequestexit();
            }

            _stallKeepAlive = false;
            workEvent.Result = true;
        }

        /// Sister function to ProgramFlashLeg()
        // Figure out whether or not to write a certain address range.
        private bool Erasedregion(int address, byte device)
        {
            // Note: The format function count partitions as 1 to 9.
            // this on the other hand will count from 0 to 8
            int part = 0;

            // T8 main
            if (device == EcuByte_T8)
            {
                if (address >= 0xC0000)
                    part = 8;
                else if (address >= 0x80000)
                    part = 7;
                else if (address >= 0x60000)
                    part = 6;
                else if (address >= 0x40000)
                    part = 5;
                else if (address >= 0x20000)
                    part = 4;
                else if (address >= 0x8000)
                    part = 3;
                else if (address >= 0x6000)
                    part = 2;
                else if (address >= 0x4000)
                    part = 1;
            }

            // MCP
            else if (device == EcuByte_MCP)
                part = (address >> 15) & 0xF;
            
            // Read one bit from selected part of the format mask to figure out if this partition should be written or not.
            return ((formatmask >> part) & 1) > 0 ? true : false;
        }

        private bool ProgramFlashLeg(int End, BlockManager bm, byte device)
        {
            const int startAddress = 0x000000;

            // The mcp part of legion can not handle writes to other than 64-byte boundaries, use hardcoded values to make sure that rule is followed 
            int lastBlockNumber = (End / 0x80)-1; 
            int saved_progress = 0;
            int length = 0x88; // 128 bytes plus dreq, param and checksum (We have 6 free bytes in the last package anyway)
            int numberOfFrames = 19;
            bool Problem = false;
            int Retries = 0;
            int blockNumber = 0;
            bool byteswapped = false;

            // Early MCP dumps were byteswapped..
            if (device == EcuByte_MCP)
                byteswapped = bm.mcpswapped();

            while (blockNumber <= lastBlockNumber)
            {
                int percentage = (int)(((float)blockNumber * 100) / (float)lastBlockNumber);
                if (percentage > saved_progress || percentage==0)
                {
                    CastProgressWriteEvent(percentage);
                    saved_progress = percentage;
                }

                // Reset status
                Problem = false;

                int currentAddress = startAddress + (blockNumber * 0x80);
                byte[] data2Send  = bm.GetCurrentBlock_128(blockNumber, byteswapped);
                
                // Calculate checksum-16 of frame-data and add it to the frame.
                int Csum16 = 0;

                for (int i = 0; i < (length - 8); i++)
                    Csum16 += data2Send[i];
                
                data2Send[length - 8] = (byte)(Csum16 >> 8 & 0xff);
                data2Send[length - 7] = (byte)(Csum16 & 0xff);

                sw.Reset();
                sw.Start();

                // Check for blocks filled with 0xFF / identical partitions and skip those
                if (!bm.FFblock(currentAddress, length - 8) && Erasedregion(currentAddress, device))
                {
                    if (SendTransferData(length, currentAddress, 0x7E8))
                    {
                        canUsbDevice.RequestDeviceReady();

                        byte iFrameNumber = 0x21;
                        int txpnt = 0;

                        CANMessage msg = new CANMessage(0x7E0, 0, 8);
                        for (int frame = 0; frame < numberOfFrames; frame++)
                        {
                            var cmd = BitTools.GetFrameBytes(iFrameNumber++, data2Send, txpnt);
                            msg.setData(cmd);

                            iFrameNumber &= 0x2F;
                            txpnt += 7;

                            msg.elmExpectedResponses = (frame == numberOfFrames - 1) ? 1 : 0;

                            if (frame == numberOfFrames - 1)
                                m_canListener.ClearQueue();

                            if (!canUsbDevice.sendMessage(msg))
                                logger.Debug("Couldn't send message");

                            // ELM is slow as it is..
                            if (!(canUsbDevice is CANELM327Device) && !LegionOptions.Faster && m_sleepTime > 0)
                            {
                                Thread.Sleep(m_sleepTime);
                            }
                        }

                        Application.DoEvents();
                        ulong data = m_canListener.waitMessage(timeoutP2ct, 0x7E8).getData();
                        if (getCanData(data, 0) != 0x01 || getCanData(data, 1) != 0x76)
                        {
                            if (++Retries < 20)
                            {
                                // Bootloader says something
                                if (data == 0x12367f01 || data == 0x33367f01)
                                {
                                    CastInfoEvent("Flash is very slow to respond at block " + currentAddress.ToString("X8"), ActivityType.UploadingFlash);
                                    Retries--;
                                }
                                else
                                    CastInfoEvent("Dropped frame. Retrying..", ActivityType.UploadingFlash);
 
                                Problem = true;
                            }
                            else
                            {
                                CastInfoEvent("Lost connection or something is really wrong with this ECUs flash ", ActivityType.UploadingFlash);
                                CastInfoEvent("Gave up on block-address: " + currentAddress.ToString("X6"), ActivityType.UploadingFlash);
                                _stallKeepAlive = false;
                                return false;
                            }
                        }
                        else
                            Retries = 0;

                        canUsbDevice.RequestDeviceReady();
                    }
                    else
                        Problem = true;
                }
                else
                    Application.DoEvents();

                sw.Stop();

                if (!Problem)
                    blockNumber++;
            }
            return true;
        }

        private void ReadFlashLegion(byte Device, int lastAddress, bool z22se, object sender, DoWorkEventArgs workEvent)
        {
            BackgroundWorker bw = sender as BackgroundWorker;
            string filename = (string)workEvent.Argument;

            _stallKeepAlive = true;
            bool success = false;
            int retryCount = 0;
            int startAddress = 0x000000;
            int blockSize = 0x80; // defined in bootloader... keep it that way!
            int bufpnt = 0;
            byte[] buf = new byte[lastAddress];
            uint Dropped = 0;
            uint Fallback = 1700;

            // Pre-fill buffer with 0xFF (unprogrammed FLASH chip value)
            for (int i = 0; i < lastAddress; i++)
                buf[i] = 0xFF;

            // Init session and start loader 
            if (!StartCommon(Device, z22se))
            {
                _stallKeepAlive = false;
                workEvent.Result = false;
                return;
            }

            CastInfoEvent("Downloading FLASH", ActivityType.DownloadingFlash);
            Stopwatch keepAliveSw = new Stopwatch();
            keepAliveSw.Start();

            int saved_progress = 0;

            CastInfoEvent("Downloading " + lastAddress.ToString("D") + " Bytes.", ActivityType.DownloadingFlash);

            while (startAddress < lastAddress)
            {
                if (!canUsbDevice.isOpen())
                {
                    _stallKeepAlive = false;
                    workEvent.Result = false;

                    return;
                }

                byte[] readbuf = readDataByLocalIdentifier(true, Device, startAddress, blockSize, out success);
                if (success)
                {
                    // figure out why readDataByLocalIdentifier() sometimes return true even though the frame is incomplete
                    if (Blockstoskip > 0)
                    {
                        bufpnt += (Blockstoskip * blockSize);

                        startAddress += Blockstoskip * blockSize;
                        retryCount = 0;
                    }
                    else if (readbuf.Length == blockSize)
                    {
                        for (int j = 0; j < blockSize; j++)
                            buf[bufpnt++] = readbuf[j];

                        startAddress += blockSize;
                        retryCount = 0;
                    }
                    else
                    {
                        retryCount++;
                        Dropped++;
                    }

                    int percentage = (int)((bufpnt * 100) / (float)lastAddress);
                    if (percentage > saved_progress)
                    {
                        CastProgressReadEvent(percentage);
                        saved_progress = percentage;
                    }
                }
                else
                {
                    CastInfoEvent("Frame dropped, retrying " + startAddress.ToString("X8") + " " + retryCount.ToString(), ActivityType.DownloadingFlash);
                    retryCount++;
                    Dropped++;

                    // read all available message from the bus now
                    for (int i = 0; i < 10; i++)
                    {
                        CANMessage response = new CANMessage();
                        ulong data = 0;
                        response = new CANMessage();
                        response = m_canListener.waitMessage(10);
                        data = response.getData();
                    }
                    if (retryCount == maxRetries)
                    {
                        CastInfoEvent("Failed to download FLASH content", ActivityType.DownloadingFlash);
                        _stallKeepAlive = false;
                        workEvent.Result = false;
                        LegionRequestexit();

                        return;
                    }
                }

                Application.DoEvents();

                // Throttle back after a set number of dropped frames.
                if (Dropped == 3 && Fallback < 8000)
                {
                    Thread.Sleep(100);
                    LegionIDemand(0, Fallback, out success);

                    if (!success)
                    {
                        // Make sure to try again
                        Dropped--;
                    }
                    else
                    {
                        CastInfoEvent("Too many dropped frames: Slowing down..", ActivityType.DownloadingFlash);

                        // Prepare to run even slower
                        Fallback += 500;
                        Dropped   =   0;
                    }
                }
            }
            sw.Stop();
            _stallKeepAlive = false;

            if (buf != null)
            {
                try
                {
                    System.Security.Cryptography.MD5CryptoServiceProvider md5 = new System.Security.Cryptography.MD5CryptoServiceProvider();
                    md5.Initialize();

                    CastInfoEvent("Verifying md5..", ActivityType.ConvertingFile);
                    byte[] Locmd5buf = md5.ComputeHash(buf);
                    byte[] Remd5dbuf = LegionIDemand(Device == EcuByte_MCP ? (uint) 3 : 2, 0, out success);

                    if (success)
                    {
                        for (byte i = 0; i < 16; i++)
                        {
                            if (Remd5dbuf[i] != Locmd5buf[i])
                                success = false;
                        }

                        if (success)
                        {
                            File.WriteAllBytes(filename, buf);
                            Md5Tools.WriteMd5HashFromByteBuffer(filename, buf);

                            CastInfoEvent("Download done and verified", ActivityType.FinishedDownloadingFlash);
                        }
                        else
                        {
                            CastInfoEvent("Local data does not match ECU! Discarding data..", ActivityType.FinishedDownloadingFlash);
                        }
                    }
                    else
                    {
                        CastInfoEvent("Could not fetch md5! Discarding data..", ActivityType.FinishedDownloadingFlash);
                    }
                    workEvent.Result = success;
                }
                catch (Exception e)
                {
                    CastInfoEvent("Could not write file... " + e.Message, ActivityType.ConvertingFile);
                    workEvent.Result = false;
                }
            }
            else
                workEvent.Result = false;

            // Loader will never exit on its own. Tell it to 
            LegionRequestexit();

            return;
        }

        private void WriteDidFile(string filename, Dictionary<uint, byte[]> dids)
        {
            string didFilename = Path.GetDirectoryName(filename);
            didFilename = Path.Combine(didFilename, Path.GetFileNameWithoutExtension(filename) + ".did");

            using (StreamWriter writer = new StreamWriter(didFilename, false))
            {
                foreach (var kvp in dids)
                {
                    writer.WriteLine(string.Format("{0},{1}", kvp.Key, Convert.ToBase64String(kvp.Value)));
                }
            }
        }

        private Dictionary<uint, byte[]> ReadDidFile(string filename)
        {
            Dictionary<uint, byte[]> readBack = new Dictionary<uint, byte[]>();
            using (StreamReader reader = new StreamReader(filename, false))
            {
                string line = reader.ReadLine();
                while (line != null)
                {
                    string[] did = line.Split(',');
                    uint id = UInt16.Parse(did[0]);
                    byte[] data = Convert.FromBase64String(did[1]);
                    readBack.Add(id, data);
                    line = reader.ReadLine();
                }
            }

            return readBack;
        }

        public bool LoadAllDID(string filename)
        {
            Dictionary<uint, byte[]> read = ReadDidFile(filename);
            foreach (var kvp in read)
            {
                if (kvp.Key != 0x5D
                    && kvp.Key != 0x73 && kvp.Key != 0x74 && kvp.Key != 0x76 && kvp.Key != 0x7A 
                    && kvp.Key != 0x92 && kvp.Key != 0x96 && kvp.Key != 0x98 && kvp.Key != 0x9A
                    && kvp.Key != 0xB0
                    && kvp.Key != 0xB4 // serial number! Error, case 0x12: "subFunction not supported - invalid format";
                    && kvp.Key != 0xC1 && kvp.Key != 0xC2 && kvp.Key != 0xC3
                    && kvp.Key != 0xD1 && kvp.Key != 0xD2 && kvp.Key != 0xD3 && kvp.Key != 0xDC)
                {
                    CastInfoEvent("Write ID 0x" + kvp.Key.ToString("X"), ActivityType.ConvertingFile);
                    WriteECUInfo(kvp.Key, kvp.Value);
                }
            }

            // Persist 
            RequestSecurityAccess(0);
            SendDeviceControlMessage(0x16);

            return true;
        }

        public bool SaveAllDID(string filename)
        {
            Dictionary<uint, byte[]> dids = ReadDid();
            WriteDidFile(filename, dids);

            return true;
        }
    }
}
