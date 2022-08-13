using System;
using System.ComponentModel;
using System.Windows.Forms;
using TrionicCANLib.API;
using Microsoft.Win32;
using NLog;
using System.Collections.Generic;
using System.Collections;
using FlasherSettings;

namespace TrionicCANFlasher
{
    public partial class frmSettings : Form
    {
        private Logger logger = LogManager.GetCurrentClassLogger();
        private m_adaptertype _m_adaptertype = new m_adaptertype();
        private m_interframe _m_interframe = new m_interframe();
        private m_adapter _m_adapter = new m_adapter();
        private m_selecu _m_selecu = new m_selecu();
        private m_baud _m_baud = new m_baud();

        // Default settings
        private bool m_fullscreen = false;
        private bool m_collapsed  = false;
        private int  m_width  = -1;
        private int  m_height = -1;

        private bool m_enablelog = true;  // "Enable logging"
        private bool m_onlypbus  = true;  // "Only P-Bus connection"
        private bool m_onbflash  = true;  // "Use flasher on device" (CombiAdapter)
        private bool m_uselegion = true;  // "Use Legion bootloader"
        private bool m_poweruser = false; // "I am a power user"
        private bool m_unlocksys = false; // "Unlock system partitions"
        private bool m_unlckboot = false; // "Unlock boot partition"
        private bool m_autocsum  = false; // "Auto update checksum"
        private bool m_remember  = false; // "Remember dimensions"

        // Hidden features
        private bool cbEnableSUFeatures = false; // This mode does not have a checkbox
        private int  m_hiddenclicks     = 5;     // Click this many times + 1 to enable su features
        private bool m_enablesufeatures = false; // enable / disable su features
        private bool m_verifychecksum   = true;  // Check checksum of file before flashing
        private bool m_uselastpointer   = true;  // Legion. Use the "last address of bin" feature or just regular partition md5
        private bool m_faster           = false; // Legion. Speed up certain tasks

        // Used to lock out SettingsLogic while populating items
        private bool m_lockout = true;

        public class m_interframe
        {
            private string m_name = "1200 (Default)";
            private int m_index = 9;
            private static uint[] m_dels = 
            {
                300, 400, 500, 600, 700,
                800, 900,1000,1100,1200, // (Default)
               1300,1400,1500,1600,1700,
               1800,1900,2000
            };

            public int Index
            {
                get { return m_index; }
                set { m_index = value; }
            }

            public string Name
            {
                get { return m_name; }
                set { m_name = value; }
            }

            public uint Value
            {
                get
                {
                    if (m_index >= 0 && m_index < 18)
                    {
                        return m_dels[m_index];
                    }
                    else
                    {
                        return 1200;
                    }
                }
            }
        }

        public class m_selecu
        {
            private string m_name = null;
            private int m_index = -1;

            public int Index
            {
                get { return m_index; }
                set { m_index = value; }
            }
            public string Name
            {
                get { return m_name; }
                set { m_name = value; }
            }
        }

        public class m_adaptertype
        {
            private string m_name = null;
            private int m_index = -1;

            public int Index
            {
                get { return m_index;  }
                set { m_index = value; }
            }
            public string Name
            {
                get { return m_name;  }
                set { m_name = value; }
            }
        }

        public class m_adapter
        {
            private string m_name = null;
            private int m_index = -1;

            public int Index
            {
                get { return m_index;  }
                set { m_index = value; }
            }
            public string Name
            {
                get { return m_name;  }
                set { m_name = value; }
            }
        }

        public class m_baud
        {
            private string m_name = null;
            private int m_index = -1;

            public int Index
            {
                get { return m_index;  }
                set { m_index = value; }
            }
            public string Name
            {
                get { return m_name;  }
                set { m_name = value; }
            }
        }

        public m_selecu SelectedECU
        {
            get { return _m_selecu;  }
            set { _m_selecu = value; }
        }

        public m_adaptertype AdapterType
        {
            get { return _m_adaptertype;  }
            set { _m_adaptertype = value; }
        }

        public m_adapter Adapter
        {
            get { return _m_adapter;  }
            set { _m_adapter = value; }
        }

        public m_baud Baudrate
        {
            get { return _m_baud;  }
            set { _m_baud = value; }
        }

        public m_interframe InterframeDelay
        {
            get { return _m_interframe;  }
            set { _m_interframe = value; }
        }

        public bool RememberDimensions
        {
            get { return m_remember; }
        }

        public bool VerifyChecksum
        {
            get { return m_verifychecksum; }
        }

        public bool Faster
        {
            get { return m_faster; }
        }

