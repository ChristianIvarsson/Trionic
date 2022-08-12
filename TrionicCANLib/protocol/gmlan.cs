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
using System.Collections;
using NLog;
using TrionicCANLib.Checksum;
using TrionicCANLib.SeedKey;

/*
Important notes:

_NEVER_ use automatic broadcasts of requests that use the TransferFrame method
Failure to follow this _WILL CORRUPT_ any currently running requests
*/

/*
Current TODO:
Implement ability to fetch last reason for failure (Well, error handling in general)
Add timeout while waiting for target readyness (FlowControl 31 instead of 30)
Add timeout while waiting for target to transition from "Request correctly received, please wait"
BS flag for consecutive messages
ProgrammingMode should expect NOT to see a response for level 3 (enableProgrammingMode)
"TranslateErrorCode" is _NOT_ translating error codes correctly for gmlan

Investigate:
Had a weird instance where transfer usdt in e39 would error out with "unable to send" after this had been used

*/

// To be erradicated. There's some elm-junk in there that I want to transplant
/*
        // This definitely needs some tweaking for ELM...
        // <returns>[0] = Number of frames before another wait, [1] delay in ms</returns>
        private uint[] flowControlSend(uint ID, out bool success)
        {
            success = false;
            while (true)
            {
                CANMessage response = new CANMessage();
                response = canListener.waitMessage(500);
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
                    canListener.setupWaitMessage(ID);
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

            canListener.ClearQueue();

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
                canListener.setupWaitMessage(0x7E8);
                if (!canUsbDevice.sendMessage(msg))
                {
                    CastInfoEvent("TransferUSDT: Couldn't send message", ActivityType.ConvertingFile);
                    return failRet;
                }

                // Only / first frame could take some time if it's performing a write etc (and I believe 500 is max what ELM327 handles?)
                response = canListener.waitMessage(500);
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
                bytesLeft -= 7;

                msg.setData(cmd);
                // msg.elmExpectedResponses = 1;
                canListener.setupWaitMessage(0x7E8);

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
                        canListener.ClearQueue();
                        canListener.setupWaitMessage(0x7E8);
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
                response = canListener.waitMessage(500);
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
                if ((bytesLeft & 0xF0) == 0x10)
                {
                    bytesLeft = ((uint)getCanData(data, 0) << 8 | getCanData(data, 1)) & 0xFFF;
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
                    canListener.setupWaitMessage(0x7E8);

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
                        response = canListener.waitMessage(timeoutP2ct);
                        data = response.getData();

                        if (getCanData(data, 0) != stepper++)
                        {
                            Thread.Sleep(500);
                            canListener.FlushQueue();
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
        */


namespace TrionicCANLib.API
{
    public class GMLAN : GMSHARED
    {
        // Junk that is to be eradicated or rewritten - get the f*ck out of here!!!
        private const int timeoutP2ct = 500;

        // For error handling, does the buffer contain a response to the last request
        private bool m_HaveFullResponse = false;

        // Should it adhere to received 3x xx xx settings or just do its own thing
        private bool m_TargetDeterminedDelays = true;
        public bool TargetDeterminedDelays
        {
            set { m_TargetDeterminedDelays = value; }
            get { return m_TargetDeterminedDelays; }
        }

        // How long should the HOST wait between consecutive frames? (Time in microseconds)
        // This is a forced parameter and will only come into effect when "m_TargetDeterminedDelays" is set to false
        private uint m_HostDelay = 0;
        public uint HostDelay
        {
            set { m_HostDelay = value; }
            get { return m_HostDelay; }
        }

