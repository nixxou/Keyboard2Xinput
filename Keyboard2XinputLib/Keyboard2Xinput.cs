﻿using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using System.Windows.Forms;
using IniParser;
using IniParser.Model;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Nefarius.ViGEm.Client.Exceptions;
using Keyboard2XinputLib.Exceptions;
using System.Drawing;
using System.IO;

namespace Keyboard2XinputLib
{
    public class Keyboard2Xinput
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private const short AXIS_POS_VALUE = 0x7530;
        private const short AXIS_NEG_VALUE = -0x7530;
        private const byte TRIGGER_VALUE = 0xFF;
        private static System.Threading.Timer timer;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        private Dictionary<string, Xbox360Button> buttonsDict = new Dictionary<string, Xbox360Button>();
        private Dictionary<string, KeyValuePair<Xbox360Axis, short>> axesDict = new Dictionary<string, KeyValuePair<Xbox360Axis, short>>();
        private Dictionary<string, KeyValuePair<Xbox360Slider, byte>> slidersDict = new Dictionary<string, KeyValuePair<Xbox360Slider, byte>>();
        // Windows introduces a delay when notifying us of each key press (usually 7-15ms between each key), which causes problems with games
        // like Mortal Kombat XI and Injustice 2 (at least)
        // To work around this, a "poll interval" is used: when >0, inputs are buffered and sent each pollInterval millisecond
        private int pollInterval = 0;
        // the object that stores the pad states when they are buffered
        private PadsStates padsStates;
        // The mutex used when buffering inputs so that the PadsStates List is not modified concurrently by the thread listening to inputs and the one that regularly updates the virtual pads
        private static readonly Mutex Mutex = new Mutex();


        private ViGEmClient client;
        private List<IXbox360Controller> controllers;
        private List<ISet<Xbox360Button>> pressedButtons;

        private Config config;
        private bool enabled = true;
        private List<StateListener> listeners;

