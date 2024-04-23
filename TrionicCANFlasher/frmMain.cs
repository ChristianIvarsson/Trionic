using System;
using System.Windows.Forms;
using System.IO;
using System.Threading;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using TrionicCANLib;
using TrionicCANLib.API;
using TrionicCANLib.Firmware;
using TrionicCANLib.Checksum;
using System.Drawing;
using NLog;
using CommonSuite;
using System.Text;
using System.Collections.Generic;
using FlasherSettings;

namespace TrionicCANFlasher
{
    public delegate void DelegateUpdateStatus(ITrionic.CanInfoEventArgs e);
    public delegate void DelegateProgressStatus(int percentage);

    public partial class frmMain : Form
    {
        // readonly Trionic8 trionic8 = new Trionic8();
        // readonly Trionic7 trionic7 = new Trionic7();
        // readonly Trionic5 trionic5 = new Trionic5();
        readonly DelcoE39 delcoe39 = new DelcoE39();
        readonly frmSettings AppSettings = new frmSettings();

        private class ECUDesc
        {
            public ITrionic Target = null;
            public ECU ecu = (ECU)(int)-1;
            public string Name = "";
        }

        private ECUDesc[] EcuTargets = null;
        private bool TargetBusy = false;

        DateTime dtstart;
        public DelegateUpdateStatus m_DelegateUpdateStatus;
        public DelegateProgressStatus m_DelegateProgressStatus;
        public ChecksumDelegate.ChecksumUpdate m_ShouldUpdateChecksum;
        private readonly Logger logger = LogManager.GetCurrentClassLogger();

        msiupdater m_msiUpdater;
        BackgroundWorker bgworkerLogCanData;
        private bool m_bypassCANfilters = false;
        private FormWindowState LastWindowState = FormWindowState.Normal;

        public frmMain()
        {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            Application.ThreadException += Application_ThreadException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            InitializeComponent();
            m_DelegateUpdateStatus = updateStatusInBox;
            m_DelegateProgressStatus = updateProgress;
            m_ShouldUpdateChecksum = ShouldUpdateChecksum;
            SetupListboxWrapping();
            EnableUserInput(true);
        }

        // A necessary evil to get rid of if/else if in button methods
        private void GenerateTargetList()
        {
            EcuTargets = new ECUDesc[]
            {
                new ECUDesc { Target = delcoe39  , ecu = ECU.DELCOE39     , Name = "ACDelco E39" },
                new ECUDesc { Target = delcoe39  , ecu = ECU.DELCOE39_BAM , Name = "ACDelco E39 BAM recovery" },
                new ECUDesc { Target = delcoe39  , ecu = ECU.DELCOE78     , Name = "ACDelco E78" },
            };
        }

        private void frmMain_Load(object sender, EventArgs e)
        {
            Text = "TrionicCANFlasher v" + System.Windows.Forms.Application.ProductVersion;
            logger.Trace(Text);
            logger.Trace(".dot net CLR " + System.Environment.Version);

            // get additional info from registry if available
            AppSettings.LoadRegistrySettings();
            CheckRegistryFTDI();

            GenerateTargetList();

            cbxEcuType.Items.Clear();

            foreach (ECUDesc Target in EcuTargets)
            {
                try
                {
                    cbxEcuType.Items.Add(Target.Name);
                }
                catch (Exception ex)
                {
                    logger.Debug(ex.Message);
                }
            }

            // Fetch last selected ECU from registry and pass its index back to AppSettings
            if (AppSettings.SelectedECU.Name != null)
            {
                try
                {
                    cbxEcuType.SelectedItem = AppSettings.SelectedECU.Name;
                    AppSettings.SelectedECU.Index = cbxEcuType.SelectedIndex;
                }

                catch (Exception ex)
                {
                    AddLogItem(ex.Message);
                }
            }

            // trionic5.onReadProgress += trionicCan_onReadProgress;
            // trionic5.onWriteProgress += trionicCan_onWriteProgress;
            // trionic5.onCanInfo += trionicCan_onCanInfo;

            // trionic7.onReadProgress += trionicCan_onReadProgress;
            // trionic7.onWriteProgress += trionicCan_onWriteProgress;
            // trionic7.onCanInfo += trionicCan_onCanInfo;

            // trionic8.onReadProgress += trionicCan_onReadProgress;
            // trionic8.onWriteProgress += trionicCan_onWriteProgress;
            // trionic8.onCanInfo += trionicCan_onCanInfo;

            delcoe39.onReadProgress += trionicCan_onReadProgress;
            delcoe39.onWriteProgress += trionicCan_onWriteProgress;
            delcoe39.onCanInfo += trionicCan_onCanInfo;

            RestoreView();
            UpdateLogManager();
            EnableUserInput(true);
        }