        // How long should the TARGET wait between consecutive frames?
        // This value is stored in its encoded form and sent as is.
        private uint m_TargetDelay = 1;
        public uint TargetDelay
        {
            // This value is weird..
            // 0 - 7f == ms 0 - 127
            // f1 - f9 == micro / 100
            set
            {
                uint val = value;
                if (val > 127000)
                {
                    m_TargetDelay = 127;
                }
                else if (val > 949)
                {
                    // If at or above the 0.5 ms threshold, round up
                    m_TargetDelay = (val + 500) / 1000;
                }
                else if (val < 50)
                {
                    m_TargetDelay = 0;
                }
                else // 50 - 949
                {
                    // If at or above the 0.05 ms threshold, round up
                    m_TargetDelay = 0xf0 + ((m_TargetDelay + 50) / 100);
                }
            }
            get
            {
                if (m_TargetDelay < 0x80)
                {
                    return m_TargetDelay * 1000;
                }
                else if (m_TargetDelay > 0xf0 && m_TargetDelay < 0xfa)
                {
                    return (m_TargetDelay - 0xf0) * 100;
                }
                else
                {
                    return 127000;
                }
            }
        }

        // Is there a bind feature in this weirdo language?
        public GMLAN(ITrionic parent) : base(parent)
        {
        }

        public string RequestStatus(int REQ = -1)
        {
            if (m_HaveFullResponse == false)
            {
                return "Dropped package or no response at all";
            }

            if (REQ >= 0 && ReadData[0] == (REQ + 0x40))
            {
                return "No error";
            }

            if (ReadData[0] != 0x7f)
            {
                return "Unknown response or no fault";
            }

            // 7f REQ XX
            return TranslateErrorCode(ReadData[2]);
        }

        public override string TranslateErrorCode(byte p)
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