        public bool UseLastMarker
        {
            get { return m_uselastpointer; }
        }

        public int MainWidth
        {
            get { return m_width;  }
            set { m_width = value; }
        }

        public int MainHeight
        {
            get { return m_height;  }
            set { m_height = value; }
        }

        public bool Fullscreen
        {
            get { return m_fullscreen;  }
            set { m_fullscreen = value; }
        }

        public bool Collapsed
        {
            get { return m_collapsed; }
            set { m_collapsed = value; }
        }

        public bool CombiFlasher
        {
            get { return m_onbflash;  }
            set { m_onbflash = value; }
        }

        public bool OnlyPBus
        {
            get { return m_onlypbus;  }
            set { m_onlypbus = value; }
        }

        public bool EnableLogging
        {
            get { return m_enablelog;  }
            set { m_enablelog = value; }
        }

        public bool UseLegion
        {
            get { return m_uselegion;  }
            set { m_uselegion = value; }
        }

        public bool PowerUser
        {
            get { return m_poweruser;  }
            set { m_poweruser = value; }
        }

        public bool UnlockSys
        {
            get { return m_unlocksys;  }
            set { m_unlocksys = value; }
        }

        public bool UnlockBoot
        {
            get { return m_unlckboot;  }
            set { m_unlckboot = value; }
        }

        public bool AutoChecksum
        {
            get { return m_autocsum;  }
            set { m_autocsum = value; }
        }

        private void LoadItems()
        {
            // Lock out logics while populating items
            m_lockout = true;

            try
            {
                if (AdapterType.Name != null)
                {
                    cbxAdapterType.SelectedItem = AdapterType.Name;
                }

                if (Adapter.Name != null)
                {
                    cbxAdapterItem.SelectedItem = Adapter.Name;
                }

                if (Baudrate.Name != null)
                {
                    cbxComSpeed.SelectedItem = Baudrate.Name;
                }

                if (InterframeDelay.Name != null)
                {
                    cbxInterFrame.SelectedItem = InterframeDelay.Name;
                }
            }

            catch (Exception ex)
            {
                logger.Debug(ex.Message);
            }

            cbOnlyPBus.Checked = m_onlypbus;
            cbEnableLogging.Checked = m_enablelog;
            cbUseLegion.Checked = m_uselegion;
            cbOnboardFlasher.Checked = m_onbflash;

            cbPowerUser.Checked = m_poweruser;
            cbUnlockSys.Checked = m_unlocksys;
            cbUnlockBoot.Checked = m_unlckboot;
            cbAutoChecksum.Checked = m_autocsum;

            // This is not a real checkbox.
            cbEnableSUFeatures = m_enablesufeatures;

            cbUseLastPointer.Checked = m_uselastpointer;
            cbVerifyChecksum.Checked = m_verifychecksum;
            cbFaster.Checked = m_faster;

            cbRemember.Checked = m_remember;

            m_lockout = false;
        }

        private void StoreItems()
        {
            try
            {
                if (cbxAdapterType.SelectedIndex >= 0)
                {
                    AdapterType.Index = cbxAdapterType.SelectedIndex;
                    AdapterType.Name = cbxAdapterType.SelectedItem.ToString();
                }

                if (cbxAdapterItem.SelectedIndex >= 0)
                {
                    Adapter.Index = cbxAdapterItem.SelectedIndex;
                    Adapter.Name = cbxAdapterItem.SelectedItem.ToString();
                }

                if (cbxComSpeed.SelectedIndex >= 0)
                {
                    Baudrate.Index = cbxComSpeed.SelectedIndex;
                    Baudrate.Name = cbxComSpeed.SelectedItem.ToString();
                }

                if (cbxInterFrame.SelectedIndex >= 0)
                {
                    InterframeDelay.Index = cbxInterFrame.SelectedIndex;
                    InterframeDelay.Name = cbxInterFrame.SelectedItem.ToString();
                }
            }

            catch (Exception ex)
            {
                logger.Debug(ex.Message);
            }

            m_onlypbus = cbOnlyPBus.Checked;
            m_enablelog = cbEnableLogging.Checked;
            m_uselegion = cbUseLegion.Checked;
            m_onbflash = cbOnboardFlasher.Checked;

            m_poweruser = cbPowerUser.Checked;
            m_unlocksys = cbUnlockSys.Checked;
            m_unlckboot = cbUnlockBoot.Checked;
            m_autocsum = cbAutoChecksum.Checked;

            // This is not a real checkbox.
            m_enablesufeatures = cbEnableSUFeatures;

            m_uselastpointer = cbUseLastPointer.Checked;
            m_verifychecksum = cbVerifyChecksum.Checked;
            m_faster = cbFaster.Checked;

            m_remember = cbRemember.Checked;
        }

