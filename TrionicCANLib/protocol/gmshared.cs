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

namespace TrionicCANLib.API
{
    // For retrieval of error codes 
    public class FailureRecord
    {
        // Record number
        public uint Number = 0;
        // Error code
        public uint Code = 0;
        // These are manufacturer specific and depending how the record is used, might not even be set
        public uint FailureType = 0;
        public uint Status = 0;
    }

    // Used by "ReadDIDList" to retrieve a chunk of specified DIDs
    public enum InfoType : int
    {
        // Display as ascii chars by default (will revert to hex array if it contains forbidden chars)
        InfoTypeString,
        // Display as hex array
        InfoTypeArray,
        // Display as a single or array of u32 values in decimal form
        InfoTypeU32
    }

    public class ReadDIDInfo
    {
        // DID to retrieve
        public byte DID;
        // How to display
        public InfoType Type;
        // Description of data (VIN, Programming date etc)
        public string Readable;
    }

    // Base class for gmlan and future kwp2k
    public class GMSHARED
    {
        // Very much bad practice!
        // Is it perhaps possible to attach a base class of ITrionic and tell the current instance to replace its base class with one that is passed in GMSHARED()?
        protected ITrionic m_parent = null;

        // TODO: Worth implementing a setter/getter type thing for these?
        // Get data from read array and set data in send array
        public byte[] ReadData = new byte[4096];
        public byte[] DataToSend = new byte[4096];

        virtual public int TransferFrame(int BytesToSend)
        {
            return 0;
        }

        virtual public string TranslateErrorCode(byte p)
        {
            return "You should not see this message";
        }

        protected uint m_TesterId = 0;
        public uint TesterId
        {
            set { m_TesterId = value; }
            get { return m_TesterId; }
        }

        protected uint m_TargetId = 0;
        public uint TargetId
        {
            set { m_TargetId = value; }
            get { return m_TargetId; }
        }

        public GMSHARED(ref ITrionic parent)
        {
            m_parent = parent;
        }
        
        // Req 1a id
        // aka kwp2k readDataByLocalIdentifier
        public string ReadByIdentifier(byte id, out bool result)
        {
            string retstring = "";
            int retLen;

            result = false;
            DataToSend[0] = 0x1a;
            DataToSend[1] = id;

            if ((retLen = TransferFrame(2)) > 1)
            {
                // 0x5a id .. ..
                if (ReadData[0] == 0x5a && ReadData[1] == id)
                {
                    result = true;

                    if (retLen > 2)
                    {
                        // Strings are bloody evil! Never let them catch you off guard
                        try
                        {
                            retstring = Encoding.UTF8.GetString(ReadData, 2, (retLen - 2));
                        }

                        catch (Exception)
                        {
                            retstring = "";
                            result = false;
                            m_parent.CastInfoEvent("Received id string with non-ascii characters", ActivityType.TransferLayer);
                            // ITrionic.logger.Debug(ex, "String conversion exception: " + ex.Message);
                        }
                    }
                }
            }

            return retstring;
        }

        // Req 1a id
        // aka kwp2k readDataByLocalIdentifier
        public byte[] ReadDataByIdentifier(byte id)
        {
            int retLen;

            DataToSend[0] = 0x1a;
            DataToSend[1] = id;

            if ((retLen = TransferFrame(2)) > 1)
            {
                // 0x5a id .. ..
                if (ReadData[0] == 0x5a && ReadData[1] == id)
                {
                    if (retLen > 2)
                    {
                        byte[] retVal = new byte[retLen - 2];
                        for (int i = 0; i < (retLen - 2); i++)
                        {
                            retVal[i] = ReadData[2 + i];
                        }
                        return retVal;
                    }
                }
            }

            return null;
        }
        
