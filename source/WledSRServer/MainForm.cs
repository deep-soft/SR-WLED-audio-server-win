﻿using System.Diagnostics;
using System.Net;
using WledSRServer.Audio;
using WledSRServer.Audio.AudioProcessor.FFTBuckets;

namespace WledSRServer
{
    public partial class MainForm : Form
    {
        private Stopwatch PPSwatch = new Stopwatch();

        public MainForm()
        {
            InitializeComponent();

            if (!DesignMode)
                this.Icon = Properties.Resources.NotifIcon;

            // Console.WriteLine("===[ packet preview ]======================================================");
            // Console.WriteLine($"sampleRaw  : {packet.sampleRaw,-20:F45}");
            // Console.WriteLine($"sampleSmth : {packet.sampleSmth,-20:F45}");
            // Console.WriteLine($"samplePeak : {packet.samplePeak,-20}");

            // Console.WriteLine($"FFT_Magnitude : {packet.FFT_Magnitude,-20:F32}");
            // Console.WriteLine($"FFT_MajorPeak : {packet.FFT_MajorPeak,10:F4} (hz)                  ");

            // V=FreqToDisplay, V0=FreqLow, V1=FreqHigh, X0-X1 control width
            // X = X0 + (X1 - X0)(log(V) - log(V0))/(log(V1) - log(V0))

            Text = $"WLED SoundReactive Server - {Program.Version(false)}";

            var settings = Properties.Settings.Default;

            btnSetAutoRun.CheckboxChecked = AdminFunctions.GetAutoRun();
            btnSetStartupGUI.CheckboxChecked = settings.StartWithoutGUI;

            ddlAudioDevices.DataSource = AudioCaptureManager.GetDevices();
            ddlAudioDevices.DisplayMember = nameof(AudioCaptureManager.SimpleDeviceDescriptor.Name);
            ddlAudioDevices.ValueMember = nameof(AudioCaptureManager.SimpleDeviceDescriptor.ID);
            ddlAudioDevices.SelectedValue = settings.AudioCaptureDeviceId;
            ddlAudioDevices.SelectedIndexChanged += ddlAudioDevices_Changed;

            ddlValueScale.ValueMember = "Key";
            ddlValueScale.DisplayMember = "Value";
            ddlValueScale.DataSource = (new Dictionary<Bucketizer.Scale, string> {
                { Bucketizer.Scale.Linear,      "Linear (Amplitude)" },
                { Bucketizer.Scale.SquareRoot,  "Square Root (Energy)" },
                { Bucketizer.Scale.Logarithmic, "Logarithmic (Loudness)" }
            }).ToList();
            ddlValueScale.SelectedValue = Bucketizer.ScaleFromString(settings.FFTValueScale, Bucketizer.Scale.SquareRoot);
            ddlValueScale.SelectedIndexChanged += ddlValueScale_Changed;

            chbFFTLogFreq.Checked = settings.FFTFreqLogScale;
            chbFFTLogFreq.CheckedChanged += ChbFFTLogFreq_Changed;

            txtUdpPort.Text = settings.WledUdpMulticastPort.ToString();
            txtUdpPort.AutoCompleteCustomSource.Add("11988"); // the default one
            txtUdpPort.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            txtUdpPort.AutoCompleteSource = AutoCompleteSource.CustomSource;
            txtUdpPort.TextChanged += txtUdpPort_TextChanged;

            txtLocalIpAddress.AutoCompleteCustomSource.AddRange(NetworkManager.GetLocalIPAddresses());
            txtLocalIpAddress.AutoCompleteMode = AutoCompleteMode.Suggest;
            txtLocalIpAddress.AutoCompleteSource = AutoCompleteSource.CustomSource;
            txtLocalIpAddress.Text = settings.LocalIPToBind;
            txtLocalIpAddress.TextChanged += txtLocalIpAddress_Changed;

            txtFFTLower.Text = settings.FFTLow.ToString();
            txtFFTLower.TextChanged += txtFFTLower_TextChanged;
            txtFFTUpper.Text = settings.FFTHigh.ToString();
            txtFFTUpper.TextChanged += txtFFTUpper_TextChanged;

            toolTip1.InitialDelay = 100;

            Program.ServerContext.PacketCounter = 0;
            PPSwatch.Start();
            tmrUpdateStats.Enabled = true;

            beatPixel1.DoubleClick += (s, e) => new BeatTestForm().Show();
        }