        public void LoadRegistrySettings()
        {
            // Fetch adapter types from TrionicCANLib.API
            cbxAdapterType.Items.Clear();

            foreach (var AdapterType in Enum.GetValues(typeof(CANBusAdapter)))
            {
                try
                {
                    cbxAdapterType.Items.Add(((DescriptionAttribute)AdapterType.GetType().GetField(AdapterType.ToString()).GetCustomAttributes(typeof(DescriptionAttribute), false)[0]).Description.ToString());
                }

                catch (Exception ex)
                {
                    logger.Debug(ex.Message);
                }
            }

            RegistryKey SoftwareKey = Registry.CurrentUser.CreateSubKey("Software");
            RegistryKey ManufacturerKey = SoftwareKey.CreateSubKey("MattiasC");

            using (RegistryKey Settings = ManufacturerKey.CreateSubKey("TrionicCANFlasher"))
            {
                if (Settings != null)
                {
                    string[] vals = Settings.GetValueNames();
                    foreach (string a in vals)
                    {
                        try
                        {
                            if (a == "AdapterType")
                            {
                                AdapterType.Name = Settings.GetValue(a).ToString();
                            }
                            else if (a == "Adapter")
                            {
                                Adapter.Name = Settings.GetValue(a).ToString();
                            }
                            else if (a == "ComSpeed")
                            {
                                Baudrate.Name = Settings.GetValue(a).ToString();
                            }
                            else if (a == "ECU")
                            {
                                SelectedECU.Name = Settings.GetValue(a).ToString();
                            }

                            else if (a == "EnableLogging")
                            {
                                m_enablelog = Convert.ToBoolean(Settings.GetValue(a).ToString());
                            }
                            else if (a == "OnboardFlasher")
                            {
                                m_onbflash = Convert.ToBoolean(Settings.GetValue(a).ToString());
                            }
                            else if (a == "OnlyPBus")
                            {
                                m_onlypbus = Convert.ToBoolean(Settings.GetValue(a).ToString());
                            }
                            else if (a == "UseLegionBootloader")
                            {
                                m_uselegion = Convert.ToBoolean(Settings.GetValue(a).ToString());
                            }

                            else if (a == "PowerUser")
                            {
                                m_poweruser = Convert.ToBoolean(Settings.GetValue(a).ToString());
                            }
                            else if (a == "FormatSystemPartitions")
                            {
                                m_unlocksys = Convert.ToBoolean(Settings.GetValue(a).ToString());
                            }
                            else if (a == "FormatBootPartition")
                            {
                                m_unlckboot = Convert.ToBoolean(Settings.GetValue(a).ToString());
                            }
                            else if (a == "AutoChecksum")
                            {
                                m_autocsum = Convert.ToBoolean(Settings.GetValue(a).ToString());
                            }

                            else if (a == "SuperUser")
                            {
                                m_enablesufeatures = Convert.ToBoolean(Settings.GetValue(a).ToString());
                            }
                            else if (a == "UseLastAddressPointer")
                            {
                                m_uselastpointer = Convert.ToBoolean(Settings.GetValue(a).ToString());
                            }

                            else if (a == "ViewRemember")
                            {
                                m_remember = Convert.ToBoolean(Settings.GetValue(a).ToString());
                            }
                            else if (a == "ViewWidth")
                            {
                                m_width = Convert.ToInt32(Settings.GetValue(a).ToString());
                            }
                            else if (a == "ViewHeight")
                            {
                                m_height = Convert.ToInt32(Settings.GetValue(a).ToString());
                            }
                            else if (a == "ViewFullscreen")
                            {
                                m_fullscreen = Convert.ToBoolean(Settings.GetValue(a).ToString());
                            }
                            else if (a == "ViewCollapsed")
                            {
                                m_collapsed = Convert.ToBoolean(Settings.GetValue(a).ToString());
                            }
                        }

                        catch (Exception ex)
                        {
                            logger.Debug(ex.Message);
                        }
                    }
                }
            }

            try
            {
                if (AdapterType.Name != null)
                {
                    cbxAdapterType.SelectedItem = AdapterType.Name;
                    AdapterType.Index = cbxAdapterType.SelectedIndex;
                }

                if (Adapter.Name != null)
                {
                    cbxAdapterItem.SelectedItem = Adapter.Name;
                    Adapter.Index = cbxAdapterItem.SelectedIndex;
                }

                if (Baudrate.Name != null)
                {
                    cbxComSpeed.SelectedItem = Baudrate.Name;
                    Baudrate.Index = cbxComSpeed.SelectedIndex;
                }
            }

            catch (Exception ex)
            {
                logger.Debug(ex.Message);
            }

            /////////////////////////////////////////////
            // Recover from strange settings in registry

            // Make sure settings are returned to safe values in case power user is not enabled
            if (!m_poweruser)
            {
                m_unlocksys = false;
                m_unlckboot = false;
                m_autocsum = false;

                m_enablesufeatures = false;
            }

            // We have to plan this section.. 
            if (!m_enablesufeatures)
            {
                m_verifychecksum = true;
                m_uselastpointer = true;
                m_faster  = false;
                InterframeDelay.Index = 9;
            }

            // Maybe we should have different unlock sys for ME9 and T8?
            if (!m_unlocksys)
            {
                m_unlckboot = false;
            }

            if ((m_fullscreen && m_collapsed) || !m_remember)
            {
                m_collapsed = false;
                m_fullscreen = false;
            }
        }