        /* USDT PCI encoding
        Byte          __________0__________ ____1____ ____2____
        Bits         | 7  6  5  4    3:0   |   7:0   |   7:0   |
        Single       | 0  0  0  0     DL   |   N/A   |   N/A   |   (XX, .. ..)
        First consec | 0  0  0  1   DL hi  |  DL lo  |   N/A   |   (1X, XX ..)
        Consecutive  | 0  0  1  0     SN   |   N/A   |   N/A   |   (21, 22, 23 .. 29, 20, 21 ..)
        Flow Control | 0  0  1  1     FS   |   BS    |  STmin  |   (3X, XX, XX)
        Extrapolated:
        Exd single   | 0  1  0  0   addr?  | ????:dl |   N/A   |   101 [41 92    (1a 79)   00000000] */
        public override int TransferFrame(int BytesToSend)
        {
            CANMessage msg = new CANMessage(m_TesterId, 0, 8);
            CANMessage response;
            ulong data;

            m_HaveFullResponse = false;

            if (BytesToSend < 1 || BytesToSend > 4095)
            {
                m_parent.CastInfoEvent("TransferFrame: Check lengths!", ActivityType.TransferLayer);
                return 0;
            }

            // Fast-forward queue index
            m_parent.canListener.ClearQueue();

            /////////////////////
            // Send

            if (BytesToSend < 8)
            {
                /////////////////////
                // <0x REQ .. ..>
                msg.setLength((byte)(BytesToSend + 1));
                ulong cmd = (ulong)BytesToSend;

                for (int i = 0; i < BytesToSend; i++)
                {
                    cmd |= (ulong)DataToSend[i] << ((i + 1) * 8);
                }

                msg.setData(cmd);
                m_parent.canListener.setupWaitMessage(m_TargetId);
                if (!m_parent.canUsbDevice.sendMessage(msg))
                {
                    m_parent.CastInfoEvent("TransferFrame: Couldn't send message", ActivityType.TransferLayer);
                    return 0;
                }
            }
            else
            {
                /////////////////////
                // <1x xx REQ .. ..>
                ulong cmd = (ulong)((BytesToSend & 0xff) << 8 | (BytesToSend >> 8) | 0x10);
                int bufPtr = 0;
                int stp = 0x21;

                BytesToSend -= 6;
                for (int i = 2; i < 8; i++)
                {
                    cmd |= (ulong)DataToSend[bufPtr++] << (i * 8);
                }

                msg.setData(cmd);
                m_parent.canListener.setupWaitMessage(m_TargetId);
                if (!m_parent.canUsbDevice.sendMessage(msg))
                {
                    m_parent.CastInfoEvent("TransferFrame: Couldn't send message", ActivityType.TransferLayer);
                    return 0;
                }

                /////////////////////
                // <3x xx xx>

                // Wait for target readyness
                // TODO: Implement timeout
                do
                {
                    response = m_parent.canListener.waitMessage(timeoutP2ct);
                    data = response.getData();
                } while ((data & 0xff) == 0x31);

                if ((data & 0xff) == 0x30) // All is green
                {
                    uint BS = (uint)(data >> 8) & 0xff;
                    uint ST = (uint)(data >> 16) & 0xff;

                    if (BS > 0)
                    {
                        // TODO: Find a target that is even able to send this and implement it!
                        m_parent.CastInfoEvent("TransferFrame: Unable to cope with segmented transfer", ActivityType.TransferLayer);
                        return 0;
                    }

                    uint delayMicro = m_HostDelay;

                    if (m_TargetDeterminedDelays)
                    {
                        if (ST < 0x80)
                        {
                            delayMicro = ST * 1000;
                        }
                        else if (ST > 0xf0 && ST < 0xfa)
                        {
                            // delayMicro = (ST - 0xf0) * 100;
                            // One could do a blocked delay but it's just wasting resources...
                            delayMicro = 1000;
                        }
                    }

                    /////////////////////
                    // <21 .. ..>
                    // <22 .. ..>
                    // ..
                    // <2f .. ..>
                    // <20 .. ..>

                    while (BytesToSend > 0)
                    {
                        int thisLen = (BytesToSend > 7) ? 7 : BytesToSend;
                        BytesToSend -= thisLen;

                        cmd = (ulong)stp;
                        stp = (stp + 1) & 0x2f;

                        for (int i = 0; i < thisLen; i++)
                        {
                            cmd |= (ulong)DataToSend[bufPtr++] << ((i + 1) * 8);
                        }

                        if (BytesToSend == 0)
                        {
                            // Fast-forward queue index
                            m_parent.canListener.ClearQueue();
                            m_parent.canListener.setupWaitMessage(m_TargetId);
                        }

                        msg.setData(cmd);
                        if (!m_parent.canUsbDevice.sendMessage(msg))
                        {
                            m_parent.CastInfoEvent("TransferFrame: Couldn't send message", ActivityType.TransferLayer);
                            return 0;
                        }

                        if (BytesToSend != 0 && delayMicro > 499)
                        {
                            Thread.Sleep((int)(delayMicro + 500) / 1000);
                        }
                    }
                }
                else if ((data & 0xff) == 0x32)
                {
                    m_parent.CastInfoEvent("TransferFrame: Target outright refuse to receive frame of this size", ActivityType.TransferLayer);
                    return 0;
                }
                else
                {
                    m_parent.CastInfoEvent("TransferFrame: Unknown response where FlowControl is expected", ActivityType.TransferLayer);
                    return 0;
                }
            }

        /////////////////////
        // Receive

        // The one and only "retry-handler" inside the frame method.
        // GMLAN dictates that a target could delay responses while busy
        ResponsePending:

            response = m_parent.canListener.waitMessage(timeoutP2ct);
            data = response.getData();

            // Single frame
            if ((data & 0xff) > 0 && (data & 0xff) < 8)
            {
                // xx7fxx78
                if ((data & 0xff00ff00) == 0x78007f00)
                {
                    goto ResponsePending;
                }

                int recLen = (int)(data & 0xff);

                for (int i = 0; i < recLen; i++)
                {
                    data >>= 8;
                    ReadData[i] = (byte)data;
                }

                m_HaveFullResponse = true;
                return recLen;
            }
            // Multi-frame
            else if ((data & 0xf0) == 0x10)
            {
                // TODO: How does a long busy-frame look??

                int bufPtr = 0;
                int stp = 0x21;
                int recLen = (int)((data & 0xf) << 8 | ((data >> 8) & 0xff));
                data >>= 16;

                // Expect a multi-frame to be at least 8 bytes
                if (recLen < 8)
                {
                    m_parent.CastInfoEvent("TransferFrame: Received malformed multi-frame", ActivityType.TransferLayer);
                    return 0;
                }

                recLen -= 6;
                for (int i = 0; i < 6; i++)
                {
                    ReadData[bufPtr++] = (byte)data;
                    data >>= 8;
                }

                msg.setData(m_TargetDelay << 16 | 0x000030);
                m_parent.canListener.setupWaitMessage(m_TargetId);
                if (!m_parent.canUsbDevice.sendMessage(msg))
                {
                    m_parent.CastInfoEvent("TransferFrame: Couldn't send message", ActivityType.TransferLayer);
                    return 0;
                }

                while (recLen > 0)
                {
                    int thisLen = (recLen > 7) ? 7 : recLen;
                    recLen -= thisLen;

                    response = m_parent.canListener.waitMessage(timeoutP2ct);
                    data = response.getData();

                    if ((int)(data & 0xff) != stp)
                    {
                        m_parent.CastInfoEvent("TransferFrame: Dropped message", ActivityType.TransferLayer);
                        return 0;
                    }

                    stp = (stp + 1) & 0x2f;

                    for (int i = 0; i < thisLen; i++)
                    {
                        data >>= 8;
                        ReadData[bufPtr++] = (byte)data;
                    }
                }

                m_HaveFullResponse = true;
                return bufPtr;
            }
            else if (data == 0)
            {
                m_parent.CastInfoEvent("TransferFrame: No response", ActivityType.TransferLayer);
                return 0;
            }

            m_parent.CastInfoEvent("TransferFrame: Received malformed or unknown message type", ActivityType.TransferLayer);
            return 0;
        }