        #region Periodic stats update

        private void tmrUpdateStats_Tick(object sender, EventArgs e)
        {
            if (PPSwatch.ElapsedMilliseconds > 500)
            {
                var pps = 0;
                if (Program.ServerContext.PacketCounter > 0)
                {
                    pps = (int)(Program.ServerContext.PacketCounter * 1000 / PPSwatch.ElapsedMilliseconds);
                    Program.ServerContext.PacketCounter = 0;
                    PPSwatch.Restart();
                }
                lblPPS.Text = $"Packet per second : {pps:D}";
            }

            if (Program.ServerContext.PacketSendingStatus == PacketSendingStatus.Error)
            {
                lblPPS.ForeColor = Color.Red;
                SetToolTip(lblPPS, $"There is some problem sending out the packages.\nError: {Program.ServerContext.PacketSendErrorMessage}");
            }
            else
            {
                lblPPS.ForeColor = Color.FromKnownColor(KnownColor.ControlText);
                SetToolTip(lblPPS, null);
            }

            switch (Program.ServerContext.AudioCaptureStatus)
            {
                case AudioCaptureStatus.unknown:
                    lblCapturing.BackColor = Color.FromKnownColor(KnownColor.Control);
                    SetToolTip(lblCapturing, "Audio capture is not initialized yet.");
                    break;
                case AudioCaptureStatus.Capturing_Sound:
                    lblCapturing.BackColor = Color.LightGreen;
                    SetToolTip(lblCapturing, "Capturing sound.");
                    break;
                case AudioCaptureStatus.Capturing_Silence:
                    lblCapturing.BackColor = Color.Gold;
                    SetToolTip(lblCapturing, "Capturing silence.");
                    break;
                case AudioCaptureStatus.Error:
                    lblCapturing.BackColor = Color.Tomato;
                    SetToolTip(lblCapturing, $"There are some error during capturing. Try to change the audio input.\nError: {Program.ServerContext.AudioCaptureErrorMessage}");
                    break;
            }
        }

        #endregion

        private void btnSettings_Click(object sender, EventArgs e)
        {
            pnlSettings.Visible = !pnlSettings.Visible;
        }

        internal void ShowSettings()
        {
            txtLocalIpAddress_Changed(null, null); //re-check
            pnlSettings.Visible = true;
        }

        private void SetToolTip(Control control, string? toolTip)
        {
            toolTip1.SetToolTip(control, toolTip);
        }

        #region Close or Exit app

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            Program.GuiContext.FormClosed(e.CloseReason);
        }

        private void btnExitApplication_Click(object sender, EventArgs e)
        {
            Program.GuiContext.ExitApp();
        }

        #endregion

        private void btnSetAutoRun_Click(object sender, EventArgs e)
        {
            var newState = !AdminFunctions.GetAutoRun();
            if (!AdminFunctions.SetAutoRun(newState))
                MessageBox.Show("Cannot set the AutoStart value", Program.MboxTitle);
            btnSetAutoRun.CheckboxChecked = AdminFunctions.GetAutoRun();
        }

        private void btnSetStartupGUI_Click(object sender, EventArgs e)
        {
            var newValue = !Properties.Settings.Default.StartWithoutGUI;
            Properties.Settings.Default.StartWithoutGUI = newValue;
            Properties.Settings.Default.Save();
            btnSetStartupGUI.CheckboxChecked = newValue;
        }

        #region Network

