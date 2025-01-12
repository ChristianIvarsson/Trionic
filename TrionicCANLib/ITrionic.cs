﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using TrionicCANLib.CAN;
using NLog;
using System.ComponentModel;
using FlasherSettings;

namespace TrionicCANLib.API
{
    public class WorkerArgument
    {
        public string FileName = "";
        public ECU ecu = (ECU)(int)-1;
    }

    public class TargetFeatures
    {
        virtual public bool FlashFull { get { return false; } }
        virtual public bool FlashCalib { get { return false; } }
        virtual public bool ReadFull { get { return false; } }
        virtual public bool ReadCalib { get { return false; } }
        virtual public bool ReadSram { get { return false; } }
        virtual public bool FirmwareInfo { get { return false; } }
        virtual public bool TroubleCodes { get { return false; } }
        virtual public bool Recover { get { return false; } }
        virtual public bool Restore { get { return false; } }
    }
    
    abstract public class ITrionic
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();
        internal ICANDevice canUsbDevice = null;
        internal CANListener canListener = null;
        private CANListener m_canLogListener = null;

        public delegate void WriteProgress(object sender, WriteProgressEventArgs e);
        public event ITrionic.WriteProgress onWriteProgress;

        public delegate void ReadProgress(object sender, ReadProgressEventArgs e);
        public event ITrionic.ReadProgress onReadProgress;

        public delegate void CanInfo(object sender, CanInfoEventArgs e);
        public event ITrionic.CanInfo onCanInfo;

        public delegate void CanFrame(object sender, CanFrameEventArgs e);
        public event ITrionic.CanFrame onCanFrame;

        // implements functions for canbus access for Trionic 8
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        public static extern uint MM_BeginPeriod(uint uMilliseconds);
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        public static extern uint MM_EndPeriod(uint uMilliseconds);

        protected int m_sleepTime = (int)SleepTime.Default;

        public SleepTime Sleeptime
        {
            get { return (SleepTime)m_sleepTime; }
            set { m_sleepTime = (int)value; }
        }

        protected int m_forcedBaudrate = 0;
        public int ForcedBaudrate
        {
            get
            {
                return m_forcedBaudrate;
            }
            set
            {
                m_forcedBaudrate = value;
            }
        }

        protected bool m_filterBypass = false;
        public bool bypassCANfilters
        {
            get
            {
                return m_filterBypass;
            }
            set
            {
                m_filterBypass = value;
            }
        }

        protected ECU m_ECU = ECU.TRIONIC8;

        public ECU ECU
        {
            get { return m_ECU; }
            set { m_ECU = value; }
        }

        public bool isOpen()
        {
            if (canUsbDevice != null)
            {
                return canUsbDevice.isOpen();
            }
            return false;
        }

        protected bool m_OnlyPBus = false;

        public bool OnlyPBus
        {
            get { return m_OnlyPBus; }
            set { m_OnlyPBus = value; }
        }

        protected Latency m_Latency = Latency.Default;

        public Latency Latency
        {
            get { return m_Latency; }
            set { m_Latency = value; }
        }

        abstract public void setCANDevice(CANBusAdapter adapterType);

        abstract public void Cleanup();

        public float GetADCValue(uint channel)
        {
            return canUsbDevice.GetADCValue(channel);
        }

        public float GetThermoValue()
        {
            return canUsbDevice.GetThermoValue();
        }

        public static string[] GetAdapterNames(CANBusAdapter adapterType)
        {
            try
            {
                if (adapterType == CANBusAdapter.LAWICEL)
                {
                    return CANUSBDevice.GetAdapterNames();
                }
                else if (adapterType == CANBusAdapter.ELM327)
                {
                    return CANELM327Device.GetAdapterNames();
                }
                else if (adapterType == CANBusAdapter.JUST4TRIONIC)
                {
                    return Just4TrionicDevice.GetAdapterNames();
                }
                else if (adapterType == CANBusAdapter.KVASER)
                {
                    return KvaserCANDevice.GetAdapterNames();
                }
                else if (adapterType == CANBusAdapter.J2534)
                {
                    return J2534CANDevice.GetAdapterNames();
                }
            }
            catch(Exception ex)
            {
                logger.Debug(ex, "Failed to get adapternames");
            }
            return new string[0];
        }

        abstract public void SetSelectedAdapter(string adapter);

        protected void CastProgressWriteEvent(int percentage)
        {
            if (onWriteProgress != null)
            {
                onWriteProgress(this, new WriteProgressEventArgs(percentage));
            }
        }

        protected void CastProgressReadEvent(int percentage)
        {
            if (onReadProgress != null)
            {
                onReadProgress(this, new ReadProgressEventArgs(percentage));
            }
        }

        public void CastInfoEvent(string info, ActivityType type)
        {
            logger.Debug(info);
            if (onCanInfo != null)
            {
                onCanInfo(this, new CanInfoEventArgs(info, type));
            }
        }

        protected void CastFrameEvent(CANMessage message)
        {
            logger.Debug(message);
            if (onCanFrame != null)
            {
                onCanFrame(this, new CanFrameEventArgs(message));
            }
        }