        // Req: 04
        public bool ClearDiagnosticInformation()
        {
            DataToSend[0] = 0x04;

            return (TransferFrame(1) > 0 && ReadData[0] == 0x44);
        }

        // Req: 10 mode
        // Mode 02: disableAllDTCs
        // Mode 03: enableDTCsDuringDevCntrl
        // Mode 04: wakeUpLinks
        public bool InitiateDiagnosticOperation(byte mode)
        {
            DataToSend[0] = 0x10;
            DataToSend[1] = mode;

            return (TransferFrame(2) > 0 && ReadData[0] == 0x50);
        }

        // Req: 12 sub (ReadFailureRecordData)
        // Sub 01: readFailureRecordIdentifiers <- this sub-request
        // Sub 02: readFailureRecordParameters
        public bool ReadFailureRecordIdentifiers(out List <FailureRecord> records)
        {
            int retLen;
            records = new List<FailureRecord>();

            DataToSend[0] = 0x12;
            DataToSend[1] = 1;

            if ((retLen = TransferFrame(2)) > 1 && ReadData[0] == 0x52 && ReadData[1] == 0x01)
            {
                if (retLen > 2)
                {
                    // TODO: Do something useful with "failureRecordDataStructureIdentifier"
                    int bufPtr = 3;
                    retLen -= 3;

                    while (retLen >= 4)
                    {
                        FailureRecord record = new FailureRecord();
                        record.Number = ReadData[bufPtr];
                        record.Code = (uint)(ReadData[bufPtr + 1] << 8 | ReadData[bufPtr + 2]);
                        record.Type = ReadData[bufPtr + 3];
                        records.Add(record);
                        bufPtr += 4;
                        retLen -= 4;
                    }
                }

                return true;
            }

            return false;
        }

        // Req: 20
        public bool ReturnToNormal()
        {
            DataToSend[0] = 0x20;

            return (TransferFrame(1) > 0 && ReadData[0] == 0x60);
        }