        private void txtUdpPort_TextChanged(object sender, EventArgs e)
        {
            if (!int.TryParse(txtUdpPort.Text, out var udpport) || udpport < 0 || udpport > 65535)
            {
                txtUdpPort.BackColor = Color.Salmon;
                SetToolTip(txtUdpPort, "Not a valid port number");
                return;
            }
            SetToolTip(txtUdpPort, null);
            txtUdpPort.BackColor = Color.White;

            Properties.Settings.Default.WledUdpMulticastPort = udpport;
            Properties.Settings.Default.Save();
            NetworkManager.ReStart();
        }

        private void txtLocalIpAddress_Changed(object? sender, EventArgs e)
        {
            var newIpAddress = txtLocalIpAddress.Text;
            if (!string.IsNullOrEmpty(newIpAddress))
            {
                if (!IPAddress.TryParse(newIpAddress, out var newAddress))
                {
                    txtLocalIpAddress.BackColor = Color.Salmon;
                    SetToolTip(txtLocalIpAddress, "Not valid IP address.");
                    return;
                }
                if (!NetworkManager.TestLocalIP(newAddress, out var errorMessage))
                {
                    txtLocalIpAddress.BackColor = Color.Salmon;
                    var err = "There is a problem with this IP address.";
                    if (!string.IsNullOrEmpty(errorMessage)) err += $"\nError: {errorMessage}";
                    SetToolTip(txtLocalIpAddress, err);
                    return;
                }
            }
            SetToolTip(txtLocalIpAddress, null);
            txtLocalIpAddress.BackColor = Color.White;

            Properties.Settings.Default.LocalIPToBind = newIpAddress;
            Properties.Settings.Default.Save();
            NetworkManager.ReStart();
        }

        #endregion

        #region Input device

        private void ddlAudioDevices_Changed(object sender, EventArgs e)
        {
            var selected = ddlAudioDevices.SelectedItem as AudioCaptureManager.SimpleDeviceDescriptor;
            if (selected == null) return;
            Properties.Settings.Default.AudioCaptureDeviceId = selected.ID;
            Properties.Settings.Default.Save();
            AudioCaptureManager.RestartCapture();
        }

        #endregion

        #region FFT

        private void txtFFTLower_TextChanged(object sender, EventArgs e)
        {
            if (!int.TryParse(txtFFTLower.Text, out var newValue) || newValue < 1 || newValue >= Properties.Settings.Default.FFTHigh)
            {
                txtFFTLower.BackColor = Color.Salmon;
                SetToolTip(txtFFTLower, "Needs to be a number between 1 and the higher end of the range");
                return;
            }
            SetToolTip(txtFFTLower, null);
            txtFFTLower.BackColor = Color.White;

            Properties.Settings.Default.FFTLow = newValue;
            Properties.Settings.Default.Save();
            AudioCaptureManager.RestartCapture();
        }

        private void txtFFTUpper_TextChanged(object sender, EventArgs e)
        {
            if (!int.TryParse(txtFFTUpper.Text, out var newValue) || newValue > 99999 || newValue <= Properties.Settings.Default.FFTLow)
            {
                txtFFTUpper.BackColor = Color.Salmon;
                SetToolTip(txtFFTUpper, "Needs to be a number between the lower end of the range and 99999");
                return;
            }
            SetToolTip(txtFFTUpper, null);
            txtFFTUpper.BackColor = Color.White;

            Properties.Settings.Default.FFTHigh = newValue;
            Properties.Settings.Default.Save();
            AudioCaptureManager.RestartCapture();
        }

        private void ChbFFTLogFreq_Changed(object? sender, EventArgs e)
        {
            Properties.Settings.Default.FFTFreqLogScale = chbFFTLogFreq.Checked;
            Properties.Settings.Default.Save();
            AudioCaptureManager.RestartCapture();
        }

        private void ddlValueScale_Changed(object? sender, EventArgs e)
        {
            if (ddlValueScale.SelectedValue == null) return;
            var selected = (Bucketizer.Scale)ddlValueScale.SelectedValue;
            Properties.Settings.Default.FFTValueScale = selected.ToString();
            Properties.Settings.Default.Save();
            AudioCaptureManager.RestartCapture();
        }

        #endregion
    }
}
