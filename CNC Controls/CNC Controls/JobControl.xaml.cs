/*
 * JobControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * v0.29 / 2020-02-23 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2018-2021, Io Engineering (Terje Io)
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

· Redistributions of source code must retain the above copyright notice, this
list of conditions and the following disclaimer.

· Redistributions in binary form must reproduce the above copyright notice, this
list of conditions and the following disclaimer in the documentation and/or
other materials provided with the distribution.

· Neither the name of the copyright holder nor the names of its contributors may
be used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

*/

using System;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CNC.Core;
using CNC.GCode;

namespace CNC.Controls
{
    /// <summary>
    /// Interaction logic for JobControl.xaml
    /// </summary>
    public partial class JobControl : UserControl
    {
        private enum StreamingHandler
        {
            Idle = 0,
            SendFile,
            FeedHold,
            ToolChange,
            AwaitAction,
            AwaitIdle,
            Previous,
            Max // only used for array instantiation
        }

        private struct StreamingHandlerFn
        {
            public StreamingHandler Handler;
            public bool Count;
            public Func<StreamingState, bool, bool> Call;
        }

        private struct JobData
        {
            public int CurrLine, PendingLine, PgmEndLine, ACKPending, serialUsed;
            public bool Started, Complete, IsSDFile;
            public DataRow CurrentRow, NextRow;
        }

        private int serialSize = 128;
        private bool holdSignal = false, cycleStartSignal = false, initOK = false, isActive = false, useBuffering = false;
        private volatile StreamingState streamingState = StreamingState.NoFile;
        private GrblState grblState;
        private GrblViewModel model;
        private JobData job;
        private int missed = 0;

        private StreamingHandlerFn[] streamingHandlers = new StreamingHandlerFn[(int)StreamingHandler.Max];
        private StreamingHandlerFn streamingHandler;

        //       private delegate void GcodeCallback(string data);

        public delegate void StreamingStateChangedHandler(StreamingState state, bool MPGMode);
        public event StreamingStateChangedHandler StreamingStateChanged;

        public JobControl()
        {
            InitializeComponent();

            DataContextChanged += JobControl_DataContextChanged;

            grblState.State = GrblStates.Unknown;
            grblState.Substate = 0;
            grblState.MPG = false;

            job.PgmEndLine = -1;

            streamingHandlers[(int)StreamingHandler.Idle].Call = StreamingIdle;
            streamingHandlers[(int)StreamingHandler.Idle].Count = false;

            streamingHandlers[(int)StreamingHandler.SendFile].Call = StreamingSendFile;
            streamingHandlers[(int)StreamingHandler.SendFile].Count = true;

            streamingHandlers[(int)StreamingHandler.ToolChange].Call = StreamingToolChange;
            streamingHandlers[(int)StreamingHandler.ToolChange].Count = false;

            streamingHandlers[(int)StreamingHandler.FeedHold].Call = StreamingFeedHold;
            streamingHandlers[(int)StreamingHandler.FeedHold].Count = true;

            streamingHandlers[(int)StreamingHandler.AwaitAction].Call = StreamingAwaitAction;
            streamingHandlers[(int)StreamingHandler.AwaitAction].Count = true;

            streamingHandlers[(int)StreamingHandler.AwaitIdle].Call = StreamingAwaitIdle;
            streamingHandlers[(int)StreamingHandler.AwaitIdle].Count = false;

            streamingHandler = streamingHandlers[(int)StreamingHandler.Previous] = streamingHandlers[(int)StreamingHandler.Idle];

            for (int i = 0; i < streamingHandlers.Length; i++)
                streamingHandlers[i].Handler = (StreamingHandler)i;

//            Thread.Sleep(100);

            Loaded += JobControl_Loaded;
        }

        private void JobControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (!System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
                AppConfig.Settings.Base.PropertyChanged += Base_PropertyChanged;
        }

        private void Base_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            GCodeParser.IgnoreM6 = AppConfig.Settings.Base.IgnoreM6;
            GCodeParser.IgnoreM7 = AppConfig.Settings.Base.IgnoreM7;
            GCodeParser.IgnoreM8 = AppConfig.Settings.Base.IgnoreM8;