        // Req: 23 XX XX XX YY YY
        public byte[] ReadMemoryByAddress_24_16(uint address, uint len, uint blockSize = 0x80)
        {
            uint bufPtr = 0;
            if (len == 0)
            {
                return null;
            }

            byte[] buf = new byte[len];
            DataToSend[0] = 0x23;

            while (len > 0)
            {
                uint thisLen = (len > blockSize) ? blockSize : len;
                DataToSend[1] = (byte)(address >> 16);
                DataToSend[2] = (byte)(address >> 8);
                DataToSend[3] = (byte)address;
                DataToSend[4] = (byte)(thisLen >> 8);
                DataToSend[5] = (byte)thisLen;

                // 63 XX XX XX ..
                if (TransferFrame(6) == (thisLen + 4) &&
                    ReadData[0] == 0x63 &&
                    ReadData[1] == (byte)(address >> 16) &&
                    ReadData[2] == (byte)(address >> 8) &&
                    ReadData[3] == (byte)address)
                {
                    for (int i = 0; i < thisLen; i++)
                    {
                        buf[bufPtr++] = ReadData[i + 4];
                    }
                }
                else
                {
                    return null;
                }

                len -= thisLen;
                address += thisLen;
            }

            return buf;
        }

        // Req: 23 XX XX XX YY YY
        public bool ReadMemoryByAddress_24_16(uint address, uint len)
        {
            if (len == 0 || len > (4095 - 4))
            {
                return false;
            }

            DataToSend[0] = 0x23;
            DataToSend[1] = (byte)(address >> 16);
            DataToSend[2] = (byte)(address >> 8);
            DataToSend[3] = (byte)address;
            DataToSend[4] = (byte)(len >> 8);
            DataToSend[5] = (byte)len;

            // 63 XX XX XX ..
            if (TransferFrame(6) == (len + 4) &&
                ReadData[0] == 0x63 &&
                ReadData[1] == (byte)(address >> 16) &&
                ReadData[2] == (byte)(address >> 8) &&
                ReadData[3] == (byte)address)
            {
                return true;
            }

            return false;
        }

        // Req: 23 XX XX XX XX YY YY
        public byte[] ReadMemoryByAddress_32_16(uint address, uint len, uint blockSize = 0x80)
        {
            uint bufPtr = 0;
            if (len == 0)
            {
                return null;
            }

            byte[] buf = new byte[len];
            DataToSend[0] = 0x23;

            while (len > 0)
            {
                uint thisLen = (len > blockSize) ? blockSize : len;
                DataToSend[1] = (byte)(address >> 24);
                DataToSend[2] = (byte)(address >> 16);
                DataToSend[3] = (byte)(address >> 8);
                DataToSend[4] = (byte)address;
                DataToSend[5] = (byte)(thisLen >> 8);
                DataToSend[6] = (byte)thisLen;

                // 63 XX XX XX XX ..
                if (TransferFrame(7) == (thisLen + 5) &&
                    ReadData[0] == 0x63 &&
                    ReadData[1] == (byte)(address >> 24) &&
                    ReadData[2] == (byte)(address >> 16) &&
                    ReadData[3] == (byte)(address >> 8) &&
                    ReadData[4] == (byte)address)
                {
                    for (int i = 0; i < thisLen; i++)
                    {
                        buf[bufPtr++] = ReadData[i + 5];
                    }
                }
                else
                {
                    return null;
                }

                len -= thisLen;
                address += thisLen;
            }

            return buf;
        }

        // Req: 23 XX XX XX XX YY YY
        public bool ReadMemoryByAddress_32_16(uint address, uint len)
        {
            if (len == 0 || len > (4095 - 5))
            {
                return false;
            }

            DataToSend[0] = 0x23;
            DataToSend[1] = (byte)(address >> 24);
            DataToSend[2] = (byte)(address >> 16);
            DataToSend[3] = (byte)(address >> 8);
            DataToSend[4] = (byte)address;
            DataToSend[5] = (byte)(len >> 8);
            DataToSend[6] = (byte)len;

            // 63 XX XX XX XX ..
            if (TransferFrame(7) == (len + 5) &&
                ReadData[0] == 0x63 &&
                ReadData[1] == (byte)(address >> 24) &&
                ReadData[2] == (byte)(address >> 16) &&
                ReadData[3] == (byte)(address >> 8) &&
                ReadData[4] == (byte)address)
            {
                return true;
            }

            return false;
        }