        private static void SaveRegistrySetting(string key, string value)
        {
            RegistryKey SoftwareKey = Registry.CurrentUser.CreateSubKey("Software");
            RegistryKey ManufacturerKey = SoftwareKey.CreateSubKey("MattiasC");
            using (RegistryKey saveSettings = ManufacturerKey.CreateSubKey("TrionicCANFlasher"))
            {
                saveSettings.SetValue(key, value);
            }
        }

        private static void SaveRegistrySetting(string key, bool value)
        {
            RegistryKey SoftwareKey = Registry.CurrentUser.CreateSubKey("Software");
            RegistryKey ManufacturerKey = SoftwareKey.CreateSubKey("MattiasC");
            using (RegistryKey saveSettings = ManufacturerKey.CreateSubKey("TrionicCANFlasher"))
            {
                saveSettings.SetValue(key, value);
            }
        }

        public void SaveRegistrySettings()
        {
            SaveRegistrySetting("AdapterType", AdapterType.Name != null ? AdapterType.Name : String.Empty);
            SaveRegistrySetting("Adapter", Adapter.Name != null ? Adapter.Name : String.Empty);
            SaveRegistrySetting("ComSpeed", Baudrate.Name != null ? Baudrate.Name : String.Empty);
            SaveRegistrySetting("ECU", SelectedECU.Name != null ? SelectedECU.Name : String.Empty);

            SaveRegistrySetting("EnableLogging", m_enablelog);
            SaveRegistrySetting("OnboardFlasher", m_onbflash);
            SaveRegistrySetting("OnlyPBus", m_onlypbus);
            SaveRegistrySetting("UseLegionBootloader", m_uselegion);

            SaveRegistrySetting("PowerUser", m_poweruser);
            SaveRegistrySetting("FormatSystemPartitions", m_unlocksys);
            SaveRegistrySetting("FormatBootPartition", m_unlckboot);
            SaveRegistrySetting("AutoChecksum", m_autocsum);

            SaveRegistrySetting("SuperUser", m_enablesufeatures);
            SaveRegistrySetting("UseLastAddressPointer", m_uselastpointer);

            SaveRegistrySetting("ViewRemember", m_remember);

            if (m_remember)
            {
                SaveRegistrySetting("ViewWidth", m_width.ToString());
                SaveRegistrySetting("ViewHeight", m_height.ToString());
                SaveRegistrySetting("ViewFullscreen", m_fullscreen);
                SaveRegistrySetting("ViewCollapsed", m_collapsed);
            }
        }

        private void GetAdapterInformation()
        {
            if (cbxAdapterType.SelectedIndex >= 0)
            {
                logger.Debug("ITrionic.GetAdapterNames selectedIndex=" + cbxAdapterType.SelectedIndex);
                string[] adapters = ITrionic.GetAdapterNames((CANBusAdapter)cbxAdapterType.SelectedIndex);
                cbxAdapterItem.Items.Clear();
                foreach (string adapter in adapters)
                {
                    cbxAdapterItem.Items.Add(adapter);
                    logger.Debug("Adaptername=" + adapter);
                }

                try
                {
                    if (adapters.Length > 0)
                        cbxAdapterItem.SelectedIndex = 0;
                }
                catch (Exception ex)
                {
                    logger.Debug(ex.Message);
                }
            }
        }

        private int m_ecuindex = -1;