        public class CanInfoEventArgs : System.EventArgs
        {
            private ActivityType _type;

            public ActivityType Type
            {
                get { return _type; }
                set { _type = value; }
            }

            private string _info;

            public string Info
            {
                get { return _info; }
                set { _info = value; }
            }

            public CanInfoEventArgs(string info, ActivityType type)
            {
                _info = info;
                _type = type;
            }
        }

        public class CanFrameEventArgs : System.EventArgs
        {
            private CANMessage _message;

            public CANMessage Message
            {
                get { return _message; }
                set { _message = value; }
            }

            public CanFrameEventArgs(CANMessage message)
            {
                _message = message;
            }
        }

        public class WriteProgressEventArgs : System.EventArgs
        {
            private int _percentage;

            private int _bytestowrite;

            public int Bytestowrite
            {
                get { return _bytestowrite; }
                set { _bytestowrite = value; }
            }

            private int _byteswritten;

            public int Byteswritten
            {
                get { return _byteswritten; }
                set { _byteswritten = value; }
            }

            public int Percentage
            {
                get { return _percentage; }
                set { _percentage = value; }
            }

            public WriteProgressEventArgs(int percentage)
            {
                _percentage = percentage;
            }

            public WriteProgressEventArgs(int percentage, int bytestowrite, int byteswritten)
            {
                _bytestowrite = bytestowrite;
                _byteswritten = byteswritten;
                _percentage = percentage;
            }
        }

        public class ReadProgressEventArgs : System.EventArgs
        {
            private int _percentage;

            public int Percentage
            {
                get { return _percentage; }
                set { _percentage = value; }
            }

            public ReadProgressEventArgs(int percentage)
            {
                _percentage = percentage;
            }
        }

        public void SetCANFilterIds(List<uint> list)
        {
            canUsbDevice.AcceptOnlyMessageIds = list;
        }

        public void LogCANData(object sender, DoWorkEventArgs workEvent)
        {
            BackgroundWorker bw = sender as BackgroundWorker;

            if (!canUsbDevice.isOpen()) return;

            if (m_canLogListener == null)
            {
                m_canLogListener = new CANListener();
            }
            canUsbDevice.AcceptOnlyMessageIds = null;
            canUsbDevice.addListener(m_canLogListener);

            while (true)
            {
                m_canLogListener.waitMessage(1000);

                if (bw.CancellationPending)
                {
                    canUsbDevice.removeListener(m_canLogListener);
                    m_canLogListener = null;
                    workEvent.Cancel = true;
                    return;
                }
            }
        }

        private LegionParameters _legopt = new LegionParameters();

        public LegionParameters LegionOptions
        {
            get { return _legopt;  }
            set { _legopt = value; }
        }

        public class LegionParameters
        {
            private uint m_interframe = 1200;
            private bool m_faster     = false;
            private bool m_lastmarker = true;

            public bool Faster
            {
                get { return m_faster;  }
                set { m_faster = value; }
            }

            public bool UseLastMarker
            {
                get { return m_lastmarker;  }
                set { m_lastmarker = value; }
            }

            public uint InterframeDelay
            {
                get { return m_interframe;  }
                set { m_interframe = value; }
            }
        }

        public struct dynAddrHelper
        {
            public byte size;
            public int address;
        };

        public virtual bool FormatBootPartition
        {
            get { return false; }
            set { }
        }

        public virtual bool FormatSystemPartitions
        {
            get { return false; }
            set { }
        }

        public virtual bool openDevice(bool requestSecurityAccess)
        {
            return false;
        }

        //////////////////////////
        // Test code

        public virtual void ReadFlash(object sender, DoWorkEventArgs workEvent)
        {
            workEvent.Result = false;
        }

        public virtual void ReadCal(object sender, DoWorkEventArgs workEvent)
        {
            workEvent.Result = false;
        }

        public virtual void WriteFlash(object sender, DoWorkEventArgs workEvent)
        {
            workEvent.Result = false;
        }

        public virtual void WriteCal(object sender, DoWorkEventArgs workEvent)
        {
            workEvent.Result = false;
        }

        public virtual void ReadSram(object sender, DoWorkEventArgs workEvent)
        {
            workEvent.Result = false;
        }

        public virtual void GetFirmwareInfo(object sender, DoWorkEventArgs workEvent)
        {
            workEvent.Result = false;
        }

        public virtual void ReadTroubleCodes(object sender, DoWorkEventArgs workEvent)
        {
            workEvent.Result = false;
        }

        public SettingProperties BaseSettings = new SettingProperties();

        public virtual ref SettingProperties GetSettings(ECU ecu)
        {
            return ref BaseSettings;
        }

        public TargetFeatures BaseFeatures = new TargetFeatures();

        public virtual ref TargetFeatures GetFeatures(ECU ecu)
        {
            return ref BaseFeatures;
        }

        public virtual void TargetSettingsLogic(ECU ecu, ref SettingsManager manager)
        {
        }
    }
}