        // Req: 28
        public bool DisableNormalCommunication()
        {
            DataToSend[0] = 0x28;

            return (TransferFrame(1) > 0 && ReadData[0] == 0x68);
        }

        // Req: 34 .. .. ..
        public bool RequestDownload(uint size, uint bitcount, byte fmt = 0)
        {
            DataToSend[0] = 0x34;
            DataToSend[1] = fmt;

            switch (bitcount)
            {
                // 34 YY XX XX
                case 16:
                    DataToSend[2] = (byte)(size >> 8);
                    DataToSend[3] = (byte)size;
                    return (TransferFrame(4) > 0 && ReadData[0] == 0x74);
                // 34 YY XX XX XX
                case 24:
                    DataToSend[2] = (byte)(size >> 16);
                    DataToSend[3] = (byte)(size >> 8);
                    DataToSend[4] = (byte)size;
                    return (TransferFrame(5) > 0 && ReadData[0] == 0x74);
                // 34 YY XX XX XX XX
                case 32:
                    DataToSend[2] = (byte)(size >> 24);
                    DataToSend[3] = (byte)(size >> 16);
                    DataToSend[4] = (byte)(size >> 8);
                    DataToSend[5] = (byte)size;
                    return (TransferFrame(6) > 0 && ReadData[0] == 0x74);
            }

            return false;
        }

        // Req: 36 YY XX XX XX XX
        public bool TransferData_32(byte[] data, uint address, uint len, uint blockSize = 0x80, bool execute = false)
        {
            uint bufPtr = 0;
            uint oAddress = address;

            DataToSend[0] = 0x36;
            DataToSend[1] = 0x00;

            if (len > 0 && (data == null || data.Length < len || blockSize == 0))
            {
                return false;
            }
            else if (blockSize > (4095 - 6))
            {
                return false;
            }

            while (len > 0)
            {
                uint thisLen = (len > blockSize) ? blockSize : len;
                DataToSend[2] = (byte)(address >> 24);
                DataToSend[3] = (byte)(address >> 16);
                DataToSend[4] = (byte)(address >> 8);
                DataToSend[5] = (byte)address;

                for (int i = 0; i < thisLen; i++)
                {
                    DataToSend[6 + i] = data[bufPtr++];
                }

                if (TransferFrame((int)(6 + thisLen)) > 0)
                {
                    if (ReadData[0] != 0x76)
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }

                address += thisLen;
                len -= thisLen;
            }

            if (execute)
            {
                // There should be no response if all went well so this must be handled differently
                CANMessage msg = new CANMessage(m_TesterId, 0, 7);

                ulong cmd = 0x00000000803606;
                cmd |= (ulong)((oAddress >> 24) & 0xff) << 24;
                cmd |= (ulong)((oAddress >> 16) & 0xff) << 32;
                cmd |= (ulong)((oAddress >> 8) & 0xff) << 40;
                cmd |= (ulong)((oAddress) & 0xff) << 48;

                // Fast-forward queue index
                m_parent.canListener.ClearQueue();

                msg.setData(cmd);
                m_parent.canListener.setupWaitMessage(m_TargetId);
                if (!m_parent.canUsbDevice.sendMessage(msg))
                {
                    m_parent.CastInfoEvent("TransferData: Couldn't send message", ActivityType.TransferLayer);
                    return false;
                }

                CANMessage response = m_parent.canListener.waitMessage(timeoutP2ct);
                cmd = response.getData();

                if (cmd != 0)
                {
                    // What the h.. It's not supposed to give a OK response to this request!
                    if ((cmd & 0xff00) == 0x7600 && (cmd & 0xff) < 8)
                    {
                        m_parent.CastInfoEvent("TransferData: Unexpected response (Target should not respond)", ActivityType.TransferLayer);
                        return true;
                    }
                    if ((cmd & 0xff00) == 0x7f00)
                    {
                        m_parent.CastInfoEvent("TransferData: Error due to " + TranslateErrorCode((byte)(cmd >> 16)), ActivityType.TransferLayer);
                    }
                    else
                    {
                        m_parent.CastInfoEvent("TransferData: Unexpected response", ActivityType.TransferLayer);
                    }

                    return false;
                }
            }

            return true;
        }