        /// <summary>
        /// This method determines what should be enabled / shown depending on what the user has selected
        /// </summary>
        private void SettingsLogic()
        {
            // Check if we're being populated or if the user did something
            if (!m_lockout)
            {
                int typeindex = cbxAdapterType.SelectedIndex;

                cbOnboardFlasher.Enabled = false;
                cbEnableLogging.Enabled = true;
                cbUseLegion.Enabled = false;
                cbOnlyPBus.Enabled = true;
                cbPowerUser.Enabled = true;
                cbUnlockSys.Enabled = false;
                cbUnlockBoot.Enabled = false;
                cbAutoChecksum.Enabled = false;

                if (cbEnableSUFeatures && cbPowerUser.Checked)
                {
                    cbPowerUser.Text = "I definitely know what I am doing";
                }
                else
                {
                    cbPowerUser.Text = "I know what I am doing";
                }

                cbxAdapterItem.Enabled = false;
                AdapterLabel.Enabled = false;

                if (typeindex == (int)CANBusAdapter.LAWICEL ||
                    typeindex == (int)CANBusAdapter.KVASER  ||
                    typeindex == (int)CANBusAdapter.J2534)
                {
                    cbxAdapterItem.Enabled = true;
                    AdapterLabel.Enabled = true;
                }

                if (typeindex == (int)CANBusAdapter.ELM327)
                {
                    cbxAdapterItem.Enabled = true;
                    AdapterLabel.Enabled = true;
                    ComBaudLabel.Enabled = true;
                    cbxComSpeed.Enabled = true;
                }
                else
                {
                    ComBaudLabel.Enabled = false;
                    cbxComSpeed.Enabled = false;
                }

                if (typeindex >= 0)
                {
                    if (m_ecuindex == (int)ECU.TRIONIC5)
                    {
                        cbOnlyPBus.Enabled = false;
                        cbAutoChecksum.Enabled = true;
                    }

                    else if (m_ecuindex == (int)ECU.TRIONIC7)
                    {
                        cbOnboardFlasher.Enabled = typeindex == (int)CANBusAdapter.COMBI ? cbOnlyPBus.Checked : false;
                        cbAutoChecksum.Enabled = true;
                    }

                    else if (m_ecuindex == (int)ECU.TRIONIC8)
                    {
                        cbUseLegion.Enabled = true;
                        cbUnlockSys.Enabled = cbUseLegion.Checked;
                        cbUnlockBoot.Enabled = (cbUnlockSys.Checked && cbUnlockSys.Enabled);

                        if (!cbUnlockSys.Checked)
                        {
                            cbUnlockBoot.Checked = false;
                        }
                        cbAutoChecksum.Enabled = true;
                    }

                    else if (m_ecuindex == (int)ECU.TRIONIC8_MCP)
                    {
                        cbUseLegion.Checked = true;
                        cbUnlockBoot.Enabled = true;
                    }

                    else if (m_ecuindex == (int)ECU.MOTRONIC96)
                    {
                        cbUnlockSys.Enabled = true;
                    }

                    else if (m_ecuindex == (int)ECU.DELCOE39)
                    {
                        cbUnlockSys.Enabled = true;
                        cbUnlockBoot.Enabled = (cbUnlockSys.Checked && cbUnlockSys.Enabled);

                        if (!cbUnlockSys.Checked)
                        {
                            cbUnlockBoot.Checked = false;
                        }
                    }

                    else if (m_ecuindex == (int)ECU.DELCOE78)
                    {
                        // No options atm..
                    }

                    // Other ECUs do not have power user features
                    else
                    {
                        cbPowerUser.Enabled = cbEnableSUFeatures;
                    }

                    if (!cbPowerUser.Enabled)
                    {
                        cbUnlockSys.Enabled = false;
                        cbUnlockBoot.Enabled = false;
                        cbAutoChecksum.Enabled = false;
                    }

                    if (!cbPowerUser.Checked)
                    {
                        cbUnlockSys.Enabled = false;
                        cbUnlockBoot.Enabled = false;
                        cbAutoChecksum.Enabled = false;
                        cbUnlockSys.Checked = false;
                        cbUnlockBoot.Checked = false;
                        cbAutoChecksum.Checked = false;

                        // Can not be super user without being a power user first
                        cbEnableSUFeatures = false;

                        // Restore safe settings
                        cbUnlockBoot.Checked = false;
                        cbUnlockSys.Checked = false;
                        cbAutoChecksum.Checked = false;
                    }

                    if (cbEnableSUFeatures)
                    {
                        bool precheck = ((m_ecuindex == (int)ECU.TRIONIC8 && cbUseLegion.Checked && cbUseLegion.Enabled) ||
                            m_ecuindex == (int)ECU.TRIONIC8_MCP || m_ecuindex == (int)ECU.Z22SEMain_LEG || m_ecuindex == (int)ECU.Z22SEMCP_LEG ||
                            m_ecuindex == (int)ECU.DELCOE39     || m_ecuindex == (int)ECU.DELCOE78);

                        InterframeLabel.Enabled = precheck;
                        cbxInterFrame.Enabled = precheck;
                        cbUseLastPointer.Enabled = (m_ecuindex == (int)ECU.TRIONIC8 && cbUseLegion.Checked && cbUseLegion.Enabled);
                        cbFaster.Enabled = precheck;
                        cbVerifyChecksum.Enabled = (m_ecuindex == (int)ECU.TRIONIC8 || m_ecuindex == (int)ECU.TRIONIC7 || m_ecuindex == (int)ECU.TRIONIC5);
                    }
                    else
                    {
                        InterframeLabel.Enabled = false;
                        cbUseLastPointer.Enabled = false;
                        cbFaster.Enabled = false;
                        cbxInterFrame.Enabled = false;
                        cbVerifyChecksum.Enabled = false;

                        // Restore safe settings
                        cbFaster.Checked = false;
                        cbUseLastPointer.Checked = true;
                        cbVerifyChecksum.Checked = true;
                        cbxInterFrame.SelectedIndex = 9;
                    }
                }

                // No adapter selected, blank everything!
                else
                {
                    cbPowerUser.Enabled = false;
                    cbEnableLogging.Enabled = false;
                    cbAutoChecksum.Enabled = false;
                    cbOnlyPBus.Enabled = false;
                }
            }
        }

