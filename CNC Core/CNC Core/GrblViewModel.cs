/*
 * GrblViewModel.cs - part of CNC Controls library
 *
 * v0.29 / 2021-03-25 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2019-2021, Io Engineering (Terje Io)
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
using System.Linq;
using System.Windows.Media;
using System.Collections.ObjectModel;
using System.Windows.Input;
using CNC.GCode;
using System.Threading;

namespace CNC.Core
{
    public class GrblViewModel : MeasureViewModel
    {
        private string _tool, _message, _WPos, _MPos, _wco, _wcs, _a, _fs, _ov, _pn, _sc, _sd, _ex, _d, _gc, _h, _thcv, _thcs;
        private string _mdiCommand, _fileName;
        private bool has_wco = false, suspend = false;
        private bool _flood, _mist, _tubeCoolant, _toolChange, _reset, _isMPos, _isJobRunning, _isProbeSuccess, _pgmEnd, _isParserStateLive, _isTloRefSet;
        private bool _canReset;
        private bool? _mpg;
        private int _pwm, _line, _scrollpos, _blocks = 0, _executingBlock = 0;
        private double _feedrate = 0d;
        private double _rpm = 0d, _rpmInput = 0d, _rpmDisplay = 0d, _jogStep = 0.1d, _tloReferenceOffset = double.NaN;
        private double _rpmActual = double.NaN;
        private double _feedOverride = 100d;
        private double _rapidsOverride = 100d;
        private double _rpmOverride = 100d;
        private double _thcVoltage = double.NaN;
        private string _pb_avail, _rxb_avail;
        private GrblState _grblState;
        private LatheMode _latheMode = LatheMode.Disabled;
        private HomedState _homedState = HomedState.Unknown;
        private GrblEncoderMode _encoder_ovr = GrblEncoderMode.Unknown;
        private StreamingState _streamingState;
        public SpindleState _spindleStatePrev = GCode.SpindleState.Off;

        private Thread pollThread = null;

        public Action<string> OnCommandResponseReceived;
        public Action<string> OnResponseReceived;
        public Action<string> OnGrblReset;
        public Action<string> OnRealtimeStatusProcessed;
        public Action<string> OnWCOUpdated;

        public delegate void GrblResetHandler();

        public GrblViewModel()
        {
            _a = _pn = _fs = _sc = _tool = string.Empty;

            Clear();

            MDICommand = new ActionCommand<string>(ExecuteMDI);

            pollThread = new Thread(new ThreadStart(Poller.Run));
            pollThread.Start();

            _grblState.LastAlarm = 0;

            Signals.PropertyChanged += Signals_PropertyChanged;
            THCSignals.PropertyChanged += THCSignals_PropertyChanged;
            OptionalSignals.PropertyChanged += OptionalSignals_PropertyChanged;
            SpindleState.PropertyChanged += SpindleState_PropertyChanged;
            AxisScaled.PropertyChanged += AxisScaled_PropertyChanged;
            Position.PropertyChanged += Position_PropertyChanged;
            MachinePosition.PropertyChanged += MachinePosition_PropertyChanged;
            ProbePosition.PropertyChanged += ProbePosition_PropertyChanged;
            ToolOffset.PropertyChanged += ToolOffset_PropertyChanged;
        }

        ~GrblViewModel()
        {
            pollThread.Abort();
        }

        private void ToolOffset_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Core.Position))
                OnPropertyChanged(nameof(ToolOffset));
        }

        private void ProbePosition_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if(e.PropertyName == nameof(Core.Position))
                OnPropertyChanged(nameof(ProbePosition));
        }

        private void Position_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Core.Position))
                OnPropertyChanged(nameof(Position));
        }

        private void MachinePosition_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Core.Position))
                OnPropertyChanged(nameof(MachinePosition));
        }

        private void AxisScaled_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(AxisScaled));
        }

        private void SpindleState_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _rpmDisplay = _spindleStatePrev == GCode.SpindleState.Off ? _rpmInput : _rpm;
            if (!(SpindleState.Value.HasFlag(GCode.SpindleState.Off | GCode.SpindleState.CW) || SpindleState.Value.HasFlag(GCode.SpindleState.Off | GCode.SpindleState.CCW)))
            {
                OnPropertyChanged(nameof(SpindleState));
                OnPropertyChanged(nameof(RPM));
                _spindleStatePrev = SpindleState.Value;
            }
        }

        private void Signals_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(Signals));
        }

        private void THCSignals_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(THCSignals));
        }

        private void OptionalSignals_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(OptionalSignals));
        }

        public void Clear()
        {
            _fileName = _mdiCommand = string.Empty;
            _streamingState = StreamingState.NoFile;
            _isMPos = _reset = _isJobRunning = _isProbeSuccess = _pgmEnd = _isTloRefSet = false;
            _canReset = true;
            _pb_avail = _rxb_avail = string.Empty;
            _mpg = null;
            _line = _pwm = _scrollpos = 0;

            _grblState.Error = 0;
            _grblState.State = GrblStates.Unknown;
            _grblState.Substate = 0;
            _grblState.MPG = false;
            GrblState = _grblState;
            IsMPGActive = null; //??

            has_wco = false;
            _MPos = _WPos = _wco = _h = string.Empty;
            Position.Clear();
            MachinePosition.Clear();
            WorkPosition.Clear();
            WorkPositionOffset.Clear();
            ProgramLimits.Clear();

            Set("Pn", string.Empty);
            Set("A", string.Empty);
            Set("FS", string.Empty);
            Set("Sc", string.Empty);
            Set("T", "0");
            Set("Ov", string.Empty);
            Set("Ex", string.Empty);
            SDCardStatus = string.Empty;
            HomedState = HomedState.Unknown;
            if (_latheMode != LatheMode.Disabled)
                LatheMode = LatheMode.Radius;

            _thcv = _thcs = string.Empty;
        }

        public PollGrbl Poller { get; } = new PollGrbl();

        public ICommand MDICommand { get; private set; }

        private bool ApplyCommand(string command)
        {
            bool ok;
            string ucmd = command.ToUpper();

            if ((ok = !(GrblState.State == GrblStates.Tool && !(ucmd.StartsWith("$J=") || ucmd == "$TPW" || ucmd.Contains("G10L20")))))
                MDI = command;
            else
                Message = "Only jogging and some system commands are allowed when changing tool!";

            return ok;
        }

        private void ExecuteMDI(string command)
        {
            if (!string.IsNullOrEmpty(command) && ApplyCommand(command))
            {
                if (command.Length > 1)
                {
                    CommandLog.Add(command);
                    ResponseLog.Add(command);
                }
            }
        }

        public void ExecuteCommand(string command)
        {
            if (command != null)
            {
                if (command == string.Empty)
                {
                    MDI = command;
                    SetGrblError(0);
                }
                else if (ApplyCommand(command))
                {
                    if (ResponseLogVerbose && !string.IsNullOrEmpty(command))
                        ResponseLog.Add(command);
                }
            }
        }
        public bool ResponseLogVerbose { get; set; } = false;
        public bool ResponseLogFilterRT { get; set; } = false;
        public bool ResponseLogFilterOk { get; set; } = false;

        public bool IsReady { get; set; } = false;
        public bool IsGrblHAL { get { return GrblInfo.IsGrblHAL; } }
        public string Firmware { get; set; } = string.Empty;
        public bool SuspendProcessing { get; set; } = false;

        public bool LimitTriggered {
            get {
                return Signals.Value.HasFlag(Core.Signals.LimitX) ||
                        Signals.Value.HasFlag(Core.Signals.LimitY) ||
                         Signals.Value.HasFlag(Core.Signals.LimitZ) ||
                          Signals.Value.HasFlag(Core.Signals.LimitA) ||
                           Signals.Value.HasFlag(Core.Signals.LimitB) ||
                            Signals.Value.HasFlag(Core.Signals.LimitC);
            }
        }

        #region Dependencyproperties

        public ObservableCollection<string> ResponseLog { get; private set; } = new ObservableCollection<string>();
        public ObservableCollection<string> CommandLog { get; private set; } = new ObservableCollection<string>();

        public ProgramLimits ProgramLimits { get; private set; } = new ProgramLimits();
        public string MDI { get { string cmd = _mdiCommand; _mdiCommand = string.Empty; return cmd; } private set { _mdiCommand = value; OnPropertyChanged(); } }
        public ObservableCollection<CoordinateSystem> CoordinateSystems { get { return GrblWorkParameters.CoordinateSystems; } }
        public ObservableCollection<Tool> Tools { get { return GrblWorkParameters.Tools; } }
        public ObservableCollection<string> SystemInfo { get { return GrblInfo.SystemInfo; } }
        public string Tool { get { return _tool; } set { _tool = GrblParserState.Tool = value; OnPropertyChanged(); } }
        public double TloReference { get { return _tloReferenceOffset; } private set { _tloReferenceOffset = value; OnPropertyChanged(); } }
        public bool IsTloReferenceSet {
            get { return _isTloRefSet; }
            private set
            {
                if (_isTloRefSet != value)
                {
                    if (_isTloRefSet)
                        TloReference = double.NaN;
                    _isTloRefSet = value;
                    OnPropertyChanged();
                }
            }
        }

//        public bool CanReset { get { return _canReset; } private set { if(value != _canReset) { _canReset = value; OnPropertyChanged(); } } }
        public bool GrblReset { get { return _reset; } set { if ((_reset = value)) { _grblState.Error = 0; OnPropertyChanged(); Message = ""; } } }
        public GrblState GrblState { get { return _grblState; } set { _grblState = value; OnPropertyChanged(); } }
        public bool IsCheckMode { get { return _grblState.State == GrblStates.Check; } }
        public bool IsSleepMode { get { return _grblState.State == GrblStates.Sleep; } }
        public bool IsG92Active { get { return GrblParserState.IsActive("G92") != null; } }
        public bool IsToolOffsetActive { get { return IsGrblHAL ? GrblParserState.IsActive("G49") == null : !(double.IsNaN(ToolOffset.Z) || ToolOffset.Z == 0d); } }
        public bool IsJobRunning { get { return _isJobRunning; } set { if (_isJobRunning != value) { _isJobRunning = value; OnPropertyChanged(); } } }
        public bool ProgramEnd { get { return _pgmEnd; } set { _pgmEnd = value; if(_pgmEnd) OnPropertyChanged(); } }
        public int GrblError { get { return _grblState.Error; } set { _grblState.Error = value; OnPropertyChanged(); } }
        public StreamingState StreamingState { get { return _streamingState; } set { if (_streamingState != value) { _streamingState = value; OnPropertyChanged(); } } }
        public string WorkCoordinateSystem { get { return _wcs; } private set { _wcs = value; OnPropertyChanged(); } }
        public Position MachinePosition { get; private set; } = new Position();
        public Position WorkPosition { get; private set; } = new Position();
        public Position Position { get; private set; } = new Position();
        public bool IsMachinePosition { get { return _isMPos; } private set { _isMPos = value; OnPropertyChanged(); } }
        public bool IsMachinePositionKnown { get { return MachinePosition.IsSet(GrblInfo.AxisFlags); } }
        public bool SuspendPositionNotifications
        {
            get { return Position.SuspendNotifications; }
            set { Position.SuspendNotifications = value; }
        }

        public EnumFlags<AxisFlags> AxisHomed { get; private set; } = new EnumFlags<AxisFlags>(AxisFlags.None);
        public Position HomePosition { get; private set; } = new Position();

        public Position WorkPositionOffset { get; private set; } = new Position();
        public Position ToolOffset { get; private set; } = new Position();
        public Position ProbePosition { get; private set; } = new Position();
        public bool IsProbeSuccess { get { return _isProbeSuccess; } private set { _isProbeSuccess = value; OnPropertyChanged(); } }
        public EnumFlags<SpindleState> SpindleState { get; private set; } = new EnumFlags<SpindleState>(GCode.SpindleState.Off);
        public EnumFlags<Signals> Signals { get; private set; } = new EnumFlags<Signals>(Core.Signals.Off);
        public EnumFlags<Signals> OptionalSignals { get; set; } = new EnumFlags<Signals>(Core.Signals.Off);
        public EnumFlags<AxisFlags> AxisScaled { get; private set; } = new EnumFlags<AxisFlags>(AxisFlags.None);
        public string FileName { get { return _fileName; } set { _fileName = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsFileLoaded)); OnPropertyChanged(nameof(IsPhysicalFileLoaded)); } }
        public bool IsFileLoaded { get { return _fileName != string.Empty; } }
        public int Blocks
        {
            get { return _blocks; }
            set
            {
                _blocks = value;
                if (value == 0)
                    BlockExecuting = 0;
                OnPropertyChanged();
            }
        }
        public int BlockExecuting { get { return _executingBlock; } set { _executingBlock = value; OnPropertyChanged(); } }
        public bool IsPhysicalFileLoaded { get { return _fileName != string.Empty && (_fileName.StartsWith(@"\\") || _fileName[1] == ':'); } }
        public bool? IsMPGActive { get { return _mpg; } private set { if (_mpg != value) { _mpg = value; OnPropertyChanged(); } } }
        public string Scaling { get { return _sc; } private set { _sc = value; OnPropertyChanged(); } }
        public string SDCardStatus { get { return _sd; } private set { _sd = value; OnPropertyChanged(); } }
        public HomedState HomedState { get { return _homedState; } private set { _homedState = value; OnPropertyChanged(); } }
        public LatheMode LatheMode
        {
            get { return _latheMode; }
            private set
            {
                if (_latheMode != value)
                {
                    _latheMode = value;
                    if(_latheMode != LatheMode.Disabled)
                    {
                        Position.Y = MachinePosition.Y = WorkPosition.Y = WorkPositionOffset.Y = 0d;
                    }
                    OnPropertyChanged();
                }
            }
        }
        public bool LatheModeEnabled { get { return GrblInfo.LatheModeEnabled; } set { OnPropertyChanged(); } }
        public int NumAxes { get { return GrblInfo.NumAxes; } set { OnPropertyChanged(); } }
        public AxisFlags AxisEnabledFlags {
            get { return GrblInfo.AxisFlags; }
            set {
                OnPropertyChanged();
                if (_isMPos)
                {
                    if (has_wco)
                        Position.Set(MachinePosition - WorkPositionOffset);
                    else
                        Position.Set(MachinePosition);
                }
                else
                {
                    if (has_wco)
                        MachinePosition.Set(WorkPosition + WorkPositionOffset);
                    else if(WorkPosition.IsSet(GrblInfo.AxisFlags))
                        Position.Set(WorkPosition);
                }
            }
        }
        public int ScrollPosition { get { return _scrollpos; } set { _scrollpos = value;  OnPropertyChanged(); } }
        public double JogStep { get { return _jogStep; } set { _jogStep = value; OnPropertyChanged(); } }
        public GrblEncoderMode OverrideEncoderMode { get { return _encoder_ovr; } set { _encoder_ovr = value; OnPropertyChanged(); } }

        public string RunTime { get { return JobTimer.RunTime; } set { OnPropertyChanged(); } } // Cannot be set...
        // CO2 Laser
        public bool TubeCoolant { get { return _tubeCoolant; }  set { _tubeCoolant = value; OnPropertyChanged(); } }
        //
        public int LineNumber { get { return _line; } private set { _line = value; OnPropertyChanged(); } }

        public double THCVoltage { get { return _thcVoltage; } private set { _thcVoltage = value; OnPropertyChanged(); } }
        public EnumFlags<THCSignals> THCSignals { get; private set; } = new EnumFlags<THCSignals>(Core.THCSignals.Off);

        #region A - Spindle, Coolant and Tool change status

        public bool Mist
        {
            get { return _mist; }
            set
            {
                if (_mist != value)
                {
                    _mist = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool Flood
        {
            get { return _flood; }
            set
            {
                if (_flood != value)
                {
                    _flood = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsToolChanging
        {
            get { return _toolChange; }
            set
            {
                if (_toolChange != value)
                {
                    _toolChange = value;
                    OnPropertyChanged();
                }
            }
        }

        #endregion

        #region FS - Feed and Speed (RPM)

        public double FeedRate { get { return _feedrate; } private set { _feedrate = value; OnPropertyChanged(); } }
        public double ProgrammedRPM
        {
            get { return _rpm; }
            set {
                if (_rpm != value)
                {
                    _rpm = value;
                    OnPropertyChanged();

                    if (_rpm != 0d)
                        _rpmInput = _rpm / (RPMOverride / 100d);
                    else if (!IsGrblHAL && (_a == "S" || _a == "C")) // Hack for legacy Grbl no informing about spindle going off
                    {
                        _a = "";
                        SpindleState.Value = GCode.SpindleState.Off;
                    }

                    if (double.IsNaN(ActualRPM))
                    {
                        _rpmDisplay = _rpm == 0d ? _rpmInput : _rpm;
                        OnPropertyChanged(nameof(RPM));
                    }
                }
            }
        }
        public double ActualRPM
        {
            get { return _rpmActual; }
            private set
            {
                if (_rpmActual != value)
                {
                    _rpmActual = value;
                    OnPropertyChanged();
                    if (!double.IsNaN(ActualRPM))
                    {
                        _rpmDisplay = _rpmActual == 0d ? _rpmInput : _rpmActual;
                        OnPropertyChanged(nameof(RPM));
                    }
                }
            }
        }
        public double RPM {
            get { return _rpmDisplay; }
            set { _rpmDisplay = _rpmInput = value; OnPropertyChanged(); }
        }

        public int PWM { get { return _pwm; } private set { _pwm = value; OnPropertyChanged(); } }
        #endregion

        #region Ov - Feed and spindle overrides

        public double FeedOverride { get { return _feedOverride; } private set { _feedOverride = value; OnPropertyChanged(); } }
        public double RapidsOverride { get { return _rapidsOverride; } private set { _rapidsOverride = value; OnPropertyChanged(); } }
        public double RPMOverride { get { return _rpmOverride; } private set { _rpmOverride = value; OnPropertyChanged(); } }

        #endregion

        #region Ov - Buffer information

        public int PlanBufferSize { get { return GrblInfo.PlanBufferSize; } }
        public int PlanBufferAvailable { get { return int.Parse(_pb_avail); } }

        public int RxBufferSize { get { return GrblInfo.SerialBufferSize; } }
        public int RxBufferAvailable { get { return int.Parse(_rxb_avail); } }

        #endregion

        public bool Silent { get; set; } = false;
        public string Message
        {
            get { return _message == null ? string.Empty : _message; }
            set
            {
                if (_message != value)
                {
                    _message = value;
                    if(!Silent)
                        OnPropertyChanged();
                }
            }
        }

        public string ParserState
        {
            get { return _gc; }
            set
            {
                _gc = value;
                if (GrblParserState.WorkOffset != _wcs)
                    WorkCoordinateSystem = GrblParserState.WorkOffset;
                if (GrblParserState.Tool != _tool)
                    Tool = GrblParserState.Tool;
                if (GrblParserState.LatheMode != _latheMode)
                    LatheMode = GrblParserState.LatheMode;
                if (GrblParserState.IsActive("G51") != null)
                    Set("Sc", GrblParserState.IsActive("G51"));
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsG92Active));
                OnPropertyChanged(nameof(IsToolOffsetActive));
            }
        }

        public bool IsParserStateLive { get { return _isParserStateLive; } set { _isParserStateLive = value; OnPropertyChanged(); } }

        #endregion

        public bool SetGRBLState(string newState, int substate, bool force)
        {
            GrblStates newstate = _grblState.State;

            Enum.TryParse(newState, true, out newstate);

            if (newstate != _grblState.State || substate != _grblState.Substate || force)
            {
                bool checkChanged = _grblState.State == GrblStates.Check || newstate == GrblStates.Check;
                bool sleepChanged = _grblState.State == GrblStates.Sleep || newstate == GrblStates.Sleep;

                if (_grblState.State == GrblStates.Door && newstate != GrblStates.Door)
                    Message = string.Empty;

                if (newstate == GrblStates.Alarm && substate > 0)
                    _grblState.LastAlarm = substate;

                _grblState.State = newstate;
                _grblState.Substate = substate;

                //                force = true;

                switch (_grblState.State)
                {

                    case GrblStates.Run:
                        _grblState.Color = Colors.LightGreen;
                        break;

                    case GrblStates.Alarm:
                        _grblState.Color = Colors.Red;
                        break;

                    case GrblStates.Jog:
                        _grblState.Color = Colors.Yellow;
                        break;

                    case GrblStates.Tool:
                        _grblState.Color = Colors.LightSalmon;
                        break;

                    case GrblStates.Hold:
                        _grblState.Color = Colors.LightSalmon;
                        break;

                    case GrblStates.Door:
                        if (_grblState.Substate > 0)
                            _grblState.Color = _grblState.Substate == 1 ? Colors.Red : Colors.LightSalmon;
                        break;

                    case GrblStates.Home:
                    case GrblStates.Sleep:
                        _grblState.Color = Colors.LightSkyBlue;
                        break;

                    case GrblStates.Check:
                        _grblState.Color = Colors.White;
                        break;

                    default:
                        _grblState.Color = Colors.White;
                        break;
                }

                OnPropertyChanged(nameof(GrblState));

//                CanReset = canReset();

                if (checkChanged || force)
                    OnPropertyChanged(nameof(IsCheckMode));

                if (sleepChanged || force)
                    OnPropertyChanged(nameof(IsSleepMode));

                if (newstate == GrblStates.Sleep)
                    Message += ", " + "<Reset> to continue";
                else if (newstate == GrblStates.Alarm)
                {
                    if (substate == 11)
                        HomedState = HomedState.NotHomed;
                }
            }

            return force;
        }

        public void SetGrblError(int error)
        {
            GrblError = error;
            Message = error == 0 ? string.Empty : GrblErrors.GetMessage(error.ToString());
        }

        public bool ParseGCStatus(string data)
        {
            GrblParserState.Process(data);
            if (GrblParserState.IsLoaded)
                ParserState = data.Substring(4).TrimEnd(']');

            return GrblParserState.IsLoaded;
        }

        public bool ParseProbeStatus(string data)
        {
            string[] values = data.TrimEnd(']').Split(':');
            if (values.Length == 3)
            {
                ProbePosition.Parse(values[1]);
                IsProbeSuccess = values[2] == "1";
                for (int i = 0; i < GrblInfo.NumAxes; i++)
                    GrblWorkParameters.ProbePosition.Values[i] = ProbePosition.Values[i];
            }

            return IsProbeSuccess && values.Length == 3;
        }

        public void ParseHomedStatus(string data)
        {
            string[] values = data.TrimEnd(']').Split(':');
            if (values.Length == 3)
            {
                HomePosition.Parse(values[1]);
                AxisHomed.Value = (AxisFlags)int.Parse(values[2]);
                for (int i = 0; i < GrblInfo.NumAxes; i++)
                {
                    if(!AxisHomed.Value.HasFlag(GrblInfo.AxisIndexToFlag(i)))
                        HomePosition.Values[i] = double.NaN;
                }
            }
        }

        public void ParseStatus(string data)
        {
            bool pos_changed = false;
            string[] elements = data.TrimEnd('>').Split('|');

            if (elements.Length > 1)
            {
                string[] pair = elements[0].Split(':');
                SetGRBLState(pair[0].Substring(1), pair.Count() == 1 ? -1 : int.Parse(pair[1]), false);

                for (int i = elements.Length - 1; i > 0; i--)
                {
                    pair = elements[i].Split(':');

                    if (pair.Length == 2)
                    {
                        if(Set(pair[0], pair[1]))
                            pos_changed = true;
                    }
                }

                if (!data.Contains("|Pn:"))
                    Set("Pn", "");

                if (pos_changed)
                {
                    if(_isMPos)
                    {
                        if (has_wco)
                            Position.Set(MachinePosition - WorkPositionOffset);
                        else
                            Position.Set(MachinePosition);
                    }
                    else
                    {
                        if (has_wco)
                            MachinePosition.Set(WorkPosition + WorkPositionOffset);
                        Position.Set(WorkPosition);
                    }
                }
            }
        }

        private bool canReset ()
        {
            return !(GrblState.State == GrblStates.Door || (GrblState.State == GrblStates.Alarm && GrblState.Substate == 11) || Signals.Value.HasFlag(Core.Signals.EStop));
        }

        private bool Set(string parameter, string value)
        {
            bool pos_changed = false;

            switch (parameter)
            {
                case "MPos":
                    if ((pos_changed = _MPos != value))
                    {
                        if (!_isMPos)
                            IsMachinePosition = true;
                        _MPos = value;
                        MachinePosition.Parse(_MPos);
                    }
                    break;

                case "WPos":
                    if ((pos_changed = _WPos != value))
                    {
                        if (_isMPos)
                            IsMachinePosition = false;
                        _WPos = value;
                        WorkPosition.Parse(_WPos);
                    }
                    break;

                case "WCO":
                    if ((pos_changed = _wco != value))
                    {
                        _wco = value;
                        has_wco = true;
                        WorkPositionOffset.Parse(value);
                    }
                    OnWCOUpdated?.Invoke(_wco);
                    break;

                case "A":
                    if (_a != value)
                    {
                        _a = value;

                        if (_a == "")
                        {
                            Mist = Flood = IsToolChanging = false;
                            SpindleState.Value = GCode.SpindleState.Off;
                        }
                        else
                        {
                            Mist = value.Contains("M");
                            Flood = value.Contains("F");
                            IsToolChanging = value.Contains("T");
                            SpindleState.Value = value.Contains("S") ? GCode.SpindleState.CW : (value.Contains("C") ? GCode.SpindleState.CCW : GCode.SpindleState.Off);
                        }
                    }
                    break;

                case "WCS":
                    if (_wcs != value)
                        WorkCoordinateSystem = GrblParserState.WorkOffset = value;
                    break;

                case "Bf":
                    string[] buffers = value.Split(',');
                    if(buffers[0] != _pb_avail)
                    {
                        _pb_avail = buffers[0];
                        OnPropertyChanged(nameof(PlanBufferAvailable));
                    }
                    if (buffers[1] != _rxb_avail)
                    {
                        _rxb_avail = buffers[1];
                        OnPropertyChanged(nameof(RxBufferAvailable));
                    }
                    break;

                case "Ln":
                    LineNumber = int.Parse(value);
                    break;

                case "FS":
                    if (_fs != value)
                    {
                        _fs = value;
                        if (_fs == "")
                        {
                            FeedRate = ProgrammedRPM = 0d;
                            if (!double.IsNaN(ActualRPM))
                                ActualRPM = 0d;
                        }
                        else
                        {
                            double[] values = dbl.ParseList(_fs);
                            if (_feedrate != values[0])
                                FeedRate = values[0];
                            if (_rpm != values[1])
                                ProgrammedRPM = values[1];
                            if (values.Length > 2 && _rpmActual != values[2])
                                ActualRPM = values[2];
                        }
                    }
                    break;

                case "F":
                    if (_fs != value)
                    {
                        _fs = value;
                        if (_fs == "")
                        {
                            FeedRate = ProgrammedRPM = 0d;
                            if (!double.IsNaN(ActualRPM))
                                ActualRPM = 0d;
                        }
                        else
                            FeedRate = dbl.Parse(_fs);
                    }
                    break;

                case "FW":
                    Firmware = value;
                    break;

                case "PWM":
                    PWM = int.Parse(value);
                    break;

                case "Pn":
                    if (_pn != value)
                    {
                        _pn = value;

                        int s = 0;
                        foreach (char c in _pn)
                        {
                            int i = GrblConstants.SIGNALS.IndexOf(c);
                            if (i >= 0)
                                s |= (1 << i);
                        }
                        Signals.Value = (Signals)s;
//                        CanReset = canReset();
                    }
                    break;

                case "Ov":
                    if (_ov != value)
                    {
                        _ov = value;
                        if (_ov == string.Empty)
                            FeedOverride = RapidsOverride = RPMOverride = 100d;
                        else
                        {
                            double[] values = dbl.ParseList(_ov);
                            if (_feedOverride != values[0])
                                FeedOverride = values[0];
                            if (_rapidsOverride != values[1])
                                RapidsOverride = values[1];
                            if (_rpmOverride != values[2])
                                RPMOverride = values[2];
                        }
                    }
                    break;

                case "Sc":
                    if (_sc != value)
                    {
                        int s = 0;
                        foreach (char c in value)
                        {
                            int i = GrblInfo.AxisLetterToIndex(c);
                            if (i >= 0)
                                s |= (1 << i);
                        }
                        AxisScaled.Value = (AxisFlags)s;
                        Scaling = value;
                    }
                    break;

                case "Ex":
                    if (_ex != value)
                        TubeCoolant = value == "C";
                    break;

                case "SD":
                    value = string.Format("SD Card streaming {0}% complete", value.Split(',')[0]);
                    if (SDCardStatus != value)
                        Message = SDCardStatus = value;
                    break;

                case "T":
                    if (_tool != value)
                        Tool = value == "0" ? GrblConstants.NO_TOOL : value;
                    break;

                case "TLR":
                    IsTloReferenceSet = value != "0";
                    break;

                case "MPG":
                    GrblInfo.MPGMode = _grblState.MPG = value == "1";
                    IsMPGActive = _grblState.MPG;
                    break;

                case "H":
                    if (_h != value)
                    {
                        _h = value;
                        var hs = _h.Split(',');
                        HomedState = hs[0] == "1" ? HomedState.Homed : (GrblState.State == GrblStates.Alarm && GrblState.Substate == 11 ? HomedState.NotHomed : HomedState.Unknown);
                    }
                    break;

                case "D":
                    _d = value;
                    LatheMode = GrblParserState.LatheMode = value == "0" ? LatheMode.Radius : LatheMode.Diameter;
                    break;

                case "THC":
                    {
                        var values = value.Split(',');

                        if (_thcv != values[0])
                        {
                            _thcv = values[0];
                            THCVoltage = dbl.Parse(_thcv);
                        }

                        value = values.Length > 1 ? values[1] : "";

                        if (_thcs != value)
                        {
                            _thcs = value;

                            int s = 0;
                            foreach (char c in _thcs)
                            {
                                int i = GrblConstants.THCSIGNALS.IndexOf(c);
                                if (i >= 0)
                                    s |= (1 << i);
                            }
                            THCSignals.Value = (THCSignals)s;
                        }
                    }
                    break;

                case "Enc":
                    {
                        var enc = value.Split(',');
                        OverrideEncoderMode = (GrblEncoderMode)int.Parse(enc[0]);
                    }
                    break;
            }

            return pos_changed;
        }

        private bool DataIsEnumeration (string data)
        {
            return data.StartsWith("[SETTING:") || data.StartsWith("[ERRORCODE:") || data.StartsWith("[ALARMCODE:") || data.StartsWith("[SETTINGGROUP:");
        }

        public void DataReceived(string data)
        {
            if (data.Length == 0)
                return;

            if(SuspendProcessing)
            {
                OnResponseReceived?.Invoke(data);
                return;
            }

            if (ResponseLogVerbose || !(data.First() == '<' || data.First() == '$' || data.First() == 'o' || (data.First() == '[' && DataIsEnumeration(data))) || data.StartsWith("error"))
            {
                if (!(data.First() == '<' && ResponseLogFilterRT))
                {
                    if (data.StartsWith("error:"))
                    {
                        var msg = GrblErrors.GetMessage(data.Substring(6));
                        ResponseLog.Add(data + (msg == data ? "" : " - " + msg));
                    }
                    else if(!ResponseLogFilterOk || data != "ok")
                        ResponseLog.Add(data);

                    if (ResponseLog.Count > 200)
                        ResponseLog.RemoveAt(0);
                }
            }

            if (data.First() == '<')
            {
                ParseStatus(data);

                OnRealtimeStatusProcessed?.Invoke(data);
            }
            else if (data.StartsWith("ALARM"))
            {
                string[] alarm = data.Split(':');

                SetGRBLState("Alarm", alarm.Length == 2 ? int.Parse(alarm[1]) : -1, false);
            }
            else if (data.StartsWith("["))
            {
                switch(data.Substring(1, data.IndexOf(':') - 1))
                {
                    case "PRB":
                        ParseProbeStatus(data);
                        break;

                    case "GC":
                        ParseGCStatus(data);
                        break;

                    case "TLR":
                        TloReference = dbl.Parse(data.Substring(5).TrimEnd(']'));
                        break;

                    case "TLO":
                        // Workaround for legacy grbl, it reports only one offset...
                        ToolOffset.SuspendNotifications = true;
                        ToolOffset.Z = double.NaN;
                        ToolOffset.SuspendNotifications = false;
                        // End workaround    

                        ToolOffset.Parse(data.Substring(5).TrimEnd(']'));

                        // Workaround for legacy grbl, copy X offset to Z (there is no info available for which axis...)
                        if (double.IsNaN(ToolOffset.Z))
                        {
                            ToolOffset.Z = ToolOffset.X;
                            ToolOffset.X = ToolOffset.Y = 0d;
                            OnPropertyChanged(nameof(IsToolOffsetActive));
                        }

                        GrblWorkParameters.ToolLengtOffset.Z = ToolOffset.Z;
                        // End workaround
                        break;

                    case "HOME":
                        ParseHomedStatus(data);
                        break;

                    case "MSG":
                        data = data.Substring(5).Trim().TrimEnd(']');
                        if (data == "'$H'|'$X' to unlock")
                            Message = GrblInfo.IsGrblHAL ? "<Unlock> to continue" : "<Home> or <Unlock> to continue";
                        else if (GrblState.State == GrblStates.Alarm && data != "Caution: Unlocked")
                        {
                            switch(GrblState.Substate)
                            {
                                case 10:
                                    _message = "clear then <Reset> then <Unlock> to continue";
                                    break;
                                case 11:
                                    _message = "<Home> to continue";
                                    break;

                                default:
                                    _message = "<Reset> then <Unlock> to continue";
                                    break;
                            }
                            Message = (data == "Reset to continue" ? string.Empty : data + ", ") + _message;
                        }
                        else
                            Message = data;
                        if (data == "Pgm End")
                            ProgramEnd = true;
                        break;
                }
            }

            else if (data.StartsWith("Grbl"))
            {
                if(Poller != null)
                    Poller.SetState(0);
                _grblState.State = GrblStates.Unknown;
                var msg = Message;
                GrblReset = true;
                OnGrblReset?.Invoke(data);
                Message = msg;
                _reset = false;
            }
            else if (_grblState.State != GrblStates.Jog)
            {
                if (data == "ok")
                    OnCommandResponseReceived?.Invoke(data);
                else
                {
                    if (data.StartsWith("error:"))
                    {
                        try
                        {
                            SetGrblError(int.Parse(data.Substring(6)));
                        }
                        catch
                        {
                        }
                        OnCommandResponseReceived?.Invoke(data);
                    }
                    else if (!data.StartsWith("?"))
                    {
                        //                 Message = data; //??
                    }
                }
            }
            OnResponseReceived?.Invoke(data);
        }
    }
}