        // Req: a2
        // 00: fully programmed
        // 01: no op s/w or cal data
        // 02: op s/w present, cal data missing
        // 50: General Memory Fault
        // 51: RAM Memory Fault
        // 52: NVRAM Memory Fault
        // 53: Boot Memory Failure
        // 54: Flash Memory Failure
        // 55: EEPROM Memory Failure
        public bool ReportProgrammedState()
        {
            DataToSend[0] = 0xa2;

            if (TransferFrame(1) < 2 || ReadData[0] != 0xe2)
            {
                m_parent.CastInfoEvent("Programmed state: Could not retrieve info", ActivityType.TransferLayer);
                return false;
            }
            else
            {
                string state = "Programmed state: ";

                switch (ReadData[1])
                {
                    case 0x00: state += "Fully programmed"; break;
                    case 0x01: state += "No op s/w or cal data"; break;
                    case 0x02: state += "Op s/w present, cal data missing"; break;
                    case 0x50: state += "General Memory Fault"; break;
                    case 0x51: state += "RAM Memory Fault"; break;
                    case 0x52: state += "NVRAM Memory Fault"; break;
                    case 0x53: state += "Boot Memory Failure"; break;
                    case 0x54: state += "Flash Memory Failure"; break;
                    case 0x55: state += "EEPROM Memory Failure"; break;
                    default:
                        state += "Unknown response " + ReadData[1].ToString("X2");
                        break;
                }

                m_parent.CastInfoEvent(state, ActivityType.TransferLayer);
                return true;
            }
        }

        // Req: a5 lev
        // lev 1: requestProgrammingMode
        // lev 2: requestProgrammingMode_HighSpeed
        // lev 3: enableProgrammingMode
        public bool ProgrammingMode(byte lev)
        {
            if (lev != 3)
            {
                DataToSend[0] = 0xa5;
                DataToSend[1] = lev;

                if (TransferFrame(2) > 0)
                {
                    if (ReadData[0] == 0xE5)
                    {
                        return true;
                    }
                }
            }
            else
            {
                // There should be no response if all went well so this must be handled differently
                CANMessage msg = new CANMessage(m_TesterId, 0, 3);

                ulong cmd = 0x03a502;

                // Fast-forward queue index
                m_parent.canListener.ClearQueue();

                msg.setData(cmd);
                m_parent.canListener.setupWaitMessage(m_TargetId);
                if (!m_parent.canUsbDevice.sendMessage(msg))
                {
                    m_parent.CastInfoEvent("ProgrammingMode: Couldn't send message", ActivityType.TransferLayer);
                    return false;
                }

                CANMessage response = m_parent.canListener.waitMessage(timeoutP2ct);
                cmd = response.getData();

                if (cmd != 0)
                {
                    // What the h.. It's not supposed to give a OK response to this request!
                    if ((cmd & 0xff00) == 0xE500 && (cmd & 0xff) < 8)
                    {
                        m_parent.CastInfoEvent("ProgrammingMode: Unexpected response (Target should not respond)", ActivityType.TransferLayer);
                        return true;
                    }
                    else if ((cmd & 0xff00) == 0x7f00 && (cmd & 0xff) < 8)
                    {
                        m_parent.CastInfoEvent("ProgrammingMode: Error due to " + TranslateErrorCode((byte)(cmd >> 16)), ActivityType.TransferLayer);
                    }
                    else
                    {
                        m_parent.CastInfoEvent("ProgrammingMode: Unexpected response", ActivityType.TransferLayer);
                    }

                    return false;
                }

                return true;
            }

            return false;
        }
    }
}