        // Req 27
        // Sorry about this one. Some targets have more than one key mechanism for the same level depending on its operational state
        public bool SecurityAccess(ECU ecu, byte level = 1, int mode = 0)
        {
            int retLen;

            DataToSend[0] = 0x27;
            DataToSend[1] = level;

            // <27 level> --> <67 level>
            if ((retLen = TransferFrame(2)) < 2)
            {
                m_parent.CastInfoEvent("SecurityAccess: No or unexpected response", ActivityType.TransferLayer);
                return false;
            }

            if (ReadData[0] != 0x67)
            {
                if (ReadData[0] == 0x7f && ReadData[1] == 0x27)
                {
                    m_parent.CastInfoEvent("SecurityAccess failed due to: " + TranslateErrorCode(ReadData[2]), ActivityType.TransferLayer);
                }
                else
                {
                    m_parent.CastInfoEvent("SecurityAccess: Unknown error", ActivityType.TransferLayer);
                }

                return false;
            }
            else if (ReadData[1] != level)
            {
                m_parent.CastInfoEvent("SecurityAccess: Target did not respond as expected", ActivityType.TransferLayer);
                return false;
            }

            // A target might respond with all 00' to indicate that security access has already been granted
            // This will also return true if the target didn't send any additional seed bytes ..
            // .. (which might not be desired but that calls for custom code for that particular target)
            bool HasGranted = true;
            for (int i = 0; i < (retLen - 2); i++)
            {
                if (ReadData[2 + i] != 0)
                {
                    HasGranted = false;
                    break;
                }
            }

            if (HasGranted)
            {
                m_parent.CastInfoEvent("SecurityAccess has already been granted", ActivityType.TransferLayer);
                return true;
            }

            // This expects the previous check to return if no seed bytes were received!
            byte[] SeedBuffer = new byte[retLen - 2];
            for (int i = 0; i < (retLen - 2); i++)
            {
                SeedBuffer[i] = ReadData[2 + i];
            }

            SeedToKey s2k = new SeedToKey();
            byte[] KeyBuffer = s2k.CalculateKeyForTarget(SeedBuffer, ecu, level, mode);

            if (KeyBuffer == null || KeyBuffer.Length == 0 || KeyBuffer.Length > (4095 - 2))
            {
                m_parent.CastInfoEvent("SecurityAccess: Could not calculate key for target", ActivityType.TransferLayer);
                return false;
            }

            DataToSend[1] = (byte)(level + 1);

            for (int i = 0; i < KeyBuffer.Length; i++)
            {
                DataToSend[2 + i] = KeyBuffer[i];
            }

            // <27 level> --> <67 level>
            if (TransferFrame(2 + KeyBuffer.Length) < 2)
            {
                m_parent.CastInfoEvent("SecurityAccess: No or unexpected response", ActivityType.TransferLayer);
                return false;
            }

            if (ReadData[0] != 0x67)
            {
                if (ReadData[0] == 0x7f && ReadData[1] == 0x27)
                {
                    m_parent.CastInfoEvent("SecurityAccess failed due to: " + TranslateErrorCode(ReadData[2]), ActivityType.TransferLayer);
                }
                else
                {
                    m_parent.CastInfoEvent("SecurityAccess: Unknown error", ActivityType.TransferLayer);
                }

                return false;
            }
            else if (ReadData[1] != (level + 1))
            {
                m_parent.CastInfoEvent("SecurityAccess: Target did not respond as expected", ActivityType.TransferLayer);
                return false;
            }

            m_parent.CastInfoEvent("SecurityAccess granted", ActivityType.TransferLayer);

            return true;
        }