        private void LoadHandler()
        {
            // Reset click counter
            if (m_hiddenclicks > 0)
            {
                m_hiddenclicks = 5;
            }

            // Restore the regular label
            label2.Text = "Advanced features";

            LoadItems();
            SettingsLogic();
        }

        private class CustomSettings
        {
            public CustomSettings(ITrionic Instance, ECU ecu, System.Drawing.Size origsize)
            {
                m_ecu = ecu;
                m_instance = Instance;
                Manager = new SettingsManager(Instance, Instance.GetSettings(ecu));
                Settings = Manager.GetLocalCopy();
                OriginalWindowSize = origsize;
            }

            public void AddBool(int index, CheckBox box)
            {
                BoolChanged boolhandle = new BoolChanged(index, this);
                box.CheckedChanged += boolhandle.EventHandler;
                BoolHandlers.Add(boolhandle);
                AddedItems.Add(box);
            }

            public void AddInt(int index, NumericUpDown scroll)
            {
                IntChanged inthandle = new IntChanged(index, this);
                scroll.ValueChanged += inthandle.EventHandler;
                ScrollHandlers.Add(inthandle);
                AddedItems.Add(scroll);
            }

            public void AddIndex(int index, ComboBox scroll)
            {
                IndexChanged indexhandle = new IndexChanged(index, this);
                scroll.SelectedIndexChanged += indexhandle.EventHandler;
                IndexHandlers.Add(indexhandle);
                AddedItems.Add(scroll);
            }

            public void AddLabel(Label lab)
            {
                LabelItems.Add(lab);
            }

            public void ClearVisibleElements(Control.ControlCollection ctrl)
            {
                foreach (Control itm in AddedItems)
                {
                    ctrl.Remove(itm);
                }
                AddedItems.Clear();

                foreach (Control itm in LabelItems)
                {
                    ctrl.Remove(itm);
                }
                LabelItems.Clear();
            }

            public void PerformTargetLogic()
            {
                if (IgnoreLogic == false)
                {
                    m_instance.TargetSettingsLogic(m_ecu, ref Manager);

                    for (int i = 0; i < AddedItems.Count && i < Settings.Count; i++)
                    {
                        AddedItems[i].Enabled = Settings[i].Enabled;
                    }
                }
            }

            private class BoolChanged
            {
                public BoolChanged(int itemindex, CustomSettings settings)
                {
                    Index = itemindex;
                    Settings = settings;
                }

                public void EventHandler(object sender, EventArgs e)
                {
                    CheckBox cbx = (CheckBox)sender;
                    Settings.Settings[Index].Value = (bool)cbx.Checked;
                    Settings.PerformTargetLogic();
                }

                private int Index;
                private CustomSettings Settings;
            }

            private class IndexChanged
            {
                public IndexChanged(int itemindex, CustomSettings settings)
                {
                    Index = itemindex;
                    Settings = settings;
                }