            useBuffering = AppConfig.Settings.Base.UseBuffering && GrblInfo.IsGrblHAL;
        }

        private void JobControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue != null && e.OldValue is INotifyPropertyChanged)
                ((INotifyPropertyChanged)e.OldValue).PropertyChanged -= OnDataContextPropertyChanged;
            if (e.NewValue != null && e.NewValue is INotifyPropertyChanged)
            {
                model = (GrblViewModel)e.NewValue;
                model.PropertyChanged += OnDataContextPropertyChanged;
                model.OnRealtimeStatusProcessed += RealtimeStatusProcessed;
                model.OnCommandResponseReceived += ResponseReceived;

                GCode.File.Model = model;
            }
        }

        private void RealtimeStatusProcessed(string response)
        {
            if (JobTimer.IsRunning && !JobTimer.IsPaused)
                model.RunTime = JobTimer.RunTime;
        }

        private void OnDataContextPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (sender is GrblViewModel) switch (e.PropertyName)
            {
                case nameof(GrblViewModel.GrblState):
                    GrblStateChanged(((GrblViewModel)sender).GrblState);
                    break;

                case nameof(GrblViewModel.MDI):
                    SendCommand(((GrblViewModel)sender).MDI);
                    break;

                case nameof(GrblViewModel.IsMPGActive):
                    grblState.MPG = ((GrblViewModel)sender).IsMPGActive == true;
                    ((GrblViewModel)sender).Poller.SetState(grblState.MPG ? 0 : AppConfig.Settings.Base.PollInterval);
                    streamingHandler.Call(grblState.MPG ? StreamingState.Disabled : StreamingState.Idle, false);
                    break;

                case nameof(GrblViewModel.Signals):
                    if(isActive) {
                        var signals = ((GrblViewModel)sender).Signals.Value;
                        if (JobPending && signals.HasFlag(Signals.CycleStart) && !signals.HasFlag(Signals.Hold) && !cycleStartSignal)
                            CycleStart();
                        holdSignal = signals.HasFlag(Signals.Hold);
                        cycleStartSignal = signals.HasFlag(Signals.CycleStart);
                    }
                    break;

                case nameof(GrblViewModel.ProgramEnd):
                    if (!GCode.File.IsLoaded)
                        streamingHandler.Call(job.IsSDFile ? StreamingState.JobFinished : StreamingState.NoFile, job.IsSDFile);
                    else if(JobTimer.IsRunning && !job.Complete)
                        streamingHandler.Call(StreamingState.JobFinished, true);
                    break;

                case nameof(GrblViewModel.FileName):
                    {
                        if(string.IsNullOrEmpty(((GrblViewModel)sender).FileName))
                            job.NextRow = null;
                        else
                        {
                            job.IsSDFile = false;
                            job.CurrLine = job.PendingLine = job.ACKPending = model.BlockExecuting = 0;
                            job.PgmEndLine = GCode.File.Blocks - 1;
                            if (GCode.File.ToolChanges > 0 && GrblSettings.GetInteger(GrblSetting.ToolChangeMode) > 0 && !model.IsTloReferenceSet)
                                MessageBox.Show(string.Format("Job has {0} tool change(s), tool length reference should be established before start.", GCode.File.ToolChanges), "GCode Sender", MessageBoxButton.OK, MessageBoxImage.Warning);
                            streamingHandler.Call(GCode.File.IsLoaded ? StreamingState.Idle : StreamingState.NoFile, false);
                        }
                        break;
                    }

                case nameof(GrblViewModel.GrblReset):
                    {
                        JobTimer.Stop();
                        streamingHandler.Call(StreamingState.Stop, true);
                    }
                    break;
            }
        }

        public bool canJog { get { return grblState.State == GrblStates.Idle || grblState.State == GrblStates.Tool || grblState.State == GrblStates.Jog; } }
        public bool JobPending { get { return GCode.File.IsLoaded && !JobTimer.IsRunning; } }

        public bool Activate(bool activate)
        {
            if (activate && !initOK)
            {
                initOK = true;
                serialSize = Math.Min(AppConfig.Settings.Base.MaxBufferSize, (int)(GrblInfo.SerialBufferSize * 0.9f)); // size should be less than hardware handshake HWM
                GCode.File.Parser.Dialect = GrblInfo.IsGrblHAL ? Dialect.GrblHAL : Dialect.Grbl;
            }

            EnablePolling(activate);

            isActive = activate;

            return isActive;
        }

        public void EnablePolling(bool enable)
        {
            if (enable)
                model.Poller.SetState(AppConfig.Settings.Base.PollInterval);
            else if (model.Poller.IsEnabled)
                model.Poller.SetState(0);
        }

        // Configure to match Grbl settings (if loaded)
        public bool Configure(KeypressHandler keypress)
        {
            keypress.AddHandler(Key.R, ModifierKeys.Alt, StartJob);
            keypress.AddHandler(Key.S, ModifierKeys.Alt, StopJob);
            keypress.AddHandler(Key.H, ModifierKeys.Control, Home);
            keypress.AddHandler(Key.U, ModifierKeys.Control, Unlock);
            keypress.AddHandler(Key.R, ModifierKeys.Shift|ModifierKeys.Control, Reset);
            keypress.AddHandler(Key.Space, ModifierKeys.None, FeedHold);
            keypress.AddHandler(Key.F1, ModifierKeys.None, FnKeyHandler);
            keypress.AddHandler(Key.F2, ModifierKeys.None, FnKeyHandler);
            keypress.AddHandler(Key.F3, ModifierKeys.None, FnKeyHandler);
            keypress.AddHandler(Key.F4, ModifierKeys.None, FnKeyHandler);
            keypress.AddHandler(Key.F5, ModifierKeys.None, FnKeyHandler);
            keypress.AddHandler(Key.F6, ModifierKeys.None, FnKeyHandler);
            keypress.AddHandler(Key.F7, ModifierKeys.None, FnKeyHandler);
            keypress.AddHandler(Key.F8, ModifierKeys.None, FnKeyHandler);
            keypress.AddHandler(Key.F9, ModifierKeys.None, FnKeyHandler);
            keypress.AddHandler(Key.F10, ModifierKeys.None, FnKeyHandler);
            keypress.AddHandler(Key.F11, ModifierKeys.None, FnKeyHandler);
            keypress.AddHandler(Key.F12, ModifierKeys.None, FnKeyHandler);

            GCodeParser.IgnoreM6 = AppConfig.Settings.Base.IgnoreM6;
            GCodeParser.IgnoreM7 = AppConfig.Settings.Base.IgnoreM7;
            GCodeParser.IgnoreM8 = AppConfig.Settings.Base.IgnoreM8;

            useBuffering = AppConfig.Settings.Base.UseBuffering && GrblInfo.IsGrblHAL;

            return GrblSettings.IsLoaded;
        }

        #region Keyboard shortcut handlers

        private bool StopJob(Key key)
        {
            streamingHandler.Call(StreamingState.Stop, false);
            return true;
        }

        private bool StartJob(Key key)
        {
            CycleStart();
            return true;
        }

        private bool Home(Key key)
        {
            model.ExecuteCommand(GrblConstants.CMD_HOMING);
            return true;
        }

        private bool Unlock(Key key)
        {
            model.ExecuteCommand(GrblConstants.CMD_UNLOCK);
            return true;
        }

        private bool Reset(Key key)
        {
            Comms.com.WriteByte((byte)GrblConstants.CMD_RESET);
            return true;
        }

        private bool FeedHold(Key key)
        {
            if (grblState.State != GrblStates.Idle)
                btnHold_Click(null, null);
            return grblState.State != GrblStates.Idle;
        }

        private bool FnKeyHandler(Key key)
        {
            if(!model.IsJobRunning)
            {
                int id = int.Parse(key.ToString().Substring(1));
                var macro = AppConfig.Settings.Macros.FirstOrDefault(o => o.Id == id);
                if (macro != null && MessageBox.Show(string.Format("Run {0} macro?", macro.Name), "Run macro", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    model.ExecuteCommand(macro.Code);
                    return true;
                }
            }
            return false;
        }

        #endregion

        public bool CallHandler (StreamingState state, bool always)
        {
            return streamingHandler.Call(state, always);
        }

        #region UIevents

        void btnRewind_Click(object sender, RoutedEventArgs e)
        {
            RewindFile();
            streamingHandler.Call(streamingState, true);
        }

        void btnHold_Click(object sender, RoutedEventArgs e)
        {
            Comms.com.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_FEED_HOLD));
        }

        void btnStop_Click(object sender, RoutedEventArgs e)
        {
            streamingHandler.Call(StreamingState.Stop, true);
        }

        void btnStart_Click(object sender, RoutedEventArgs e)
        {
            CycleStart();
        }

        #endregion

        public void CycleStart()
        {
            if (grblState.State == GrblStates.Hold || (grblState.State == GrblStates.Run && grblState.Substate == 1))
                Comms.com.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_CYCLE_START));
            else if(grblState.State == GrblStates.Tool)
            {
                model.Message = "";
                Comms.com.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_CYCLE_START));
            }
            else if(JobTimer.IsRunning)
            {
                JobTimer.Pause = false;
                streamingHandler.Call(StreamingState.Send, false);
            }
            else if (GCode.File.IsLoaded)
            {
                model.RunTime = "";
                job.ACKPending = job.CurrLine = job.ACKPending = job.serialUsed = 0;
                job.Started = false;
                job.NextRow = GCode.File.Data.Rows[0];
                Comms.com.PurgeQueue();
                model.Message = "";
                JobTimer.Start();
                streamingHandler.Call(StreamingState.Send, false);
                System.Threading.Thread.Sleep(250);
                SendNextLine();
            }
        }

        public void SendRTCommand(string command)
        {
            Comms.com.WriteByte((byte)command[0]);
        }

        private void SendCommand(string command)
        {
            if (command.Length == 1)
                SendRTCommand(command);
            else if (streamingState == StreamingState.Idle || streamingState == StreamingState.NoFile || streamingState == StreamingState.ToolChange || streamingState == StreamingState.Stop || command == GrblConstants.CMD_UNLOCK)
            {
                //                command = command.ToUpper();
                try
                {
                    string c = command;
                    GCode.File.Parser.ParseBlock(ref c, true);
                    GCode.File.Commands.Enqueue(command);
                    if (streamingState != StreamingState.SendMDI)
                    {
                        streamingState = StreamingState.SendMDI;
                        ResponseReceived("go");
                    }
                }
                catch
                {
                }
            }
        }

        public void RewindFile()
        {
            job.Complete = false;

            if (GCode.File.IsLoaded)
            {
                using (new UIUtils.WaitCursor())
                {
                    btnStart.IsEnabled = false;

   //                 grdGCode.DataContext = null;

                    GCode.File.ClearStatus();

                    //                  grdGCode.DataContext = GCode.File.Data.DefaultView;
                    model.ScrollPosition = 0;
                    job.CurrLine = job.PendingLine = job.ACKPending = model.BlockExecuting = 0;
                    job.PgmEndLine = GCode.File.Blocks - 1;

                    btnStart.IsEnabled = true;
                }
            }
        }

        private void SetStreamingHandler(StreamingHandler handler)
        {
            if (handler == StreamingHandler.Previous)
                streamingHandler = streamingHandlers[(int)StreamingHandler.Previous];
            else if (streamingHandler.Handler != handler)
            {
                if (handler == StreamingHandler.Idle)
                    streamingHandler = streamingHandlers[(int)StreamingHandler.Previous] = streamingHandlers[(int)StreamingHandler.Idle];
                else {
                    streamingHandlers[(int)StreamingHandler.Previous] = streamingHandler;
                    streamingHandler = streamingHandlers[(int)handler];
                    if (handler == StreamingHandler.AwaitAction)
                        streamingHandler.Count = true;
                }
            }
        }

        public bool StreamingToolChange(StreamingState newState, bool always)
        {
            bool changed = streamingState != newState;

            switch (newState)
            {
                case StreamingState.ToolChange:
                    model.IsJobRunning = false; // only enable UI if no ATC?
                    btnStart.IsEnabled = true;
                    btnHold.IsEnabled = false;
                    btnStop.IsEnabled = true;
                    break;

                case StreamingState.Idle:
                case StreamingState.Send:
                    if (JobTimer.IsRunning)
                    {
                        model.IsJobRunning = true;
                        GCode.File.Data.Rows[job.PendingLine]["Sent"] = "ok";
                        SetStreamingHandler(StreamingHandler.SendFile);
                        SendNextLine();
                    }
                    else
                        SetStreamingHandler(StreamingHandler.Previous);
                    break;

                case StreamingState.Error:
                    SetStreamingHandler(StreamingHandler.Previous);
                    break;

                case StreamingState.Stop:
                    SetStreamingHandler(StreamingHandler.Idle);
                    break;
            }

            if (streamingHandler.Handler != StreamingHandler.ToolChange)
                return streamingHandler.Call(newState, true);
            else if (changed)
            {
                model.StreamingState = streamingState = newState;
                StreamingStateChanged?.Invoke(streamingState, grblState.MPG);
            }

            return true;
        }

        public bool StreamingFeedHold(StreamingState newState, bool always)
        {
            bool changed = streamingState != newState;

            if (always || changed)
            {
                switch (newState)
                {
                    case StreamingState.Halted:
                    case StreamingState.FeedHold:
                        btnStart.IsEnabled = true;
                        btnHold.IsEnabled = false;
                        btnStop.IsEnabled = model.IsJobRunning;
                        break;

                    case StreamingState.Send:
                    case StreamingState.Error:
                    case StreamingState.Idle:
                        SetStreamingHandler(StreamingHandler.Previous);
                        break;

                    case StreamingState.Stop:
                        SetStreamingHandler(StreamingHandler.Idle);
                        break;
                }
            }

            if (streamingHandler.Handler != StreamingHandler.FeedHold)
                return streamingHandler.Call(newState, true);
            else if (changed)
            {
                model.StreamingState = streamingState = newState;
                StreamingStateChanged?.Invoke(streamingState, grblState.MPG);
            }

            return true;
        }

        public bool StreamingSendFile(StreamingState newState, bool always)
        {
            bool changed = streamingState != newState;

            if (changed || always)
            {
                switch (newState)
                {
                    case StreamingState.Idle:
                        if(streamingState == StreamingState.Error)
                        {
                            btnStart.IsEnabled = !GrblInfo.IsGrblHAL; // BAD! ?
                            btnHold.IsEnabled = false;
                            btnStop.IsEnabled = true;
                            SetStreamingHandler(StreamingHandler.AwaitAction);
                        }
                        else
                            changed = false; // ignore
                        break;

                    case StreamingState.Send:
                        if (!model.IsJobRunning)
                            model.IsJobRunning = true;
                        btnStart.IsEnabled = false;
                        btnHold.IsEnabled = true;
                        btnStop.IsEnabled = true;
                        btnRewind.IsEnabled = false;
                        break;

                    case StreamingState.Error:
                    case StreamingState.Halted:
                        btnHold.IsEnabled = false;
                        break;

                    case StreamingState.FeedHold:
                        SetStreamingHandler(StreamingHandler.FeedHold);
                        break;

                    case StreamingState.ToolChange:
                        SetStreamingHandler(StreamingHandler.ToolChange);
                        break;

                    case StreamingState.JobFinished:
                        if (grblState.State == GrblStates.Idle || grblState.State == GrblStates.Check)
                            newState = StreamingState.Idle;
                        job.Complete = true;
                        job.ACKPending = job.CurrLine = 0;
                        SetStreamingHandler(StreamingHandler.AwaitIdle);
                        break;

                    case StreamingState.Stop:
                        if (GrblInfo.IsGrblHAL)
                            SetStreamingHandler(StreamingHandler.Idle);
                        else
                        {
                            newState = StreamingState.Paused;
                            SetStreamingHandler(StreamingHandler.AwaitAction);
                        }
                        break;
                }
            }

            if (streamingHandler.Handler != StreamingHandler.SendFile)
                return streamingHandler.Call(newState, true);
            else if (changed)
            {
                model.StreamingState = streamingState = newState;
                StreamingStateChanged?.Invoke(streamingState, grblState.MPG);
            }

            return true;
        }

        public bool StreamingAwaitAction(StreamingState newState, bool always)
        {
            bool changed = streamingState != newState || newState == StreamingState.Idle;

            if (changed || always)
            {
                switch (newState)
                {
                    case StreamingState.Idle:
                        btnStart.IsEnabled = !GrblInfo.IsGrblHAL;
                        break;

                    case StreamingState.Stop:
                        if (GrblInfo.IsGrblHAL) {
                            if(!model.GrblReset)
                                Comms.com.WriteByte(GrblConstants.CMD_STOP);
                        } else if(grblState.State == GrblStates.Run)
                            Comms.com.WriteByte(GrblConstants.CMD_RESET);
                        newState = StreamingState.Idle;
                        SetStreamingHandler(StreamingHandler.AwaitIdle);
                        break;

                    case StreamingState.Paused:
                        btnStart.IsEnabled = false;
                        btnHold.IsEnabled = false;
                        btnStop.IsEnabled = true;
                        if (job.ACKPending == 0)
                            streamingHandler.Count = false;
                        break;

                    case StreamingState.Send:
                        SetStreamingHandler(StreamingHandler.SendFile);
                        SendNextLine();
                        break;

                    case StreamingState.JobFinished:
                        SetStreamingHandler(StreamingHandler.SendFile);
                        break;
                }
            }

            if (streamingHandler.Handler != StreamingHandler.AwaitAction)
                return streamingHandler.Call(newState, true);
            else if (changed)
            {
                model.StreamingState = streamingState = newState;
                StreamingStateChanged?.Invoke(streamingState, grblState.MPG);
            }

            return true;
        }

        public bool StreamingAwaitIdle(StreamingState newState, bool always)
        {
            bool changed = streamingState != newState || newState == StreamingState.Idle;

            if (changed || always)
            {
                switch (newState)
                {
                    case StreamingState.Idle:
                        model.RunTime = JobTimer.RunTime;
                        JobTimer.Stop();
                        RewindFile();
                        SetStreamingHandler(StreamingHandler.Idle);
                        break;

                    case StreamingState.Error:
                    case StreamingState.Halted:
                        btnStart.IsEnabled = !GrblInfo.IsGrblHAL;
                        btnHold.IsEnabled = false;
                        btnStop.IsEnabled = true;
                        break;

                    case StreamingState.Send:
                        btnStart.IsEnabled = false;
                        btnHold.IsEnabled = true;
                        btnStop.IsEnabled = true;
                        btnRewind.IsEnabled = false;
                        break;

                    case StreamingState.FeedHold:
                        SetStreamingHandler(StreamingHandler.FeedHold);
                        break;

                    case StreamingState.Stop:
                        SetStreamingHandler(StreamingHandler.Idle);
                        break;
                }
            }

            if (streamingHandler.Handler != StreamingHandler.AwaitIdle)
                return streamingHandler.Call(newState, true);
            else if (changed)
            {
                model.StreamingState = streamingState = newState;
                StreamingStateChanged?.Invoke(streamingState, grblState.MPG);
            }

            return true;
        }

        public bool StreamingIdle(StreamingState newState, bool always)
        {
            bool changed = streamingState != newState || newState == StreamingState.Idle;

            if (changed || always)
            {
                switch (newState)
                {
                    case StreamingState.Disabled:
                        IsEnabled = false;
                        break;

                    case StreamingState.Idle:
                    case StreamingState.NoFile:
                        IsEnabled = !grblState.MPG;
                        btnStart.IsEnabled = GCode.File.IsLoaded;
                        btnStop.IsEnabled = false;
                        btnHold.IsEnabled = !grblState.MPG;
                        btnRewind.IsEnabled = !grblState.MPG && GCode.File.IsLoaded && job.CurrLine != 0;
                        model.IsJobRunning = JobTimer.IsRunning;
                        break;

                    case StreamingState.Send:
                        if (!string.IsNullOrEmpty(model.FileName) && !grblState.MPG)
                            model.IsJobRunning = true;
                        if (JobTimer.IsRunning)
                            SetStreamingHandler(StreamingHandler.SendFile);
                        else
                        {
                            btnStop.IsEnabled = true;
                            btnHold.IsEnabled = !grblState.MPG;
                        }
                        break;

                    case StreamingState.Start: // Streaming from SD Card
                        job.IsSDFile = true;
                        JobTimer.Start();
                        break;

                    case StreamingState.Error:
                    case StreamingState.Halted:
                        btnStart.IsEnabled = !grblState.MPG;
                        btnHold.IsEnabled = false;
                        btnStop.IsEnabled = !grblState.MPG;
                        break;

                    case StreamingState.FeedHold:
                        SetStreamingHandler(StreamingHandler.FeedHold);
                        break;

                    case StreamingState.ToolChange:
                        SetStreamingHandler(StreamingHandler.ToolChange);
                        break;

                    case StreamingState.Stop:
                        btnHold.IsEnabled = !(grblState.MPG || grblState.State == GrblStates.Alarm);
                        btnStart.IsEnabled = btnHold.IsEnabled && GCode.File.IsLoaded; //!GrblInfo.IsGrblHAL;
                        btnStop.IsEnabled = false;
                        btnRewind.IsEnabled = false;
                        job.IsSDFile = false;
                        model.IsJobRunning = false;
                        if (!grblState.MPG)
                        {
                            if (GrblInfo.IsGrblHAL && !(grblState.State == GrblStates.Home || grblState.State == GrblStates.Alarm))
                            {
                                if (!model.GrblReset)
                                    Comms.com.WriteByte(GrblConstants.CMD_STOP);
                            }
                            else if (grblState.State == GrblStates.Hold && !model.GrblReset)
                                Comms.com.WriteByte(GrblConstants.CMD_RESET);
                        }
                        if (JobTimer.IsRunning)
                        {
                            always = false;
                            model.StreamingState = streamingState = streamingState == StreamingState.Error ? StreamingState.Idle : newState;
                            SetStreamingHandler(StreamingHandler.AwaitIdle);
                        } else if(grblState.State != GrblStates.Alarm)
                            return streamingHandler.Call(StreamingState.Idle, true);
                        break;
                }
            }

            if (streamingHandler.Handler != StreamingHandler.Idle)
                return streamingHandler.Call(newState, always);
            else if (changed)
            {
                model.StreamingState = streamingState = newState;
                StreamingStateChanged?.Invoke(streamingState, grblState.MPG);
            }

            return true;
        }

        void GrblStateChanged(GrblState newstate)
        {
            if (grblState.State == GrblStates.Jog)
                model.IsJobRunning = false;

            if (isActive) switch(newstate.State)
            {
                case GrblStates.Idle:
                    streamingHandler.Call(StreamingState.Idle, true);
                    break;

                case GrblStates.Jog:
                    model.IsJobRunning = !model.IsToolChanging;
                    break;

                //case GrblStates.Check:
                //    streamingHandler.Call(StreamingState.Send, false);
                //    break;

                case GrblStates.Run:
                    if (JobTimer.IsPaused)
                        JobTimer.Pause = false;
                    if (model.StreamingState != StreamingState.Error)
                        streamingHandler.Call(StreamingState.Send, false);
                    if (newstate.Substate == 1)
                    {
                        btnStart.IsEnabled = !grblState.MPG;
                        btnHold.IsEnabled = false;
                    }
                    else if (grblState.Substate == 1)
                    {
                        btnStart.IsEnabled = false;
                        btnHold.IsEnabled = !grblState.MPG;
                    }
                    break;

                case GrblStates.Tool:
                        if (grblState.State != GrblStates.Jog)
                        {
                            if (JobTimer.IsRunning)
                                ResponseReceived("pending");
                            streamingHandler.Call(StreamingState.ToolChange, false);
                            if (!grblState.MPG)
                                Comms.com.WriteByte(GrblConstants.CMD_TOOL_ACK);
                        }
                    break;

                case GrblStates.Hold:
                    streamingHandler.Call(StreamingState.FeedHold, false);
                    break;

                case GrblStates.Door:
                    if (newstate.Substate > 0)
                    {
                        if (streamingState == StreamingState.Send)
                            streamingHandler.Call(StreamingState.FeedHold, false);
                    }
                    break;

                case GrblStates.Alarm:
                    streamingHandler.Call(StreamingState.Stop, false);
                    break;
            }

            grblState.State = newstate.State;
            grblState.Substate = newstate.Substate;
            grblState.MPG = newstate.MPG;
        }

        private void ResponseReceived(string response)
        {
            if (streamingHandler.Count)
            {
                if (job.ACKPending > 0 && response != "pending")
                    job.ACKPending--;

                if (!job.IsSDFile && (string)GCode.File.Data.Rows[job.PendingLine]["Sent"] == "*")
                    job.serialUsed = Math.Max(0, job.serialUsed - (int)GCode.File.Data.Rows[job.PendingLine]["Length"]);

                //if (streamingState == StreamingState.Send || streamingState == StreamingState.Paused)
                //{
                bool isError = response.StartsWith("error");

                if (!job.IsSDFile)
                {
                    GCode.File.Data.Rows[job.PendingLine]["Sent"] = response;

                    if (job.PendingLine > 5)
                    {
                        if (grblState.State != GrblStates.Check || isError || (job.PendingLine % 50) == 0)
                            model.ScrollPosition = job.PendingLine - 5;
                    }

                    if(streamingHandler.Call == StreamingAwaitAction)
                    {
                        streamingHandler.Count = false;
                    }

                }
                if (isError)
                    streamingHandler.Call(StreamingState.Error, true);
                else if (job.PgmEndLine == job.PendingLine)
                    streamingHandler.Call(StreamingState.JobFinished, true);
                else if(streamingHandler.Count && response == "ok")
                    SendNextLine();
                //}

                if (!job.Complete)
                {
                    job.PendingLine++;
                    model.BlockExecuting = job.PendingLine;
                }
            }
            else if (response == "ok")
                missed++;

            switch (streamingState)
            {
                case StreamingState.Send:
                    if(response == "start")
                        SendNextLine();
                    break;

                case StreamingState.SendMDI:
                    if (GCode.File.Commands.Count > 0)
                        Comms.com.WriteCommand(GCode.File.Commands.Dequeue());
                    if (GCode.File.Commands.Count == 0)
                        streamingState = StreamingState.Idle;
                    break;

                case StreamingState.Reset:
                    Comms.com.WriteCommand(GrblConstants.CMD_UNLOCK);
                    streamingState = StreamingState.AwaitResetAck;
                    break;

                case StreamingState.AwaitResetAck:
                    streamingHandler.Call(GCode.File.IsLoaded ? StreamingState.Idle : StreamingState.NoFile, false);
                    break;
            }
        }

        void SendNextLine()
        {
            while (job.NextRow != null) {

                string line = (string)job.NextRow["Data"]; //  GCodeUtils.StripSpaces((string)currentRow["Data"]);

                // Send comment lines as empty comment
                if ((bool)job.NextRow["IsComment"])
                {
                    line = "()";
                    job.NextRow["Length"] = line.Length + 1;
                }

                if (job.serialUsed < (serialSize - (int)job.NextRow["Length"]))
                {

                    if (GCode.File.Commands.Count > 0)
                        Comms.com.WriteCommand(GCode.File.Commands.Dequeue());
                    else
                    {
                        job.CurrentRow = job.NextRow;

                        job.CurrentRow["Sent"] = "*";
                        if (line == "%")
                        {
                            if (!(job.Started = !job.Started))
                                job.PgmEndLine = job.CurrLine;
                        }
                        else if ((bool)job.CurrentRow["ProgramEnd"])
                            job.PgmEndLine = job.CurrLine;
                        job.NextRow = job.PgmEndLine == job.CurrLine ? null : GCode.File.Data.Rows[++job.CurrLine];
                        //            ParseBlock(line + "\r");
                        job.serialUsed += (int)job.CurrentRow["Length"];
                        Comms.com.WriteString(line + '\r');
                    }
                    job.ACKPending++;

                    if (!useBuffering)
                        break;
                }
                else
                    break;
            }
        }
    }
}