        public Keyboard2Xinput(String mappingFile)
        {
            config = new Config(mappingFile);

            string pollIntervalStr  = config.getCurrentMapping()["config"]["pollInterval"];
            if (pollIntervalStr != null)
            {
                try
                {

                    pollInterval = Int16.Parse(pollIntervalStr);
                } catch (FormatException e)
                {
                    log.Error($"Error parsing poll interval: {pollIntervalStr} is not an integer", e);

                }
            }
            if (pollInterval > 0)
            {
                    log.Info($"Using a poll interval of {pollInterval}");
            }
            else
            {
                log.Info($"Poll interval is 0: inputs will not be buffered");
            }

            InitializeAxesDict();
            InitializeButtonsDict();
            log.Debug("initialize dicts done.");

            // start enabled?
            String startEnabledStr = config.getCurrentMapping()["startup"]["enabled"];
            // only start disabled if explicitly configured as such
            if ((startEnabledStr != null) && ("false".Equals(startEnabledStr.ToLower())))
            {
                enabled = false;
            }

            // try to init ViGEm
            try
            {
                client = new ViGEmClient();
            }
            catch (VigemBusNotFoundException e)
            {
                throw new ViGEmBusNotFoundException("ViGEm bus not found, please make sure ViGEm is correctly installed.", e);
            }
            // create pads
            controllers = new List<IXbox360Controller>(config.PadCount);
            string XinputData = "";
            for (int i = 1; i <= config.PadCount; i++)
            {
                IXbox360Controller controller = client.CreateXbox360Controller();
                
                controllers.Add(controller);
                controller.FeedbackReceived +=
                    (sender, eventArgs) => log.Debug(
                        $"LM: {eventArgs.LargeMotor}, SM: {eventArgs.SmallMotor}, LED: {eventArgs.LedNumber}");
                controller.Connect();

                
				// Do NOT auto submit reports, as each submits takes milliseconds et prevents simultaneous inputs to work correctly
				controller.AutoSubmitReport = false;
                Thread.Sleep(1000);
                int XinputSlotNum = -1;
				try
				{
                    XinputSlotNum = controller.UserIndex + 1;
                    XinputData += $"XINPUT{XinputSlotNum} = {i}";
                    XinputData += "\n";
				}
				catch (Exception){ }
			}

            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"xinput.txt"),XinputData.ToString());

            // the pressed buttons (to avoid sending reports if the pressed buttons haven't changed)
            pressedButtons = new List<ISet<Xbox360Button>>(config.PadCount);
            for (int i = 0; i < config.PadCount; i++)
            {
                pressedButtons.Add(new HashSet<Xbox360Button>());
            }
            // if poll interval is > 0, start the polling thread
            if (pollInterval > 0)
            {
                padsStates = new PadsStates(controllers, pollInterval);
                timer = new System.Threading.Timer(
                callback: new TimerCallback(TimerTask),
                state: padsStates,
                dueTime: 100,
                period: 2);

            }

            listeners = new List<StateListener>();
        }

        private static void TimerTask(object timerState)
        {
            var state = timerState as PadsStates;
            List<PadState> padStates = state.GetAndFlushBuffer();
            if (padStates.Count > 0)
            {
                bool[] padInputsChanged = new bool[state.Controllers.Count];
                if (log.IsDebugEnabled)
                {
                    log.Debug("Changing virtual controller(s) state");
                }
                foreach (var padState in padStates)
                {
                    if (padState.property is Xbox360Button)
                    {
                        state.Controllers[padState.padNumber].SetButtonState((Xbox360Button)padState.property, (bool)padState.value);
                    }
                    else if (padState.property is Xbox360Axis)
                    {
                        state.Controllers[padState.padNumber].SetAxisValue((Xbox360Axis)padState.property, (short)padState.value);
                    }
                    else if (padState.property is Xbox360Slider)
                    {
                        state.Controllers[padState.padNumber].SetSliderValue((Xbox360Slider)padState.property, (byte)padState.value);
                    }
                    padInputsChanged[padState.padNumber] = true;
                    if (log.IsDebugEnabled)
                    {
                        log.Debug($"\t pad{padState.padNumber + 1} {padState.property.Name} {padState.value}");
                    }
                };

                submitReports(padInputsChanged, state.Controllers);
            }
        }

        public void AddListener(StateListener listener)
        {
            listeners.Add(listener);
        }

        private void NotifyListeners(Boolean enabled)
        {
            listeners.ForEach(delegate (StateListener listener)
            {
                listener.NotifyEnabled(enabled);
            });
        }


        /// <summary>
        /// handles key events
        /// </summary>
        /// <param name="eventType"></param>
        /// <param name="vkCode"></param>
        /// <returns>1 if the event has been handled, 0 if the key was not mapped, and -1 if the exit key has been pressed</returns>
        public int keyEvent(int eventType, Keys vkCode)
        {
            int handled = 0;

            if (enabled)
            {
                bool[] padInputsChanged = new bool[config.PadCount];

                for (int i = 0; i < config.PadCount; i++)
                {
                    int padNumberForDisplay = i + 1;
                    string sectionName = "pad" + (padNumberForDisplay);
                    // is the key pressed mapped to a button?
                    string mappedButton = config.getCurrentMapping()[sectionName][vkCode.ToString()];
                    if (mappedButton != null)
                    {
                        if (buttonsDict.ContainsKey(mappedButton))
                        {
                            if ((eventType == WM_KEYDOWN) || (eventType == WM_SYSKEYDOWN))
                            {
                                // if we already notified the virtual pad, don't do it again
                                Xbox360Button pressedButton = buttonsDict[mappedButton];
                                if (pressedButtons[i].Contains(pressedButton))
                                {
                                    handled = 1;
                                    break;
                                }
                                // store the state of the button
                                pressedButtons[i].Add(pressedButton);
                                // only buffer inputs if pollInterval > 0
                                if (pollInterval == 0)
                                {
                                    controllers[i].SetButtonState(pressedButton, true);
                                    padInputsChanged[i] = true;
                                }
                                else
                                {
                                    padsStates.PushState(i, pressedButton, true);
                                }
                                if (log.IsDebugEnabled)
                                {
                                    log.Debug($"pad{padNumberForDisplay} {mappedButton} down");
                                }
                            }
                            else
                            {
                                Xbox360Button pressedButton = buttonsDict[mappedButton];
                                if (pollInterval == 0)
                                {
                                    controllers[i].SetButtonState(pressedButton, false);
                                    padInputsChanged[i] = true;
                                }
                                else
                                {
                                    padsStates.PushState(i, pressedButton, false);
                                }
                                // remove the button state from our own set
                                pressedButtons[i].Remove(pressedButton);
                                if (log.IsDebugEnabled)
                                {
                                    log.Debug($"pad{padNumberForDisplay} {mappedButton} up");
                                }
                            }
                            handled = 1;
                            break;
                        }
                        else if (axesDict.ContainsKey(mappedButton))
                        {
                            KeyValuePair<Xbox360Axis, short> axisValuePair = axesDict[mappedButton];
                            if ((eventType == WM_KEYDOWN) || (eventType == WM_SYSKEYDOWN))
                            {
                                if (pollInterval == 0)
                                {
                                    controllers[i].SetAxisValue(axisValuePair.Key, axisValuePair.Value);
                                    padInputsChanged[i] = true;
                                }
                                else
                                {
                                    padsStates.PushState(i, axisValuePair.Key, axisValuePair.Value);
                                }
                                if (log.IsDebugEnabled)
                                {
                                    log.Debug($"pad{padNumberForDisplay} {mappedButton} down");
                                }
                            }
                            else
                            {
                                if (pollInterval == 0)
                                {
                                    controllers[i].SetAxisValue(axisValuePair.Key, 0x0);
                                    padInputsChanged[i] = true;
                                }
                                else
                                {
                                    padsStates.PushState(i, axisValuePair.Key, (short)0x0);
                                }
                                if (log.IsDebugEnabled)
                                {
                                    log.Debug($"pad{padNumberForDisplay} {mappedButton} up");
                                }
                            }
                            handled = 1;
                            break;
                        }
                        else if (slidersDict.ContainsKey(mappedButton))
                        {
                            KeyValuePair<Xbox360Slider, byte> sliderValuePair = slidersDict[mappedButton];
                            if ((eventType == WM_KEYDOWN) || (eventType == WM_SYSKEYDOWN))
                            {
                                if (pollInterval == 0)
                                {
                                    controllers[i].SetSliderValue(sliderValuePair.Key, sliderValuePair.Value);
                                    padInputsChanged[i] = true;
                                }
                                else
                                {
                                    padsStates.PushState(i, sliderValuePair.Key, sliderValuePair.Value);
                                }
                                if (log.IsDebugEnabled)
                                {
                                    log.Debug($"pad{padNumberForDisplay} {mappedButton} down");
                                }
                            }
                            else
                            {
                                if (pollInterval == 0)
                                {
                                    controllers[i].SetSliderValue(sliderValuePair.Key, 0x0);
                                    padInputsChanged[i] = true;
                                }
                                else
                                {
                                    padsStates.PushState(i, sliderValuePair.Key, (byte)0x0);
                                }
                                if (log.IsDebugEnabled)
                                {
                                    log.Debug($"pad{padNumberForDisplay} {mappedButton} up");
                                }
                            }
                            handled = 1;
                            break;
                        }

                    }
                }
                if ((pollInterval == 0))
                {
                    // no buffering of inputs: submit reports now
                    submitReports(padInputsChanged, controllers);
                }
            }
            if (handled == 0)
            {
                // handle the enable toggle key even if disabled (otherwise there's not much point to it...)
                string enableButton = config.getCurrentMapping()["config"][vkCode.ToString()];
                if ("enableToggle".Equals(enableButton))
                {
                    if ((eventType == WM_KEYDOWN) || (eventType == WM_SYSKEYDOWN))
                    {
                        ToggleEnabled();
                        if (log.IsInfoEnabled)
                        {
                            log.Info($"enableToggle down; enabled={enabled}");
                        }
                    }
                    handled = 1;
                }
                else if ("enable".Equals(enableButton))
                {
                    if ((eventType == WM_KEYDOWN) || (eventType == WM_SYSKEYDOWN))
                    {
                        Enable();
                        if (log.IsInfoEnabled)
                        {
                            log.Info($"enable down; enabled={enabled}");
                        }
                    }
                    handled = 1;
                }
                else if ("disable".Equals(enableButton))
                {
                    if ((eventType == WM_KEYDOWN) || (eventType == WM_SYSKEYDOWN))
                    {
                        Disable();
                        if (log.IsInfoEnabled)
                        {
                            log.Info($"disable down; enabled={enabled}");
                        }
                    }
                    handled = 1;
                }
                // key that exits the software
                string configButton = config.getCurrentMapping()["config"][vkCode.ToString()];
                if ("exit".Equals(configButton))
                {
                    handled = -1;
                }
                else if ((configButton != null) && configButton.StartsWith("config"))
                {
                    if ((eventType == WM_KEYDOWN) || (eventType == WM_SYSKEYDOWN))
                    {
                        int index = Int32.Parse(configButton.Substring(configButton.Length - 1));
                        if (log.IsInfoEnabled)
                        {
                            log.Info($"Switching to mapping {index}");
                        }
                        config.CurrentMappingIndex = index;
                    }
                    handled = 1;
                }
            }

            if (handled == 0 && enabled && log.IsWarnEnabled)
            {
                log.Warn($"unmapped button {vkCode.ToString()}");
            }

            return handled;
        }

        private static void submitReports(bool[] padInputsChanged, List<IXbox360Controller> controllers)
        {
            for (int i = 0; i < padInputsChanged.Length; i++)
            {
                if (padInputsChanged[i])
                {
                    controllers[i].SubmitReport();
                    if (log.IsDebugEnabled)
                    {
                        log.Debug($"Virtual controller {i + 1} state changed");
                    }
                }
            }
        }

        private void InitializeAxesDict()
        {
            // TODO create slider dict
            slidersDict.Add("LT", new KeyValuePair<Xbox360Slider, byte>(Xbox360Slider.LeftTrigger, TRIGGER_VALUE));
            slidersDict.Add("RT", new KeyValuePair<Xbox360Slider, byte>(Xbox360Slider.RightTrigger, TRIGGER_VALUE));
            axesDict.Add("LLEFT", new KeyValuePair<Xbox360Axis, short>(Xbox360Axis.LeftThumbX, AXIS_NEG_VALUE));
            axesDict.Add("LRIGHT", new KeyValuePair<Xbox360Axis, short>(Xbox360Axis.LeftThumbX, AXIS_POS_VALUE));
            axesDict.Add("LUP", new KeyValuePair<Xbox360Axis, short>(Xbox360Axis.LeftThumbY, AXIS_POS_VALUE));
            axesDict.Add("LDOWN", new KeyValuePair<Xbox360Axis, short>(Xbox360Axis.LeftThumbY, AXIS_NEG_VALUE));
            axesDict.Add("RLEFT", new KeyValuePair<Xbox360Axis, short>(Xbox360Axis.RightThumbX, AXIS_NEG_VALUE));
            axesDict.Add("RRIGHT", new KeyValuePair<Xbox360Axis, short>(Xbox360Axis.RightThumbX, AXIS_POS_VALUE));
            axesDict.Add("RUP", new KeyValuePair<Xbox360Axis, short>(Xbox360Axis.RightThumbY, AXIS_POS_VALUE));
            axesDict.Add("RDOWN", new KeyValuePair<Xbox360Axis, short>(Xbox360Axis.RightThumbY, AXIS_NEG_VALUE));

        }

        private void InitializeButtonsDict()
        {
            buttonsDict.Add("UP", Xbox360Button.Up);
            buttonsDict.Add("DOWN", Xbox360Button.Down);
            buttonsDict.Add("LEFT", Xbox360Button.Left);
            buttonsDict.Add("RIGHT", Xbox360Button.Right);
            buttonsDict.Add("A", Xbox360Button.A);
            buttonsDict.Add("B", Xbox360Button.B);
            buttonsDict.Add("X", Xbox360Button.X);
            buttonsDict.Add("Y", Xbox360Button.Y);
            buttonsDict.Add("START", Xbox360Button.Start);
            buttonsDict.Add("BACK", Xbox360Button.Back);
            buttonsDict.Add("GUIDE", Xbox360Button.Guide);
            buttonsDict.Add("LB", Xbox360Button.LeftShoulder);
            buttonsDict.Add("LTB", Xbox360Button.LeftThumb);
            buttonsDict.Add("RB", Xbox360Button.RightShoulder);
            buttonsDict.Add("RTB", Xbox360Button.RightThumb);

        }
        public void Close()
        {
            log.Info("Closing");
            foreach (IXbox360Controller controller in controllers)
            {
                log.Debug($"Disconnecting {controller.ToString()}");
                controller.Disconnect();
            }
            log.Debug("Disposing of ViGEm client");
            client.Dispose();
        }
        public void Enable()
        {
            enabled = true;
            NotifyListeners(enabled);
        }
        public void Disable()
        {
            enabled = false;
            NotifyListeners(enabled);
        }
        public void ToggleEnabled()
        {
            enabled = !enabled;
            NotifyListeners(enabled);
        }
        public Boolean IsEnabled()
        {
            return enabled;
        }

        /// <summary>Class <c>PadState</c> represents a XBox360 button, axis or trigger states.<br/>
        /// It's used when the poll intervall is >0 to store the pad states that will be passed to the virtual pads during the next update.</summary>

        private class PadState : IEquatable<PadState>
        {
            public int padNumber;
            public Xbox360Property property;
            public Object value;
            public DateTime timestamp;
            public PadState(int padNumber, Xbox360Property button, Object state)
            {
                this.padNumber = padNumber;
                this.property = button;
                this.value = state;
                timestamp = DateTime.UtcNow;
            }

            public bool Equals(PadState other)
            {
                // ignore value for equals, as equals is used to check if a state is already in a list to avoid having both 'button down' and a 'button up' states in the list
                return (padNumber == other.padNumber) && (property.Equals(other.property));
            }
        }

        private class PadsStates
        {
            public readonly List<PadState> Buffer = new List<PadState>();
            private int PollInterval;

            public List<IXbox360Controller> Controllers { get; }

            public PadsStates(List<IXbox360Controller> controllers, int pollInteval)
            {
                Controllers = controllers;
                PollInterval = pollInteval;
            }

            public void PushState(int padNumber, Xbox360Property button, Object state)
            {
                PadState padState = new PadState(padNumber, button, state);
                Mutex.WaitOne();
                Buffer.Add(padState);
                Mutex.ReleaseMutex();
            }
            public List<PadState> GetAndFlushBuffer()
            {
                List<PadState> result = new List<PadState>();
                DateTime now = DateTime.UtcNow;
                Mutex.WaitOne();
                try
                {
                    if (Buffer.Count > 0)
                    {
                        DateTime firstEntryTime = Buffer[0].timestamp;
                        TimeSpan timeSpan = now.Subtract(firstEntryTime);
                        if (timeSpan.TotalMilliseconds < PollInterval)
                        {
                            // too soon: nothing to actually process
                            if (log.IsDebugEnabled)
                            {
                                log.Debug($"Buffer has {Buffer.Count} inputs, but it's not time yet to consume it");
                            }
                            return result;
                        }
                        List<PadState> newBuffer = new List<PadState>(Buffer);
                        Buffer.Clear();
                        result.Add(newBuffer[0]);
                        for (int i = 1; i < newBuffer.Count; i++)
                        {
                            PadState padState = newBuffer[i];

                            // only add input if the same XBox360Property isn't already in result, so that if things go too fast, we don't process button down and up at the same time (which would appear as if the button hadn't been pressed)
                            timeSpan = padState.timestamp.Subtract(firstEntryTime);
                            if ((timeSpan.TotalMilliseconds <= PollInterval) && (!result.Contains(padState))) 
                            {
                                result.Add(padState);
                            } else
                            {
                                Buffer.Add(padState);
                            }

                        }

                    }
                }
                finally
                {
                    Mutex.ReleaseMutex();
                }
                return result;
            }
        }
    }
}