                public void EventHandler(object sender, EventArgs e)
                {
                    ComboBox combo = (ComboBox)sender;
                    Settings.Settings[Index].Value = (int)combo.SelectedIndex;
                    Settings.PerformTargetLogic();
                }

                private int Index;
                private CustomSettings Settings;
            }

            private class IntChanged
            {
                public IntChanged(int itemindex, CustomSettings settings)
                {
                    Index = itemindex;
                    Settings = settings;
                }

                public void EventHandler(object sender, EventArgs e)
                {
                    NumericUpDown scroll = (NumericUpDown)sender;
                    Settings.Settings[Index].Value = (int)scroll.Value;
                    Settings.PerformTargetLogic();
                }

                private int Index;
                private CustomSettings Settings;
            }

            private List<BoolChanged> BoolHandlers = new List<BoolChanged>();
            private List<IntChanged> ScrollHandlers = new List<IntChanged>();
            private List<IndexChanged> IndexHandlers = new List<IndexChanged>();
            private ITrionic m_instance;
            private ECU m_ecu;

            public readonly List<SettingCopy> Settings;
            private readonly List<Control> AddedItems = new List<Control>();
            public readonly List<Control> LabelItems = new List<Control>();
            public readonly System.Drawing.Size OriginalWindowSize;

            public bool IgnoreLogic = true;
            public SettingsManager Manager;
        }

        private CustomSettings m_custSet = null;

        private void RemoveCustomItems()
        {
            if (m_custSet != null)
            {
                this.ClientSize = m_custSet.OriginalWindowSize;
                m_custSet.ClearVisibleElements(this.Controls);
            }

            m_custSet = null;
        }

        public void PopulateItems(ITrionic Instance, ECU ecu)
        {
            m_ecuindex = (int)ecu;

            RemoveCustomItems();

            LoadHandler();

            int XSize = this.ClientSize.Width;
            int YSize = this.ClientSize.Height;

            m_custSet = new CustomSettings(Instance, ecu, this.ClientSize);
            m_custSet.IgnoreLogic = true;

            if (YSize > 64 && XSize > 100)
            {
                SettingsManager setmgr = m_custSet.Manager;
                List<SettingCopy> sets = m_custSet.Settings;
                
                // Dimension of button
                // xx, 44;
                // Dimension of checkbox (Vertical spacing: 23)
                // xx, 17
                // Dimension of label
                // xx, 13
                // Dimension of Drop-down
                // xx, 21

                // Tab index stuff??
                // Label
                // CheckBox
                // ComboBox
                // NumericUpDown

                // Even items are left-aligned, odd centre-right-aligned
                bool even = true;

                for (int i = 0; i < sets.Count; i++)
                {
                    Type type = sets[i].Type;
                    Object value = sets[i].Value;
                    Object[] listvals = sets[i].ListValues;

                    // Look ahead and push down if there's a list present
                    if (even)
                    {
                        if ((i + 1) < sets.Count)
                        {
                            Type nexttype = sets[i+1].Type;
                            Object[] nextlist = sets[i+1].ListValues;

                            if ((type == typeof(int) && listvals != null) ||
                                (nexttype == typeof(int) && nextlist != null))
                            {
                                YSize += 40;
                            }
                        }
                        else
                        {
                            if (type == typeof(int) && listvals != null)
                            {
                                YSize += 40;
                            }
                        }
                    }

                    if (value != null)
                    {
                        if (type == typeof(int))
                        {
                            Label lbl = new Label();
                            lbl.AutoSize = true;
                            // lbl.Location = new System.Drawing.Point(191, 49);
                            lbl.Name = "lbl_" + sets[i].DisplayName;
                            lbl.Size = new System.Drawing.Size((XSize / 2) - 20, 13);
                            // lbl.TabIndex = 82;
                            lbl.Text = sets[i].DisplayName;
                            lbl.Left = (even) ? 9 : (13 + (XSize / 2));
                            lbl.Top = YSize - 109;

                            m_custSet.AddLabel(lbl);
                            this.Controls.Add(lbl);

                            if (listvals == null)
                            {
                                // No list of values so.. Numero up down it is
                                NumericUpDown Numero = new NumericUpDown();
                                Numero.AutoSize = true;
                                Numero.ForeColor = System.Drawing.Color.Black;
                                Numero.Name = "upd_" + sets[i].DisplayName + i.ToString("D");
                                Numero.Text = sets[i].DisplayName;
                                Numero.Size = new System.Drawing.Size((XSize / 2) - 20, 17);
                                Numero.Left = (even) ? 12 : (16 + (XSize / 2));
                                Numero.Top = YSize - 95; // 90
                                Numero.Maximum = Int32.MaxValue;
                                Numero.Minimum = Int32.MinValue;
                                Numero.Value = (int)value;

                                m_custSet.AddInt(i, Numero);
                                this.Controls.Add(Numero);
                            }
                            else
                            {
                                // There's a list so make it a dropdown type
                                ComboBox ddown = new ComboBox();
                                ddown.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
                                ddown.FormattingEnabled = true;
                                // ddown.Location = new System.Drawing.Point(194, 65);
                                // ddown.TabIndex = 81;
                                ddown.Items.AddRange(listvals);
                                ddown.Name = "drp_" + sets[i].DisplayName + i.ToString("D");
                                ddown.Size = new System.Drawing.Size(150, 21);
                                ddown.SelectedIndex = (int)value;
                                ddown.Left = (even) ? 12 : (16 + (XSize / 2));
                                ddown.Top = YSize - 95; // 90

                                m_custSet.AddIndex(i, ddown);
                                this.Controls.Add(ddown);
                            }

                            even = !even;
                        }
                        else if (type == typeof(bool))
                        {
                            CheckBox cbox = new CheckBox();
                            cbox.AutoSize = true;
                            cbox.ForeColor = System.Drawing.Color.Black;
                            cbox.Name = "cbx_" + sets[i].DisplayName + i.ToString("D");
                            cbox.Text = sets[i].DisplayName;
                            cbox.Size = new System.Drawing.Size((XSize / 2) - 20, 17);
                            cbox.Checked = (bool)value;

                            if (even)
                            {
                                cbox.Left = 12;
                                YSize += 23;
                            }
                            else
                            {
                                cbox.Left = 16 + (XSize / 2);
                            }

                            cbox.Top = YSize - 90;
                            even = !even;

                            m_custSet.AddBool(i, cbox);
                            this.Controls.Add(cbox);
                        }
                    }
                }

                this.ClientSize = new System.Drawing.Size(XSize, YSize);
            }

            m_custSet.IgnoreLogic = false;
            m_custSet.PerformTargetLogic();
        }