        // Req 3b id ..
        // aka kwp2k writeDataByLocalIdentifier
        public bool WriteDataByIdentifier(byte[] data, byte id, int len = -1)
        {
            int retLen;

            DataToSend[0] = 0x3b;
            DataToSend[1] = id;

            if (len > 0)
            {
                // Explicitly specified length of 1 or more
                if (len > (4095 - 2) || data == null || data.Length < len)
                {
                    return false;
                }

                for (int i = 0; i < len; i++)
                {
                    DataToSend[2 + i] = data[i];
                }

                retLen = TransferFrame(2 + len);
            }
            else
            {
                if (data != null && len != 0)
                {
                    // Retrieve length from data array if not explicit length of 0
                    if (data.Length > (4095 - 2))
                    {
                        return false;
                    }

                    for (int i = 0; i < data.Length; i++)
                    {
                        DataToSend[2 + i] = data[i];
                    }

                    retLen = TransferFrame(2 + data.Length);
                }
                else
                {
                    // Write to a ID with no data. (why??)
                    retLen = TransferFrame(2);
                }
            }

            return (retLen > 1 && ReadData[0] == 0x7b && ReadData[1] == id);
        }

        /// <summary>
        /// Simplified method for retrieval of target DIDs
        /// </summary>
        /// <param name="idlist"></param>
        public void ReadDIDList(ReadDIDInfo[] idlist)
        {
            if (idlist == null || idlist.Length == 0)
            {
                return;
            }

            foreach (ReadDIDInfo id in idlist)
            {
                string Row = id.Readable + ": ";
                byte[] tmp = ReadDataByIdentifier(id.DID);
                bool ConversionError = false;

                if (tmp == null)
                {
                    Row += "(Could not read)";
                }
                else
                {
                    string InfoString = "";

                    switch (id.Type)
                    {
                        case InfoType.InfoTypeString:
                            try
                            {
                                InfoString = Encoding.UTF8.GetString(tmp, 0, tmp.Length);
                            }

                            catch (Exception)
                            {
                                InfoString = "";
                                ConversionError = true;
                            }
                            break;
                        case InfoType.InfoTypeU32:
                            if ((tmp.Length & 3) != 0 || tmp.Length > 16)
                            {
                                // Too large or not in multiples of 4.
                                ConversionError = true;
                            }
                            else
                            {
                                int Len = tmp.Length;
                                int bufPtr = 0;

                                while (Len > 3)
                                {
                                    uint tempVal = (uint)(tmp[bufPtr] << 24 | tmp[bufPtr + 1] << 16 | tmp[bufPtr + 2] << 8 | tmp[bufPtr + 3]);

                                    // Odometer (this id is standardized)
                                    if (id.DID == 0xdf && tmp.Length == 4)
                                    {
                                        tempVal /= 64;
                                    }

                                    InfoString += tempVal.ToString("D");

                                    if (Len > 7)
                                    {
                                        InfoString += ", ";
                                    }

                                    bufPtr += 4;
                                    Len -= 4;
                                }
                            }
                            break;
                    }

                    if (ConversionError || id.Type == InfoType.InfoTypeArray)
                    {
                        for (int i = 0; i < tmp.Length; i++)
                        {
                            InfoString += tmp[i].ToString("X02");
                        }
                    }

                    Row += InfoString;
                }

                m_parent.CastInfoEvent(Row, ActivityType.QueryingECUTypeInfo);
            }
        }

        /// <summary>
        /// Simplified method for retrieval of _ALL_ target IDs
        /// </summary>
        public void TryAllIds()
        {
            for (int i = 0; i < 0x100; i++)
            {
                byte[] tmp = ReadDataByIdentifier((byte)i);

                if (tmp != null)
                {
                    string InfoString = "";
                    for (int d = 0; d < tmp.Length; d++)
                    {
                        InfoString += tmp[d].ToString("X02");
                    }

                    m_parent.CastInfoEvent(i.ToString("X02") + ": " + InfoString, ActivityType.QueryingECUTypeInfo);

                    InfoString = "";
                    try
                    {
                        InfoString = Encoding.UTF8.GetString(tmp, 0, tmp.Length);
                    }

                    catch (Exception)
                    {
                        InfoString = "";
                    }

                    m_parent.CastInfoEvent(i.ToString("X02") + ": " + InfoString, ActivityType.QueryingECUTypeInfo);
                }
            }
        }
    }
}