        private void frmMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            AppSettings.SaveRegistrySettings();
            // trionic8.Cleanup();
            // trionic7.Cleanup();
            // trionic5.Cleanup();
            delcoe39.Cleanup();
        }

        private void SetupListboxWrapping()
        {
            listBoxLog.DrawMode = System.Windows.Forms.DrawMode.OwnerDrawVariable;
            listBoxLog.MeasureItem += lst_MeasureItem;
            listBoxLog.DrawItem += lst_DrawItem;
        }

        private void lst_MeasureItem(object sender, MeasureItemEventArgs e)
        {
            e.ItemHeight = (int)e.Graphics.MeasureString(listBoxLog.Items[e.Index].ToString(), listBoxLog.Font, listBoxLog.Width).Height;
        }

        private void lst_DrawItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            e.DrawFocusRectangle();
            if (e.Index >= 0)
                e.Graphics.DrawString(listBoxLog.Items[e.Index].ToString(), e.Font, new SolidBrush(e.ForeColor), e.Bounds);
        }

        void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs u)
        {
            logger.Trace(u.ExceptionObject);
        }

        void Application_ThreadException(object sender, ThreadExceptionEventArgs t)
        {
            logger.Trace(t.Exception);
        }

        private void frmMain_Shown(object sender, EventArgs e)
        {
            try
            {
                m_msiUpdater = new msiupdater(new Version(System.Windows.Forms.Application.ProductVersion));
                m_msiUpdater.Apppath = System.Windows.Forms.Application.UserAppDataPath;
                m_msiUpdater.onDataPump += new msiupdater.DataPump(m_msiUpdater_onDataPump);
                m_msiUpdater.onUpdateProgressChanged += new msiupdater.UpdateProgressChanged(m_msiUpdater_onUpdateProgressChanged);
                m_msiUpdater.CheckForUpdates("http://develop.trionictuning.com/TrionicCANFlasher/", "canflasher", "TrionicCANFlash.msi");
            }
            catch (Exception E)
            {
                AddLogItem(E.Message);
            }
        }

        void m_msiUpdater_onUpdateProgressChanged(msiupdater.MSIUpdateProgressEventArgs e)
        {

        }

        void m_msiUpdater_onDataPump(msiupdater.MSIUpdaterEventArgs e)
        {
            /*
            if (e.UpdateAvailable)
            {
                frmUpdateAvailable frmUpdate = new frmUpdateAvailable();
                frmUpdate.SetVersionNumber(e.Version.ToString());
                if (m_msiUpdater != null)
                {
                    m_msiUpdater.Blockauto_updates = false;
                }
                if (frmUpdate.ShowDialog() == DialogResult.OK)
                {
                    if (m_msiUpdater != null)
                    {
                        // No active target loaded. It's ok to update
                        if (TagetBusy == false)
                        {
                            m_msiUpdater.ExecuteUpdate(e.Version);
                            System.Windows.Forms.Application.Exit();
                        }
                    }
                }
                else
                {
                    // user choose "NO", don't bug him again!
                    if (m_msiUpdater != null)
                    {
                        m_msiUpdater.Blockauto_updates = false;
                    }
                }
            }
            */
        }

        private void CheckRegistryFTDI()
        {
            if (AppSettings.AdapterType.Index == (int)CANBusAdapter.ELM327)
            {
                using (RegistryKey FTDIBUSKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\FTDIBUS"))
                {
                    if (FTDIBUSKey != null)
                    {
                        string[] vals = FTDIBUSKey.GetSubKeyNames();
                        foreach (string name in vals)
                        {
                            if (name.StartsWith("VID_0403+PID_6001"))
                            {
                                using (RegistryKey NameKey = FTDIBUSKey.OpenSubKey(name + "\\0000\\Device Parameters"))
                                {
                                    if (NameKey != null)
                                    {
                                        String PortName = NameKey.GetValue("PortName").ToString();
                                        if (AppSettings.Adapter.Name != null && AppSettings.Adapter.Name.Equals(PortName))
                                        {
                                            String Latency = NameKey.GetValue("LatencyTimer").ToString();
                                            AddLogItem(String.Format("ELM327 FTDI setting for {0} LatencyTimer {1}ms.", PortName, Latency));
                                            if (!Latency.Equals("2") && !Latency.Equals("1"))
                                            {
                                                MessageBox.Show("Warning LatencyTimer should be set to 2 ms", "ELM327 FTDI setting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        public void AddLogItem(string item)
        {
            if (AppSettings.MainWidth > 740)
            {
                var uiItem = DateTime.Now.ToString("HH:mm:ss.fff") + " - " + item;
                listBoxLog.Items.Add(uiItem);
            }
            else
            {
                listBoxLog.Items.Add(item);
            }

            if (AppSettings.Collapsed)
            {
                string Lastmsg = item;

                if (Lastmsg.Length > 57)
                {
                    Lastmsg = Lastmsg.Remove(57, Lastmsg.Length - 57);
                }

                Minilog.Text = Lastmsg;
                Minilog.Visible = true;
            }

            while (listBoxLog.Items.Count > 100) listBoxLog.Items.RemoveAt(0);
            listBoxLog.SelectedIndex = listBoxLog.Items.Count - 1;
            logger.Trace(item);
            Application.DoEvents();
        }

        void trionicCan_onWriteProgress(object sender, ITrionic.WriteProgressEventArgs e)
        {
            UpdateProgressStatus(e.Percentage);
        }

        void trionicCan_onCanInfo(object sender, ITrionic.CanInfoEventArgs e)
        {
            UpdateFlashStatus(e);
        }

        void trionicCan_onReadProgress(object sender, ITrionic.ReadProgressEventArgs e)
        {
            UpdateProgressStatus(e.Percentage);
        }

        private void updateStatusInBox(ITrionic.CanInfoEventArgs e)
        {
            AddLogItem(e.Info);
        }

        private void UpdateFlashStatus(ITrionic.CanInfoEventArgs e)
        {
            try
            {
                Invoke(m_DelegateUpdateStatus, e);
            }
            catch (Exception ex)
            {
                AddLogItem(ex.Message);
            }
        }

        private void updateProgress(int percentage)
        {
            if (progressBar1.Value != percentage)
            {
                progressBar1.Value = percentage;
            }
            if (AppSettings.EnableLogging)
            {
                logger.Trace("progress: " + percentage.ToString("F0") + "%");
            }
        }

        private void UpdateProgressStatus(int percentage)
        {
            try
            {
                Invoke(m_DelegateProgressStatus, percentage);
            }
            catch (Exception e)
            {
                logger.Trace(e.Message);
            }
        }

        private void StartBGWorkerLog(ITrionic trionic)
        {
            AddLogItem("Logging in progress");
            bgworkerLogCanData = new BackgroundWorker
            {
                WorkerReportsProgress = true,
                WorkerSupportsCancellation = true
            };
            bgworkerLogCanData.DoWork += trionic.LogCANData;
            bgworkerLogCanData.RunWorkerCompleted += bgWorker_RunWorkerCompleted;
            bgworkerLogCanData.RunWorkerAsync();
        }

        void bgWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Cancelled)
            {
                AddLogItem("Stopped");
            }
            else if (e.Result != null && (bool)e.Result)
            {
                AddLogItem("Operation done");
            }
            else
            {
                AddLogItem("Operation failed");
                SetViewMode(false);
            }

            TimeSpan ts = DateTime.Now - dtstart;
            AddLogItem("Total duration: " + ts.Minutes + " minutes " + ts.Seconds + " seconds");

            delcoe39.Cleanup();

            EnableUserInput(true);
            AddLogItem("Connection terminated");
        }

        public void UpdateLogManager()
        {
            if (AppSettings.EnableLogging)
            {
                LogManager.EnableLogging();
            }
            else
            {
                LogManager.DisableLogging();
            }
        }

        private void SetGenericOptions(ITrionic trionic)
        {
            if (trionic == null)
            {
                AddLogItem("SetGenericOptions: Passed null instance of trionic");
                return;
            }

            trionic.OnlyPBus = AppSettings.OnlyPBus;
            trionic.bypassCANfilters = m_bypassCANfilters;

            trionic.LegionOptions.Faster = AppSettings.Faster;
            trionic.LegionOptions.InterframeDelay = AppSettings.InterframeDelay.Value;
            trionic.LegionOptions.UseLastMarker = AppSettings.UseLastMarker;
            trionic.ECU = EcuTargets[cbxEcuType.SelectedIndex].ecu;

            m_bypassCANfilters = false;
 
            switch (AppSettings.AdapterType.Index)
            {
                case (int)CANBusAdapter.JUST4TRIONIC:
                    trionic.ForcedBaudrate = 115200;
                    break;
                case (int)CANBusAdapter.ELM327:
                    //set selected com speed
                    switch (AppSettings.Baudrate.Index)
                    {
                        case (int)ComSpeed.S2Mbit:
                            trionic.ForcedBaudrate = 2000000;
                            break;
                        case (int)ComSpeed.S1Mbit:
                            trionic.ForcedBaudrate = 1000000;
                            break;
                        case (int)ComSpeed.S230400:
                            trionic.ForcedBaudrate = 230400;
                            break;
                        case (int)ComSpeed.S115200:
                            trionic.ForcedBaudrate = 115200;
                            break;
                        default:
                            trionic.ForcedBaudrate = 0; //default , no speed will be changed
                            break;
                    }
                    break;
                default:
                    break;
            }

            trionic.setCANDevice((CANBusAdapter)AppSettings.AdapterType.Index);
            if (AppSettings.Adapter.Name != null)
            {
                trionic.SetSelectedAdapter(AppSettings.Adapter.Name);
            }

            trionic.FormatBootPartition = AppSettings.UnlockBoot;
            trionic.FormatSystemPartitions = AppSettings.UnlockSys;
        }

        private void EnableUserInput(bool enable)
        {
            TargetBusy = !enable;

            btnFlashECU.Enabled = enable;
            btnReadECU.Enabled = enable;
            btnGetECUInfo.Enabled = enable;
            btnReadSRAM.Enabled = enable;
            btnRecoverECU.Enabled = enable;
            btnReadDTC.Enabled = enable;
            cbxEcuType.Enabled = enable;

            btnEditParameters.Enabled = enable;
            btnReadECUcalibration.Enabled = enable;
            btnRestoreT8.Enabled = enable;
            btnLogData.Enabled = enable;
            btnSettings.Enabled = enable;
            btnWriteDID.Enabled = enable;

            bool PreCheck = true;
            if (AppSettings.AdapterType.Index == (int)CANBusAdapter.ELM327 &&
                (AppSettings.Baudrate.Index < 0 || AppSettings.Adapter.Index < 0))
            {
                PreCheck = false;
            }
            else if ((AppSettings.AdapterType.Index == (int)CANBusAdapter.J2534  ||
                      AppSettings.AdapterType.Index == (int)CANBusAdapter.KVASER ||
                      AppSettings.AdapterType.Index == (int)CANBusAdapter.LAWICEL) &&
                      AppSettings.Adapter.Index < 0)
            {
                PreCheck = false;
            }

            // Chriva. Check
            if (AppSettings.AdapterType.Index >= 0 && cbxEcuType.SelectedIndex >= 0 && cbxEcuType.SelectedIndex < EcuTargets.Length && PreCheck)
            {
                TargetFeatures features = EcuTargets[cbxEcuType.SelectedIndex].Target.GetFeatures(EcuTargets[cbxEcuType.SelectedIndex].ecu);

                if (features != null && enable)
                {
                    btnFlashECU.Enabled = features.FlashFull;
                    btnReadECU.Enabled = features.ReadFull;
                    btnReadECUcalibration.Enabled = features.ReadCalib;
                    btnReadSRAM.Enabled = features.ReadSram;
                    btnGetECUInfo.Enabled = features.FirmwareInfo;

                    btnReadDTC.Enabled = features.TroubleCodes;
                    btnEditParameters.Enabled = false;
                    btnLogData.Enabled = false;
                    btnWriteDID.Enabled = false;

                    btnRecoverECU.Enabled = features.Recover;
                    btnRestoreT8.Enabled = features.Restore;
                }
                else
                {
                    btnFlashECU.Enabled = false;
                    btnReadECU.Enabled = false;
                    btnGetECUInfo.Enabled = false;
                    btnReadSRAM.Enabled = false;
                    btnRecoverECU.Enabled = false;
                    btnReadDTC.Enabled = false;

                    btnEditParameters.Enabled = false;
                    btnReadECUcalibration.Enabled = false;
                    btnRestoreT8.Enabled = false;
                    btnLogData.Enabled = false;
                    btnWriteDID.Enabled = false;
                }
            }

            // Disable everything except Settings; Still not fully configured
            else if (cbxEcuType.SelectedIndex >= 0)
            {
                btnFlashECU.Enabled = false;
                btnReadECU.Enabled = false;
                btnGetECUInfo.Enabled = false;
                btnReadSRAM.Enabled = false;
                btnRecoverECU.Enabled = false;
                btnReadDTC.Enabled = false;

                btnEditParameters.Enabled = false;
                btnReadECUcalibration.Enabled = false;
                btnRestoreT8.Enabled = false;
                btnLogData.Enabled = false;
                btnWriteDID.Enabled = false;
            }

            // Disable everything; Select ECU before poking around!
            else
            {
                btnFlashECU.Enabled = false;
                btnReadECU.Enabled = false;
                btnGetECUInfo.Enabled = false;
                btnReadSRAM.Enabled = false;
                btnRecoverECU.Enabled = false;
                btnReadDTC.Enabled = false;

                btnEditParameters.Enabled = false;
                btnReadECUcalibration.Enabled = false;
                btnRestoreT8.Enabled = false;
                btnLogData.Enabled = false;
                btnSettings.Enabled = false;
                btnWriteDID.Enabled = false;
            }
        }

        private static string SubString8(string value)
        {
            return value.Length < 8 ? value : value.Substring(0, 8);
        }

        private ITrionic GetSelectedTarget()
        {
            if (EcuTargets != null && cbxEcuType.SelectedIndex >= 0 && cbxEcuType.SelectedIndex < EcuTargets.Length)
            {
                return EcuTargets[cbxEcuType.SelectedIndex].Target;
            }

            AddLogItem("GetTarget: Internal fault - Target is zero");

            return null;
        }

        private void btnReadECU_Click(object sender, EventArgs e)
        {
            ITrionic target = GetSelectedTarget();

            if (target != null)
            {
                using (SaveFileDialog sfd = new SaveFileDialog() { Filter = "Bin files|*.bin" })
                {
                    if (sfd.ShowDialog() == DialogResult.OK &&
                        sfd.FileName != string.Empty &&
                        Path.GetFileName(sfd.FileName) != string.Empty)
                    {
                        SetGenericOptions(target);
                        EnableUserInput(false);
                        AddLogItem("Opening connection");

                        if (target.openDevice(false))
                        {
                            Thread.Sleep(1000);
                            dtstart = DateTime.Now;
                            AddLogItem("Acquiring FLASH content");
                            Application.DoEvents();
                            BackgroundWorker bgWorker;
                            bgWorker = new BackgroundWorker();
                            bgWorker.DoWork += new DoWorkEventHandler(target.ReadFlash);
                            bgWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(bgWorker_RunWorkerCompleted);
                            bgWorker.RunWorkerAsync(new WorkerArgument { FileName = sfd.FileName, ecu = EcuTargets[cbxEcuType.SelectedIndex].ecu });
                        }
                        else
                        {
                            AddLogItem("Unable to connect to target");
                            target.Cleanup();
                            EnableUserInput(true);
                            AddLogItem("Connection terminated");
                        }
                    }
                }
            }

            LogManager.Flush();
        }

        bool CheckFullFile(string fileName)
        {
            return true;
        }

        private void btnFlashEcu_Click(object sender, EventArgs e)
        {
            ITrionic target = GetSelectedTarget();

            if (target != null)
            {
                using (OpenFileDialog ofd = new OpenFileDialog() { Filter = "Bin files|*.bin", Multiselect = false })
                {
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        if (CheckFullFile(ofd.FileName))
                        {
                            SetGenericOptions(target);
                            EnableUserInput(false);
                            AddLogItem("Opening connection");

                            if (target.openDevice(false))
                            {
                                Thread.Sleep(1000);
                                dtstart = DateTime.Now;
                                AddLogItem("Update FLASH content");
                                Application.DoEvents();
                                BackgroundWorker bgWorker;
                                bgWorker = new BackgroundWorker();
                                bgWorker.DoWork += new DoWorkEventHandler(target.WriteFlash);
                                bgWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(bgWorker_RunWorkerCompleted);
                                bgWorker.RunWorkerAsync(new WorkerArgument { FileName = ofd.FileName, ecu = EcuTargets[cbxEcuType.SelectedIndex].ecu });
                            }
                            else
                            {
                                AddLogItem("Unable to connect to target");
                                target.Cleanup();
                                EnableUserInput(true);
                                AddLogItem("Connection terminated");
                            }
                        }
                        else
                        {
                            target.Cleanup();
                        }
                    }
                }
            }

            LogManager.Flush();
        }

        private void btnGetEcuInfo_Click(object sender, EventArgs e)
        {
            SetViewMode(false);

            ITrionic target = GetSelectedTarget();

            if (target != null)
            {
                SetGenericOptions(target);
                EnableUserInput(false);

                AddLogItem("Opening connection");

                if (target.openDevice(false))
                {
                    Thread.Sleep(1000);
                    dtstart = DateTime.Now;
                    Application.DoEvents();
                    BackgroundWorker bgWorker;
                    bgWorker = new BackgroundWorker();
                    bgWorker.DoWork += new DoWorkEventHandler(target.GetFirmwareInfo);
                    bgWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(bgWorker_RunWorkerCompleted);
                    bgWorker.RunWorkerAsync(new WorkerArgument { FileName = "", ecu = EcuTargets[cbxEcuType.SelectedIndex].ecu });
                }
                else
                {
                    AddLogItem("Unable to connect to target");
                    target.Cleanup();
                    EnableUserInput(true);
                    AddLogItem("Connection terminated");
                }
            }

            LogManager.Flush();
        }

        private void btnReadSRAM_Click(object sender, EventArgs e)
        {
            ITrionic target = GetSelectedTarget();

            if (target != null)
            {
                using (SaveFileDialog sfd = new SaveFileDialog() { Filter = "Bin files|*.bin" })
                {
                    if (sfd.ShowDialog() == DialogResult.OK &&
                        sfd.FileName != string.Empty &&
                        Path.GetFileName(sfd.FileName) != string.Empty)
                    {
                        SetGenericOptions(target);
                        EnableUserInput(false);
                        AddLogItem("Opening connection");

                        if (target.openDevice(false))
                        {
                            Thread.Sleep(1000);
                            dtstart = DateTime.Now;
                            AddLogItem("Acquiring FLASH content");
                            Application.DoEvents();
                            BackgroundWorker bgWorker;
                            bgWorker = new BackgroundWorker();
                            bgWorker.DoWork += new DoWorkEventHandler(target.ReadSram);
                            bgWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(bgWorker_RunWorkerCompleted);
                            bgWorker.RunWorkerAsync(new WorkerArgument { FileName = sfd.FileName, ecu = EcuTargets[cbxEcuType.SelectedIndex].ecu });
                        }
                        else
                        {
                            AddLogItem("Unable to connect to target");
                            target.Cleanup();
                            EnableUserInput(true);
                            AddLogItem("Connection terminated");
                        }
                    }
                }
            }

            LogManager.Flush();
        }

        private void btnRecoverECU_Click(object sender, EventArgs e)
        {
            LogManager.Flush();
        }

        private void btnReadDTC_Click(object sender, EventArgs e)
        {
            SetViewMode(false);

            ITrionic target = GetSelectedTarget();

            if (target != null)
            {
                SetGenericOptions(target);
                EnableUserInput(false);
                AddLogItem("Opening connection");

                if (target.openDevice(false))
                {
                    Thread.Sleep(1000);
                    dtstart = DateTime.Now;
                    Application.DoEvents();
                    BackgroundWorker bgWorker;
                    bgWorker = new BackgroundWorker();
                    bgWorker.DoWork += new DoWorkEventHandler(target.ReadTroubleCodes);
                    bgWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(bgWorker_RunWorkerCompleted);
                    bgWorker.RunWorkerAsync(new WorkerArgument { FileName = "", ecu = EcuTargets[cbxEcuType.SelectedIndex].ecu });
                }
                else
                {
                    AddLogItem("Unable to connect to target");
                    target.Cleanup();
                    EnableUserInput(true);
                    AddLogItem("Connection terminated");
                }
            }

            LogManager.Flush();
        }

        private void btnEditParameters_Click(object sender, EventArgs e)
        {
            // TagetBusy = false;
            LogManager.Flush();
        }

        private void btnReadECUcalibration_Click(object sender, EventArgs e)
        {
            ITrionic target = GetSelectedTarget();

            if (target != null)
            {
                using (SaveFileDialog sfd = new SaveFileDialog() { Filter = "Bin files|*.bin" })
                {
                    if (sfd.ShowDialog() == DialogResult.OK &&
                        sfd.FileName != string.Empty &&
                        Path.GetFileName(sfd.FileName) != string.Empty)
                    {
                        SetGenericOptions(target);
                        EnableUserInput(false);
                        AddLogItem("Opening connection");

                        if (target.openDevice(false))
                        {
                            Thread.Sleep(1000);
                            dtstart = DateTime.Now;
                            AddLogItem("Acquiring FLASH content");
                            Application.DoEvents();
                            BackgroundWorker bgWorker;
                            bgWorker = new BackgroundWorker();
                            bgWorker.DoWork += new DoWorkEventHandler(target.ReadCal);
                            bgWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(bgWorker_RunWorkerCompleted);
                            bgWorker.RunWorkerAsync(new WorkerArgument { FileName = sfd.FileName, ecu = EcuTargets[cbxEcuType.SelectedIndex].ecu });
                        }
                        else
                        {
                            AddLogItem("Unable to connect to target");
                            target.Cleanup();
                            EnableUserInput(true);
                            AddLogItem("Connection terminated");
                        }
                    }
                }
            }

            LogManager.Flush();
        }

        private void btnRestoreT8_Click(object sender, EventArgs e)
        {
            LogManager.Flush();
        }

        private void btnLogData_Click(object sender, EventArgs e)
        {
            // EnableUserInput(true);
        }

        private bool ShouldUpdateChecksum(string layer, string filechecksum, string realchecksum)
        {
            AddLogItem(layer);
            AddLogItem("File Checksum: " + filechecksum);
            AddLogItem("Real Checksum: " + realchecksum);

            using (frmChecksum frm = new frmChecksum() { Layer = layer, FileChecksum = filechecksum, RealChecksum = realchecksum })
            {
                return frm.ShowDialog() == DialogResult.OK ? true : false;
            }
        }

        private void linkLabelLogging_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            string path = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "/MattiasC/TrionicCANFlasher";
            Process.Start(path);
        }

        private void documentation_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start("TrionicCanFlasher.pdf");
        }

        private void RestoreView()
        {
            if (AppSettings.RememberDimensions)
            {
                int maxX = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
                int maxY = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;

                // Check if any of the parameters are out of bounds.
                // If it is, use default from .Designer
                if (AppSettings.MainWidth < 374 || AppSettings.MainHeight < 360 ||
                    AppSettings.MainWidth > maxX || AppSettings.MainHeight > maxY)
                {
                    AppSettings.MainWidth = this.Width;
                    AppSettings.MainHeight = this.Height;
                }

                int Xloc = this.Location.X + (this.Width / 2);
                int Yloc = this.Location.Y + (this.Height / 2);
                int X = AppSettings.MainWidth;
                int Y = AppSettings.MainHeight;

                if (AppSettings.Fullscreen)
                {
                    this.Width = X;
                    this.Height = Y;
                    this.Location = new Point(Xloc - (this.Width / 2), Yloc - (this.Height / 2));
                    WindowState = FormWindowState.Maximized;
                }
                else
                {
                    if (AppSettings.Collapsed)
                    {
                        HandleDynItems(true);
                        this.MinimumSize = new Size(374, 360);
                        this.MaximumSize = new Size(374, 360);
                    }
                    else
                    {
                        this.Width = X;
                        this.Height = Y;
                    }

                    this.Location = new Point(Xloc - (this.Width / 2), Yloc - (this.Height / 2));
                }
            }
            else
            {
                AppSettings.MainWidth  = this.Width;
                AppSettings.MainHeight = this.Height;
            }
        }

        private void HandleDynItems(bool Compact)
        {
            if (Compact)
            {
                // Move links to make room for minilog
                int xDoc = documentation.Location.X;
                int yDoc = documentation.Location.Y - 20;
                documentation.Location = new Point(xDoc, yDoc);
                linkLabelLogging.Location = new Point(xDoc, yDoc + 17);

                int xLog = Minilog.Location.X;
                int yLog = Minilog.Location.Y;
                Minilog.Location = new Point(xLog - 112, yLog);

                this.MaximizeBox = false;
                btnCollapse.Visible = false;
                btnCollapse.Enabled = false;
                btnExpand.Enabled = true;
                btnExpand.Visible = true;
            }
            else
            {
                // Restore location of links
                int xDoc = documentation.Location.X;
                int yDoc = documentation.Location.Y + 20;
                documentation.Location = new Point(xDoc, yDoc);
                linkLabelLogging.Location = new Point(xDoc, yDoc + 17);

                Minilog.Visible = false;
                Minilog.Text = "Mini log";
                int xLog = Minilog.Location.X;
                int yLog = Minilog.Location.Y;
                Minilog.Location = new Point(xLog + 112, yLog);

                btnCollapse.Visible = true;
                btnCollapse.Enabled = true;
                btnExpand.Enabled = false;
                btnExpand.Visible = false;
                this.MaximizeBox = true;
            }
        }

        private void SetViewMode(bool Compact)
        {
            if (Compact)
            {
                HandleDynItems(true);
                AppSettings.Collapsed = true;

                AppSettings.MainWidth  = this.Width;
                AppSettings.MainHeight = this.Height;

                int Xloc = this.Location.X + AppSettings.MainWidth;
                int Yloc = this.Location.Y;

                this.MinimumSize = new Size(374, 360);
                this.MaximumSize = new Size(374, 360);
                this.Location = new Point(Xloc - 374, Yloc);
            }

            else if(AppSettings.Collapsed)
            {
                HandleDynItems(false);

                int Xloc = this.Location.X + this.Width;
                int Yloc = this.Location.Y;

                this.MaximumSize = new Size(0, 0);
                this.Width  = AppSettings.MainWidth;
                this.Height = AppSettings.MainHeight;

                this.Location = new Point(Xloc - AppSettings.MainWidth, Yloc);
                this.MinimumSize = new Size(600, 360);

                AppSettings.Collapsed = false;
            }
        }

        private void btnExpand_Click(object sender, EventArgs e)
        {
            SetViewMode(false);
        }

        private void btnCollapse_Click(object sender, EventArgs e)
        {
            SetViewMode(true);

            // Show last logged item in minilog. If available
            if (listBoxLog.Items.Count > 0)
            {

                if (listBoxLog.Text.Length > 0)
                {
                    string Lastmsg = listBoxLog.Text;

                    // Strip time stamp from item
                    if (listBoxLog.Text.Length > 15)
                    {
                        if (listBoxLog.Text[2] == 0x3A &&
                            listBoxLog.Text[5] == 0x3A &&
                            listBoxLog.Text[8] == 0x2E)
                        {
                            Lastmsg = Lastmsg.Remove(0, 15);
                        }
                    }

                    // Truncate text that is longer than the progress bar
                    if (Lastmsg.Length > 57)
                    {
                        Lastmsg = Lastmsg.Remove(57, Lastmsg.Length - 57);
                    }

                    Minilog.Text = Lastmsg;
                    Minilog.Visible = true;
                }
            }
        }

        // Hide btnCollapse when maximised (For simplicity's sake. Less parameters to keep track of)
        private void frmMainResized(object sender, EventArgs e)
        {
            AppSettings.Fullscreen = WindowState == FormWindowState.Maximized ? true : false;

            if (!AppSettings.Collapsed && WindowState != FormWindowState.Maximized)
            {
                AppSettings.MainWidth = this.Width;
                AppSettings.MainHeight = this.Height;
            }

            if (WindowState != LastWindowState)
            {

                if (WindowState == FormWindowState.Maximized)
                {
                    btnCollapse.Visible = false;
                    btnCollapse.Enabled = false;
                }

                else if (LastWindowState == FormWindowState.Maximized)
                {
                    btnCollapse.Visible = true;
                    btnCollapse.Enabled = true;
                }

                LastWindowState = WindowState;
            }
        }

        private void cbxEcuType_SelectedIndexChanged(object sender, EventArgs e)
        {
            AppSettings.SelectedECU.Index = cbxEcuType.SelectedIndex;
            AppSettings.SelectedECU.Name = cbxEcuType.SelectedItem.ToString();
            EnableUserInput(true);
        }

        private void btnWriteDID_Click(object sender, EventArgs e)
        {
            LogManager.Flush();
        }
        
        private void btnSettings_Click(object sender, EventArgs e)
        {
            ITrionic target = GetSelectedTarget();

            if (target != null)
            {
                bool LastLoggingState = AppSettings.EnableLogging;

                AppSettings.PopulateItems(target, EcuTargets[cbxEcuType.SelectedIndex].ecu);
                AppSettings.ShowDialog();

                delcoe39.PrintSettings();

                if (LastLoggingState != AppSettings.EnableLogging)
                {
                    UpdateLogManager();
                }

                EnableUserInput(true);
            }
            else
            {
                AddLogItem("btnSettings_Click: You should not see this");
            }
        }
    }
}