        /// <summary>
        /// Dialog has been shown. Now populate it with current settings
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DialogShown(object sender, EventArgs e)
        {
            // LoadHandler();
        }

        private void cbxAdapterType_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cbxAdapterType.SelectedIndex == (int)CANBusAdapter.JUST4TRIONIC)
            {
                cbxComSpeed.SelectedIndex = (int)ComSpeed.S115200;
            }

            // Prevent checkboxes from popping in and out as the adapter name is loaded
            SettingsLogic();

            GetAdapterInformation();

            // Now perform a real check
            SettingsLogic();
        }

        private void cbOnlyPBus_Checkchanged(object sender, EventArgs e)
        {
            SettingsLogic();
        }

        private void cbUseLegion_CheckedChanged(object sender, EventArgs e)
        {
            SettingsLogic();
        }

        private void cbUnlockSys_CheckedChanged(object sender, EventArgs e)
        {
            SettingsLogic();
        }

        private void cbPowerUser_CheckedChanged(object sender, EventArgs e)
        {
            // Ignore changes made by "LoadItems"
            if (!m_lockout)
            {
                if (cbEnableSUFeatures && !cbPowerUser.Checked)
                {
                    label2.Text = "Advanced features";
                    cbPowerUser.Checked = true;
                    cbEnableSUFeatures = false;
                    m_hiddenclicks = 5;
                }

                SettingsLogic();
            }
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            StoreItems();

            if (m_custSet != null)
            {
                m_custSet.Manager.StoreLocalCopy();
            }

            this.Close();
        }

        /// <summary>
        /// Enable super user options by clicking "Advanced features" 6 times
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void label2_Click(object sender, EventArgs e)
        {
            if (!cbEnableSUFeatures && cbPowerUser.Checked)
            {
                if (m_hiddenclicks > 0)
                {
                    m_hiddenclicks--;
                }
                else
                {
                    label2.Text = "You are in deep water now..";
                    cbEnableSUFeatures = true;
                    m_hiddenclicks = 5;
                    SettingsLogic();  
                }
            }

            // Feature is already enabled
            else if (cbPowerUser.Checked)
            {
                if (m_hiddenclicks > 0)
                {
                    m_hiddenclicks--;
                }
                else
                {
                    m_hiddenclicks = 5;
                    label2.Text = "Already a super user";
                }
            }
        }

        public frmSettings()
        {
            InitializeComponent();
        }
    }
}
