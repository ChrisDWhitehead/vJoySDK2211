/////////////////////////////////////////////////////////////////////////////////////////////////////////
//
// This project demonstrates how to write a simple vJoy feeder in C#
//
// You can compile it with either #define ROBUST OR #define EFFICIENT.
// The fuctionality is similar.
// The ROBUST section demonstrate the usage of functions that are easy and safe to use but are less efficient.
// The EFFICIENT section demonstrate the usage of functions that are more efficient.
//
// Functionality:
//	The program starts with creating one joystick object. 
//	Then it fetches the device id from the command-line and makes sure that it is within range.
//	After testing that the driver is enabled it gets information about the driver.
//	Gets information about the specified virtual device.
//	This feeder uses only a few axes. It checks their existence and 
//	checks the number of buttons and POV Hat switches.
//	Then the feeder acquires the virtual device.
//	Here starts an endless loop that feeds data into the virtual device.
//
/////////////////////////////////////////////////////////////////////////////////////////////////////////
//#define ROBUST
#define EFFICIENT
//#define FFB
//#define DUMP_FFB_FRAME

using SharpDX.DirectInput;
using System;
using System.Collections.Generic;
using System.Collections;
using static System.Console;
using static System.Convert;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Diagnostics;

// Don't forget to add this
using vJoyInterfaceWrap;

namespace Feeder221FB_DI
{
    public class vJoyFFBReceiver
    {
        protected bool isRegistered = false;
        protected vJoy Joystick;
        protected uint Id;
        protected vJoy.FfbCbFunc wrapper;
        vJoy.FFB_DEVICE_PID PIDBlock = new vJoy.FFB_DEVICE_PID();

        // For debugging only (dump frame content)
        private enum CommandType : int
        {
            IOCTL_HID_SET_FEATURE = 0xB0191,
            IOCTL_HID_WRITE_REPORT = 0xB000F
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct InternalFfbPacket
        {
            public int DataSize;
            public CommandType Command;
            public IntPtr PtrToData;
        }

        protected enum ERROR : uint
        {
            ERROR_SUCCESS = 0,
        }




        public void RegisterBaseCallback(vJoy joystick, uint id)
        {
            this.Joystick = joystick;
            this.Id = id;
            // Read PID block
            this.Joystick.FfbReadPID(this.Id, ref this.PIDBlock);

            if (!isRegistered)
            {
                this.wrapper = this.FfbFunction1; //needed to keep a reference!
                joystick.FfbRegisterGenCB(this.wrapper, IntPtr.Zero);
                this.isRegistered = true;
            }
        }

        protected void LogFormat(string text, params object[] args)
        {
            Console.WriteLine(String.Format(text, args));
        }

#if DUMP_FFB_FRAME
        public void DumpFrame(IntPtr data)
        {
            unsafe {
                InternalFfbPacket* FfbData = (InternalFfbPacket*)data;
                int size = FfbData->DataSize;
                int command = (int)FfbData->Command;
                byte* bytes = (byte*)FfbData->PtrToData;
                StringBuilder line = new StringBuilder();
                line.AppendFormat(String.Format("FFB Size {0}", size));
                line.AppendFormat(" Cmd:" + String.Format("{0:X08}", (int)FfbData->Command));
                line.AppendFormat(" ID:" + String.Format("{0:X02}", command));
                line.AppendFormat(" Size:" + String.Format("{0:D02}", (int)(size - 8)));
                line.AppendFormat(" -");
                for (uint i = 0; i < size - 8; i++)
                    line.AppendFormat(String.Format(" {0:X02}", (uint)(bytes[i])));

                LogFormat(line.ToString());
            }
        }
#endif

        /// <summary>
        /// Called when vJoy has a new FFB packet.
        /// WARNING This is called from a thread pool managed by windows.
        /// The thread itself is created and managed by vJoyInterface.dll.
        /// Do not overload it, else you will be missing FFB packets from
        /// third party application.
        /// </summary>
        /// <param name="ffbDataPtr"></param>
        /// <param name="userData"></param>
        public void FfbFunction1(IntPtr data, object userdata)
        {
            // Packet Header
            //copy ffb packet to managed structure
            InternalFfbPacket packet = (InternalFfbPacket)Marshal.PtrToStructure(data, typeof(InternalFfbPacket));

            // Packet Header
            LogFormat("============= FFB Packet =============");

            /////// Packet Device ID, and Type Block Index (if exists)
            #region Packet Device ID, and Type Block Index


            uint DeviceID = 0, BlockIndex = 0;
            FFBPType Type = new FFBPType();

            if ((uint)ERROR.ERROR_SUCCESS == Joystick.Ffb_h_DeviceID(data, ref DeviceID))
            {
                LogFormat(" > Device ID: {0}", DeviceID);
            }

            // Effect block index only used when simultaneous effects should be done by
            // underlying hardware, which is not the case for a single motor driving wheel
            if ((uint)ERROR.ERROR_SUCCESS == Joystick.Ffb_h_EffectBlockIndex(data, ref BlockIndex))
            {
                LogFormat(" > Effect Block Index: {0}", BlockIndex);
            }

            if ((uint)ERROR.ERROR_SUCCESS == Joystick.Ffb_h_Type(data, ref Type))
            {
                if (!PacketType2Str(Type, out var TypeStr))
                    LogFormat(" > Packet Type: {0}", Type);
                else
                    LogFormat(" > Packet Type: {0}", TypeStr);
                switch (Type)
                {
                    case FFBPType.PT_POOLREP:
                        LogFormat(" > Pool report handled by driver side");
                        break;
                    case FFBPType.PT_BLKLDREP:
                        LogFormat(" > Block Load report handled by driver side");
                        break;
                    case FFBPType.PT_BLKFRREP:
                        //FFBManager.FreeEffect(BlockIndex);
                        // Update PID
                        Joystick.FfbReadPID(DeviceID, ref PIDBlock);
                        LogFormat(" > Block Free effect id {0}", PIDBlock.NextFreeEID);
                        break;
                }
            }



            #endregion

            #region PID Device Control
            FFB_CTRL Control = new FFB_CTRL();
            if ((uint)ERROR.ERROR_SUCCESS == Joystick.Ffb_h_DevCtrl(data, ref Control) && DevCtrl2Str(Control, out var CtrlStr))
            {
                LogFormat(" >> PID Device Control: {0}", CtrlStr);
                switch (Control)
                {
                    case FFB_CTRL.CTRL_DEVRST:
                        // Update PID data to get the resetted values from driver side
                        Joystick.FfbReadPID(DeviceID, ref PIDBlock);
                        // device reset
                        break;
                    case FFB_CTRL.CTRL_ENACT:
                        break;
                    case FFB_CTRL.CTRL_DISACT:
                        break;
                    case FFB_CTRL.CTRL_STOPALL:
                        break;
                }
            }

            #endregion


            #region Create new effect
            FFBEType EffectType = new FFBEType();
            uint NewBlockIndex = 0;
            if ((uint)ERROR.ERROR_SUCCESS == Joystick.Ffb_h_CreateNewEffect(data, ref EffectType, ref NewBlockIndex))
            {
                // Create new effect

                // Update PID
                Joystick.FfbReadPID(Id, ref PIDBlock);

                if (EffectType2Str(EffectType, out var TypeStr))
                    LogFormat(" >> Effect Type: {0}", TypeStr);
                else
                    LogFormat(" >> Effect Type: Unknown({0})", EffectType);
                LogFormat(" >> New Effect ID: {0}", NewBlockIndex);
                if (NewBlockIndex != PIDBlock.PIDBlockLoad.EffectBlockIndex)
                {
                    LogFormat("!!! BUG NewBlockIndex=" + NewBlockIndex + " <> pid=" + ((int)PIDBlock.PIDBlockLoad.EffectBlockIndex));
                }
                LogFormat(" >> LoadStatus {0}", PIDBlock.PIDBlockLoad.LoadStatus);
            }
            #endregion

            #region Condition
            vJoy.FFB_EFF_COND Condition = new vJoy.FFB_EFF_COND();
            if ((uint)ERROR.ERROR_SUCCESS == Joystick.Ffb_h_Eff_Cond(data, ref Condition))
            {

                if (Condition.isY)
                    LogFormat(" >> Y Axis");
                else
                    LogFormat(" >> X Axis");
                LogFormat(" >> Center Point Offset: {0}", TwosCompWord2Int(Condition.CenterPointOffset));
                LogFormat(" >> Positive Coefficient: {0}", TwosCompWord2Int(Condition.PosCoeff));
                LogFormat(" >> Negative Coefficient: {0}", TwosCompWord2Int(Condition.NegCoeff));
                LogFormat(" >> Positive Saturation: {0}", Condition.PosSatur);
                LogFormat(" >> Negative Saturation: {0}", Condition.NegSatur);
                LogFormat(" >> Dead Band: {0}", Condition.DeadBand);

            }
            #endregion

            #region Effect Report
            vJoy.FFB_EFF_REPORT Effect = new vJoy.FFB_EFF_REPORT();
            if ((uint)ERROR.ERROR_SUCCESS == Joystick.Ffb_h_Eff_Report(data, ref Effect))
            {
                if (!EffectType2Str(Effect.EffectType, out var TypeStr))
                    LogFormat(" >> Effect Report: {0} {1}", (int)Effect.EffectType, Effect.EffectType.ToString());
                else
                    LogFormat(" >> Effect Report: {0}", TypeStr);
                LogFormat(" >> AxisEnabledDirection: {0}", (ushort)Effect.AxesEnabledDirection);
                if (Effect.Polar)
                {
                    LogFormat(" >> Direction: {0} deg ({1})", Polar2Deg(Effect.Direction), Effect.Direction);
                }
                else
                {
                    LogFormat(" >> X Direction: {0}", Effect.DirX);
                    LogFormat(" >> Y Direction: {0}", Effect.DirY);
                }

                if (Effect.Duration == 0xFFFF)
                    LogFormat(" >> Duration: Infinit");
                else
                    LogFormat(" >> Duration: {0} MilliSec", (int)(Effect.Duration));

                if (Effect.TrigerRpt == 0xFFFF)
                    LogFormat(" >> Trigger Repeat: Infinit");
                else
                    LogFormat(" >> Trigger Repeat: {0}", (int)(Effect.TrigerRpt));

                if (Effect.SamplePrd == 0xFFFF)
                    LogFormat(" >> Sample Period: Infinit");
                else
                    LogFormat(" >> Sample Period: {0}", (int)(Effect.SamplePrd));

                if (Effect.StartDelay == 0xFFFF)
                    LogFormat(" >> Start Delay: max ");
                else
                    LogFormat(" >> Start Delay: {0}", (int)(Effect.StartDelay));


                LogFormat(" >> Gain: {0}%%", Byte2Percent(Effect.Gain));

            }
            #endregion

            #region Effect Operation
            vJoy.FFB_EFF_OP Operation = new vJoy.FFB_EFF_OP();
            if ((uint)ERROR.ERROR_SUCCESS == Joystick.Ffb_h_EffOp(data, ref Operation) && EffectOpStr(Operation.EffectOp, out var EffOpStr))
            {

                LogFormat(" >> Effect Operation: {0}", EffOpStr);
                if (Operation.LoopCount == 0xFF)
                    LogFormat(" >> Loop until stopped");
                else
                    LogFormat(" >> Loop {0} times", (int)(Operation.LoopCount));

                switch (Operation.EffectOp)
                {
                    case FFBOP.EFF_START:
                        // Start the effect identified by the Effect Handle.
                        break;
                    case FFBOP.EFF_STOP:
                        // Stop the effect identified by the Effect Handle.
                        break;
                    case FFBOP.EFF_SOLO:
                        // Start the effect identified by the Effect Handle and stop all other effects.
                        break;
                }

            }
            #endregion

            #region Global Device Gain
            byte Gain = 0;
            if ((uint)ERROR.ERROR_SUCCESS == Joystick.Ffb_h_DevGain(data, ref Gain))
            {

                LogFormat(" >> Global Device Gain: {0}", Byte2Percent(Gain));
            }

            #endregion

            #region Envelope
            vJoy.FFB_EFF_ENVLP Envelope = new vJoy.FFB_EFF_ENVLP();
            if ((uint)ERROR.ERROR_SUCCESS == Joystick.Ffb_h_Eff_Envlp(data, ref Envelope))
            {

                LogFormat(" >> Attack Level: {0}", Envelope.AttackLevel);
                LogFormat(" >> Fade Level: {0}", Envelope.FadeLevel);
                LogFormat(" >> Attack Time: {0}", (int)(Envelope.AttackTime));
                LogFormat(" >> Fade Time: {0}", (int)(Envelope.FadeTime));
            }

            #endregion

            #region Periodic
            vJoy.FFB_EFF_PERIOD EffPrd = new vJoy.FFB_EFF_PERIOD();
            if ((uint)ERROR.ERROR_SUCCESS == Joystick.Ffb_h_Eff_Period(data, ref EffPrd))
            {

                LogFormat(" >> Magnitude: {0}", EffPrd.Magnitude);
                LogFormat(" >> Offset: {0}", TwosCompWord2Int(EffPrd.Offset));
                LogFormat(" >> Phase: {0}", EffPrd.Phase * 3600 / 255);
                LogFormat(" >> Period: {0}", (int)(EffPrd.Period));
            }
            #endregion

            #region Ramp Effect
            vJoy.FFB_EFF_RAMP RampEffect = new vJoy.FFB_EFF_RAMP();
            if ((uint)ERROR.ERROR_SUCCESS == Joystick.Ffb_h_Eff_Ramp(data, ref RampEffect))
            {
                LogFormat(" >> Ramp Start: {0}", TwosCompWord2Int(RampEffect.Start));
                LogFormat(" >> Ramp End: {0}", TwosCompWord2Int(RampEffect.End));
            }

            #endregion

            #region Constant Effect
            vJoy.FFB_EFF_CONSTANT CstEffect = new vJoy.FFB_EFF_CONSTANT();
            if ((uint)ERROR.ERROR_SUCCESS == Joystick.Ffb_h_Eff_Constant(data, ref CstEffect))
            {
                LogFormat(" >> Block Index: {0}", TwosCompWord2Int(CstEffect.EffectBlockIndex));
                LogFormat(" >> Magnitude: {0}", TwosCompWord2Int(CstEffect.Magnitude));
            }

            #endregion

#if DUMP_FFB_FRAME
            DumpFrame(data);
#endif
            LogFormat("======================================");

        }



        // Convert Packet type to String
        public static bool PacketType2Str(FFBPType Type, out string Str)
        {
            bool stat = true;
            Str = "";

            switch (Type)
            {
                case FFBPType.PT_EFFREP:
                    Str = "Effect Report";
                    break;
                case FFBPType.PT_ENVREP:
                    Str = "Envelope Report";
                    break;
                case FFBPType.PT_CONDREP:
                    Str = "Condition Report";
                    break;
                case FFBPType.PT_PRIDREP:
                    Str = "Periodic Report";
                    break;
                case FFBPType.PT_CONSTREP:
                    Str = "Constant Force Report";
                    break;
                case FFBPType.PT_RAMPREP:
                    Str = "Ramp Force Report";
                    break;
                case FFBPType.PT_CSTMREP:
                    Str = "Custom Force Data Report";
                    break;
                case FFBPType.PT_SMPLREP:
                    Str = "Download Force Sample";
                    break;
                case FFBPType.PT_EFOPREP:
                    Str = "Effect Operation Report";
                    break;
                case FFBPType.PT_BLKFRREP:
                    Str = "PID Block Free Report";
                    break;
                case FFBPType.PT_CTRLREP:
                    Str = "PID Device Control";
                    break;
                case FFBPType.PT_GAINREP:
                    Str = "Device Gain Report";
                    break;
                case FFBPType.PT_SETCREP:
                    Str = "Set Custom Force Report";
                    break;
                case FFBPType.PT_NEWEFREP:
                    Str = "Create New Effect Report";
                    break;
                case FFBPType.PT_BLKLDREP:
                    Str = "Block Load Report";
                    break;
                case FFBPType.PT_POOLREP:
                    Str = "PID Pool Report";
                    break;
                default:
                    stat = false;
                    break;
            }

            return stat;
        }

        // Convert Effect type to String
        public static bool EffectType2Str(FFBEType Type, out string Str)
        {
            bool stat = true;
            Str = "";

            switch (Type)
            {
                case FFBEType.ET_NONE:
                    stat = false;
                    break;
                case FFBEType.ET_CONST:
                    Str = "Constant Force";
                    break;
                case FFBEType.ET_RAMP:
                    Str = "Ramp";
                    break;
                case FFBEType.ET_SQR:
                    Str = "Square";
                    break;
                case FFBEType.ET_SINE:
                    Str = "Sine";
                    break;
                case FFBEType.ET_TRNGL:
                    Str = "Triangle";
                    break;
                case FFBEType.ET_STUP:
                    Str = "Sawtooth Up";
                    break;
                case FFBEType.ET_STDN:
                    Str = "Sawtooth Down";
                    break;
                case FFBEType.ET_SPRNG:
                    Str = "Spring";
                    break;
                case FFBEType.ET_DMPR:
                    Str = "Damper";
                    break;
                case FFBEType.ET_INRT:
                    Str = "Inertia";
                    break;
                case FFBEType.ET_FRCTN:
                    Str = "Friction";
                    break;
                case FFBEType.ET_CSTM:
                    Str = "Custom Force";
                    break;
                default:
                    stat = false;
                    break;
            }

            return stat;
        }

        // Convert PID Device Control to String
        public static bool DevCtrl2Str(FFB_CTRL Ctrl, out string Str)
        {
            bool stat = true;
            Str = "";

            switch (Ctrl)
            {
                case FFB_CTRL.CTRL_ENACT:
                    Str = "Enable Actuators";
                    break;
                case FFB_CTRL.CTRL_DISACT:
                    Str = "Disable Actuators";
                    break;
                case FFB_CTRL.CTRL_STOPALL:
                    Str = "Stop All Effects";
                    break;
                case FFB_CTRL.CTRL_DEVRST:
                    Str = "Device Reset";
                    break;
                case FFB_CTRL.CTRL_DEVPAUSE:
                    Str = "Device Pause";
                    break;
                case FFB_CTRL.CTRL_DEVCONT:
                    Str = "Device Continue";
                    break;
                default:
                    stat = false;
                    break;
            }

            return stat;
        }

        // Convert Effect operation to string
        public static bool EffectOpStr(FFBOP Op, out string Str)
        {
            bool stat = true;
            Str = "";

            switch (Op)
            {
                case FFBOP.EFF_START:
                    Str = "Effect Start";
                    break;
                case FFBOP.EFF_SOLO:
                    Str = "Effect Solo Start";
                    break;
                case FFBOP.EFF_STOP:
                    Str = "Effect Stop";
                    break;
                default:
                    stat = false;
                    break;
            }

            return stat;
        }

        // Polar values (0x00-0xFF) to Degrees (0-360)
        public static int Polar2Deg(UInt16 Polar)
        {
            return (int)((long)Polar * 360) / 32767;
        }

        // Convert range 0x00-0xFF to 0%-100%
        public static int Byte2Percent(byte InByte)
        {
            return ((byte)InByte * 100) / 255;
        }

        // Convert One-Byte 2's complement input to integer
        public static int TwosCompByte2Int(byte inb)
        {
            int tmp;
            byte inv = (byte)~inb;
            bool isNeg = ((inb >> 7) != 0 ? true : false);
            if (isNeg)
            {
                tmp = (int)(inv);
                tmp = -1 * tmp;
                return tmp;
            }
            else
                return (int)inb;
        }

        // Convert One-Byte 2's complement input to integer
        public static int TwosCompWord2Int(short inb)
        {
            int tmp;
            int inv = (int)~inb + 1;
            bool isNeg = ((inb >> 15) != 0 ? true : false);
            if (isNeg)
            {
                tmp = (int)(inv);
                tmp = -1 * tmp;
                return tmp;
            }
            else
                return (int)inb;
        }

    }

    //Create ENUM for button name to buttons index. For Saitek X36 only.
    enum BN
    {
        TRIG,
        A,
        B,
        LAUNCH,
        D,
        MCLICK,
        PINKY,
        C,
        M1,
        M2,
        M3,
        AUX0,
        AUX1,
        AUX2,
        HATup,
        HATrt,
        HATdn,
        HATlf,
        tHATup,
        tHATrt,
        tHATdn,
        tHATlf,
        MOUSEup,
        MOUSEfw,
        MOUSEdn,
        MOUSEbk,
    }

    // Put into separate file
    public class DIDevice
    {
        public String name;
        public Guid id;
        public DIDevice(String name, Guid id)
        {
            this.name = name;
            this.id = id;
        }
        public override string ToString()
        {
            return "" + name + " : " + id;
        }
    }
    class Program
    {
        // Declaring one joystick (Device id 1) and a position structure. 
        static public vJoy vJoystick;
        static public vJoy.JoystickState iReport;
        static public vJoyFFBReceiver FFBReceiver;
        static public uint vJoyID = 1;
        static public string deviceName;

        static public int StartAndRegisterFFB()
        {
            // Start FFB
#if FFB
            if (joystick.IsDeviceFfb(id)) {

                // Register Generic callback function
                // At this point you instruct the Receptor which callback function to call with every FFB packet it receives
                // It is the role of the designer to register the right FFB callback function

                // Note from me:
                // Warning: the callback is called in the context of a thread started by vJoyInterface.dll
                // when opening the joystick. This thread blocks upon a new system event from the driver.
                // It is perfectly ok to do some work in it, but do not overload it to avoid
                // loosing/desynchronizing FFB packets from the third party application.
                FFBReceiver.RegisterBaseCallback(joystick, id);
            }
#endif // FFB
            return 0;
        }

        static Joystick InitJoystick() // Change name to GetDIJoystick()? Put in separate file
        {
            // Initialize DirectInput
            var directInput = new DirectInput();

            // Find a Joystick Guid
            var joystickGuid = Guid.Empty; // Change name to DIJoystick
            ArrayList DIDevices = new ArrayList();

            foreach (var deviceInstance in directInput.GetDevices(DeviceType.Gamepad, DeviceEnumerationFlags.AllDevices))
                DIDevices.Add(new DIDevice(deviceInstance.InstanceName, deviceInstance.InstanceGuid));

            // If Gamepad not found, look for a Joystick
            if (joystickGuid == Guid.Empty)
                foreach (var deviceInstance in directInput.GetDevices(DeviceType.Joystick, DeviceEnumerationFlags.AllDevices))
                    DIDevices.Add(new DIDevice(deviceInstance.InstanceName, deviceInstance.InstanceGuid));

            // Display a list of detected devices
            int i = 1;
            foreach (DIDevice device in DIDevices)
            {
                Console.WriteLine($"{i} {device.ToString()}");
                i++;
            }

            //Take and check users input
            Int32 choice = 0;
            while (choice < 1 | choice > DIDevices.Count)
            {
                WriteLine("Choose a Joystick to use: ");
                // MUST add error code here, will crash if ToInt32 fails.
                // use try/catch with default choice = 1
                choice = ToInt32(ReadLine());
            }
            joystickGuid = ((DIDevice)DIDevices[choice - 1]).id;
            deviceName = ((DIDevice)DIDevices[choice - 1]).name;

            // This no longer needed
            // If Joystick not found, throws an error
            if (joystickGuid == Guid.Empty)
            {
                Console.WriteLine("No joystick/Gamepad found.\nEnter to continue...");
                Console.ReadKey();
                Environment.Exit(1);
            }

            // Instantiate the Direct Input joystick
            var diJoystick = new Joystick(directInput, joystickGuid);

            Console.WriteLine("Found Joystick/Gamepad with GUID: {0} \nEnter to continue...\n", joystickGuid);
            Console.ReadKey();
            Console.Clear();

            // Query all suported ForceFeedback effects
            var allEffects = diJoystick.GetEffects();
            foreach (var effectInfo in allEffects)
                Console.WriteLine("Effect available {0}\nEnter to continue...", effectInfo.Name);
            if (allEffects.Count == 0)
                WriteLine("Force Feedback Effects not supported\nEnter to continue...");
            ReadKey();
            Clear();

            // Set BufferSize in order to use buffered data.
            diJoystick.Properties.BufferSize = 128;

            return diJoystick;
        }

        static public uint GetButtons(bool[] barray)
        {
            uint buttons = 0;

            int index = 0;
            foreach (bool button in barray)
            {
                if (button)
                {
                    buttons |= (uint)(1 << (index));
                }
                index++;
            }
            return (uint)buttons;
        }

        static void Main(string[] args)
        {
            // Create one joystick object and a position structure.
            vJoystick = new vJoy();
            iReport = new vJoy.JoystickState();
            FFBReceiver = new vJoyFFBReceiver();


            // Device ID can only be in the range 1-16
            if (args.Length > 0 && !String.IsNullOrEmpty(args[0]))
                vJoyID = Convert.ToUInt32(args[0]);
            if (vJoyID <= 0 || vJoyID > 16)
            {
                Console.WriteLine("Illegal device ID {0}\nExit!", vJoyID);
                return;
            }

            // Get the driver attributes (Vendor ID, Product ID, Version Number)
            if (!vJoystick.vJoyEnabled())
            {
                Console.WriteLine("vJoy driver not enabled: Failed Getting vJoy attributes.");
                return;
            }
            else
                Console.WriteLine(" Vendor: {0}\nProduct: {1}\nVersion: {2}", vJoystick.GetvJoyManufacturerString(), vJoystick.GetvJoyProductString(), vJoystick.GetvJoySerialNumberString());

            // Get the state of the requested device
            VjdStat status = vJoystick.GetVJDStatus(vJoyID);
            switch (status)
            {
                case VjdStat.VJD_STAT_OWN:
                    Console.WriteLine("vJoy Device {0} is already owned by this feeder", vJoyID);
                    break;
                case VjdStat.VJD_STAT_FREE:
                    Console.WriteLine("vJoy Device {0} is free", vJoyID);
                    break;
                case VjdStat.VJD_STAT_BUSY:
                    Console.WriteLine("vJoy Device {0} is already owned by another feeder\nCannot continue", vJoyID);
                    return;
                case VjdStat.VJD_STAT_MISS:
                    Console.WriteLine("vJoy Device {0} is not installed or disabled\nCannot continue", vJoyID);
                    return;
                default:
                    Console.WriteLine("vJoy Device {0} general error\nCannot continue", vJoyID);
                    return;
            };

            // Check which axes are supported
            bool AxisX = vJoystick.GetVJDAxisExist(vJoyID, HID_USAGES.HID_USAGE_X);
            bool AxisY = vJoystick.GetVJDAxisExist(vJoyID, HID_USAGES.HID_USAGE_Y);
            bool AxisZ = vJoystick.GetVJDAxisExist(vJoyID, HID_USAGES.HID_USAGE_Z);
            bool AxisRX = vJoystick.GetVJDAxisExist(vJoyID, HID_USAGES.HID_USAGE_RX);
            bool AxisRZ = vJoystick.GetVJDAxisExist(vJoyID, HID_USAGES.HID_USAGE_RZ);
            bool AxisRY = vJoystick.GetVJDAxisExist(vJoyID, HID_USAGES.HID_USAGE_RY);
            bool AxisSL0 = vJoystick.GetVJDAxisExist(vJoyID, HID_USAGES.HID_USAGE_SL0);
            bool AxisSL1 = vJoystick.GetVJDAxisExist(vJoyID, HID_USAGES.HID_USAGE_SL1);


            // Get the number of buttons and POV Hat switches supported by this vJoy device
            int nButtons = vJoystick.GetVJDButtonNumber(vJoyID);
            int ContPovNumber = vJoystick.GetVJDContPovNumber(vJoyID);
            int DiscPovNumber = vJoystick.GetVJDDiscPovNumber(vJoyID);

            // Print results
            Console.WriteLine("\nvJoy Device {0} capabilities:", vJoyID);
            Console.WriteLine("Number of buttons\t\t{0}", nButtons);
            Console.WriteLine("Number of Continuous POVs\t{0}", ContPovNumber);
            Console.WriteLine("Number of Discrete POVs\t\t{0}", DiscPovNumber);
            Console.WriteLine("Axis X\t\t{0}", AxisX ? "Yes" : "No");
            Console.WriteLine("Axis Y\t\t{0}", AxisY ? "Yes" : "No");
            Console.WriteLine("Axis Z\t\t{0}", AxisZ ? "Yes" : "No");
            Console.WriteLine("Axis Rx\t\t{0}", AxisRX ? "Yes" : "No");
            Console.WriteLine("Axis Ry\t\t{0}", AxisRY ? "Yes" : "No");
            Console.WriteLine("Axis Rz\t\t{0}", AxisRZ ? "Yes" : "No");
            Console.WriteLine("Axis SL0\t\t{0}", AxisSL0 ? "Yes" : "No");
            Console.WriteLine("Axis SL1\t\t{0}", AxisSL1 ? "Yes" : "No");


            // Test if DLL matches the driver
            UInt32 DllVer = 0, DrvVer = 0;
            bool match = vJoystick.DriverMatch(ref DllVer, ref DrvVer);
            if (match)
                Console.WriteLine("Version of Driver Matches DLL Version ({0:X})", DllVer);
            else
                Console.WriteLine("Version of Driver ({0:X}) does NOT match SDK DLL Version ({1:X})", DrvVer, DllVer);


            // Acquire the target
            if ((status == VjdStat.VJD_STAT_OWN) || ((status == VjdStat.VJD_STAT_FREE) && (!vJoystick.AcquireVJD(vJoyID))))
            {
                Console.WriteLine("Failed to acquire vJoy device number {0}.", vJoyID);
                return;
            }
            else
                Console.WriteLine("Acquired: vJoy device number {0}.", vJoyID);

            StartAndRegisterFFB();

            Console.WriteLine("\nPress enter to start feeding...");
            Console.ReadKey(true);

            // Initialize physical joystick
            Joystick diJoystick = InitJoystick();
            // Acquire the physical joystick
            diJoystick.Acquire();
            JoystickState data2;

            int X, Y, Z, ZR, YR, XR, SL0, SL1;
            long maxval = 0;

            X = 20;
            Y = 30;
            Z = 40;
            XR = 60;
            YR = 0;
            ZR = 80;
            SL0 = 0;
            SL1 = 0;

            vJoystick.GetVJDAxisMax(vJoyID, HID_USAGES.HID_USAGE_X, ref maxval);
            Debug.WriteLine("" + maxval);

#if ROBUST
            bool res;
            // Reset this device to default values
            vJoystick.ResetVJD(vJoyID);

            // Calibrate axis, two step, min-max, then center
            bool run = true;
            bool cal = true;
            bool cen = true;

            //int X, Xmin, Xmax, Y, Ymin, Ymax, Z, Zmin, Zmax, RDR, TH, RZ, RZmin, RZmax, RX, RY, SL0, SL0min, SL0max, SL1;
            int Xmin, Xmax, Ymin, Ymax, Zmin, Zmax, RDR, TH, RZ, RZmin, RZmax, RX, RY,  SL0min, SL0max ;
            int Xcen, Ycen, RZcen;
            float Xcorrhi, Xcorrlo, Ycorrhi, Ycorrlo, Zcorrhi, RZcorrhi, RZcorrlo, SL0corr;
            

            X = 16383;
            Xmin = 65535;
            Xmax = 0;
            Xcorrhi = 1;
            Xcorrlo = 1;
            Xcen = 32767;
            Y = 16383;
            Ymin = 65535;
            Ymax = 0;
            Ycorrhi = 1;
            Ycorrlo = 1;
            Ycen = 32767;
            Z = 32767;
            Zmin = 65535;
            Zmax = 0;
            Zcorrhi = 1;
            //RX = 16383;
            //RY = 16383;
            RZ = 32767;
            RZmin = 65535;
            RZmax = 0;
            RZcorrhi = 1;
            RZcorrlo = 1;
            RZcen = 32767;
            SL0 = 32767;
            SL0min = 65535;
            SL0max = 0;
            SL0corr = 1;
            //SL1 = 16383;
            //RDR = 0;
            //TH = 0;


            while (cal)
            {
                diJoystick.Poll();

                var data2 = diJoystick.GetCurrentState();

                if (data2.X > Xmax)
                    Xmax = data2.X;
                if (data2.X < Xmin)
                    Xmin = data2.X;

                if (data2.Y > Ymax)
                    Ymax = data2.Y;
                if (data2.Y < Ymin)
                    Ymin = data2.Y;

                if (data2.Sliders[0] > SL0max)
                    SL0max = data2.Sliders[0];
                if (data2.Sliders[0] < SL0min)
                    SL0min = data2.Sliders[0];

                if (data2.RotationZ > RZmax)
                    RZmax = data2.RotationZ;
                if (data2.RotationZ < RZmin)
                    RZmin = data2.RotationZ;

                Xcen = data2.X;
                Ycen = data2.Y;
                RZcen = data2.RotationZ;

                Console.WriteLine("Calibrate Axis (move all axis to limits)...");
                Console.WriteLine("Press Trigger to finish.");
                Console.WriteLine($"XCal: Min={Xmin} Max={Xmax} Cen={Xcen}");
                Console.WriteLine($"YCal: Min={Ymin} Max={Ymax} Cen={Ycen}");
                Console.WriteLine($"ZCal: Min={SL0min} Max={SL0max}");
                Console.WriteLine($"RZCal: Min={RZmin} Max={RZmax} Cen={RZcen}");
                


                if (data2.Buttons[(int)BN.TRIG])
                    cal = false;


                Thread.Sleep(20);
                Console.Clear();
            }

            // Feed the device in endless loop
            while (true) {

                diJoystick.Poll();

                var data = diJoystick.GetBufferedData();
                // retrieves DIJOYSTATE2 (device with extended capabilities)
                var data2 = diJoystick.GetCurrentState();
                //foreach (var state in data)
                //    Console.WriteLine(state);
                //Console.WriteLine($"Trig:\t{data2.Buttons[(int)BN.TRIG]}\tAxisX:\t{data2.X}\n" +
                //                  $"A:\t{data2.Buttons[(int)BN.A]}\tAxisY:\t{data2.Y}\n" +
                //                  $"B:\t{data2.Buttons[(int)BN.B]}\tAxisZ:\t{data2.Z}\n" +
                //                  $"C:\t{data2.Buttons[(int)BN.C]}\n" +
                //                  $"D:\t{data2.Buttons[(int)BN.D]}\tAxisXr:\t{data2.RotationX}\n" +
                //                  $"Lnch:\t{data2.Buttons[(int)BN.LAUNCH]}\tAxisYr:\t{data2.RotationY}\n" +
                //                  $"Pnky:\t{data2.Buttons[(int)BN.PINKY]}\tAxisZr:\t{data2.RotationZ}\n" +
                //                  $"MClck:\t{data2.Buttons[(int)BN.MCLICK]}\n" +
                //                  $"\nMode1:\t{data2.Buttons[(int)BN.M1]}\tSlide0:\t{data2.Sliders[0]}\n" +
                //                  $"Mode2:\t{data2.Buttons[(int)BN.M2]}\tSlide1:\t{data2.Sliders[1]}\n" +
                //                  $"Mode3:\t{data2.Buttons[(int)BN.M3]}\n" +
                //                  $"\nAUX0:\t{data2.Buttons[(int)BN.AUX0]}\tPOV:\t{data2.PointOfViewControllers[0]}\n" +
                //                  $"AUX1:\t{data2.Buttons[(int)BN.AUX1]}\n" +
                //                  $"AUX2:\t{data2.Buttons[(int)BN.AUX2]}\n" +
                //                  $"\n\tHATup:\t{data2.Buttons[(int)BN.HATup]}\n" +
                //                  $"HATlf:\t{data2.Buttons[(int)BN.HATlf]}\tHATrt:\t{data2.Buttons[(int)BN.HATrt]}\n" +
                //                  $"\tHATdn:\t{data2.Buttons[(int)BN.HATdn]}\n" +
                //                  $"\n\ttHATup:\t{data2.Buttons[(int)BN.tHATup]}\n" +
                //                  $"tHATlf:\t{data2.Buttons[(int)BN.tHATlf]}\ttHATrt:\t{data2.Buttons[(int)BN.tHATrt]}\n" +
                //                  $"\ttHATdn:\t{data2.Buttons[(int)BN.tHATdn]}\n" +
                //                  $"\n\tMSEup:\t{data2.Buttons[(int)BN.MOUSEup]}\n" +
                //                  $"MSEbk:\t{data2.Buttons[(int)BN.MOUSEbk]}\tMSEfw:\t{data2.Buttons[(int)BN.MOUSEfw]}\n" +
                //                  $"\tMSEdn:\t{data2.Buttons[(int)BN.MOUSEdn]}\n" +
                //                  $"Using device: {deviceName}");
                Thread.Sleep(5);
                Console.Clear();

                // Set position of 4 axes
                res = vJoystick.SetAxis(X, vJoyID, HID_USAGES.HID_USAGE_X);
                res = vJoystick.SetAxis(Y, vJoyID, HID_USAGES.HID_USAGE_Y);
                res = vJoystick.SetAxis(Z, vJoyID, HID_USAGES.HID_USAGE_Z);
                res = vJoystick.SetAxis(XR, vJoyID, HID_USAGES.HID_USAGE_RX);
                res = vJoystick.SetAxis(YR, vJoyID, HID_USAGES.HID_USAGE_RY);
                res = vJoystick.SetAxis(ZR, vJoyID, HID_USAGES.HID_USAGE_RZ);
                res = vJoystick.SetAxis(SL0, vJoyID, HID_USAGES.HID_USAGE_SL0);
                res = vJoystick.SetAxis(SL1, vJoyID, HID_USAGES.HID_USAGE_SL1);

                // Press/Release Buttons
                //res = joystick.SetBtn(true, vJoyID, count / 50);
                //res = joystick.SetBtn(false, vJoyID, 1 + count / 50);
                res = vJoystick.SetBtn(data2.Buttons[(int)BN.TRIG], vJoyID, (int)BN.TRIG+1);
                res = vJoystick.SetBtn(data2.Buttons[(int)BN.A], vJoyID, (int)BN.A+1);
                res = vJoystick.SetBtn(data2.Buttons[(int)BN.B], vJoyID, (int)BN.B);
                res = vJoystick.SetBtn(data2.Buttons[(int)BN.C], vJoyID, (int)BN.C);

                // If Continuous POV hat switches installed - make them go round
                // For high values - put the switches in neutral state
                if (ContPovNumber>0) {
                    if ((count * 70) < 30000) {
                        res = vJoystick.SetContPov(((int)count * 70), vJoyID, 1);
                        res = vJoystick.SetContPov(((int)count * 70) + 2000, vJoyID, 2);
                        res = vJoystick.SetContPov(((int)count * 70) + 4000, vJoyID, 3);
                        res = vJoystick.SetContPov(((int)count * 70) + 6000, vJoyID, 4);
                    } else {
                        res = vJoystick.SetContPov(-1, vJoyID, 1);
                        res = vJoystick.SetContPov(-1, vJoyID, 2);
                        res = vJoystick.SetContPov(-1, vJoyID, 3);
                        res = vJoystick.SetContPov(-1, vJoyID, 4);
                    };
                };

                // If Discrete POV hat switches installed - make them go round
                // From time to time - put the switches in neutral state
                if (DiscPovNumber>0) {
                    if (count < 550) {
                        vJoystick.SetDiscPov((((int)count / 20) + 0) % 4, vJoyID, 1);
                        vJoystick.SetDiscPov((((int)count / 20) + 1) % 4, vJoyID, 2);
                        vJoystick.SetDiscPov((((int)count / 20) + 2) % 4, vJoyID, 3);
                        vJoystick.SetDiscPov((((int)count / 20) + 3) % 4, vJoyID, 4);
                    } else {
                        vJoystick.SetDiscPov(-1, vJoyID, 1);
                        vJoystick.SetDiscPov(-1, vJoyID, 2);
                        vJoystick.SetDiscPov(-1, vJoyID, 3);
                        vJoystick.SetDiscPov(-1, vJoyID, 4);
                    };
                };

                //Thread.Sleep(20);
                //X += 150; if (X > maxval) X = 0;
                //Y += 250; if (Y > maxval) Y = 0;
                //Z += 350; if (Z > maxval) Z = 0;
                //XR += 220; if (XR > maxval) XR = 0;
                //ZR += 200; if (ZR > maxval) ZR = 0;
                //count++;

                //if (count > 640)
                //    count = 0;

                X = data2.X / 2;
                Y = data2.Y / 2;
                Z = data2.Z / 2;

                XR = data2.RotationX / 2;
                YR = data2.RotationY / 2;
                ZR = data2.RotationZ / 2;

                SL0 = data2.Sliders[0] / 2;
                SL1 = data2.Sliders[1] / 2;
            } // While (Robust)
        //vJoystick.RelinquishVJD(vJoyID);
#endif // ROBUST
#if EFFICIENT

            byte[] pov = new byte[4];
            uint buttons = new uint();
            bool[] bbuttons = new bool[32];
            uint buttonsEx1 = new uint();
            bool[] bbuttonsEx1 = new bool[32];
            uint buttonsEx2 = new uint();
            bool[] bbuttonsEx2 = new bool[32];
            uint buttonsEx3 = new uint();
            bool[] bbuttonsEx3 = new bool[32];


            while (true)
            {

                diJoystick.Poll();
                data2 = diJoystick.GetCurrentState();
                // Split bool[128] array into 4 bool[32] arrays
                Array.Copy(data2.Buttons, 0, bbuttons, 0, 32);
                Array.Copy(data2.Buttons, 32, bbuttonsEx1, 0, 32);
                Array.Copy(data2.Buttons, 64, bbuttonsEx1, 0, 32);
                Array.Copy(data2.Buttons, 96, bbuttonsEx1, 0, 32);

                // To mask out buttons, set the bbuttons... array postiions to 
                // false.
                bbuttons[9] = false;
                bbuttons[12] = false;

                // GetButtons(bool[]) uses a loop and indexed bit shift with OR
                // accumulation to turn bools into uint value.
                buttons = GetButtons(bbuttons);
                buttonsEx1 = GetButtons(bbuttonsEx1);
                buttonsEx2 = GetButtons(bbuttonsEx2);
                buttonsEx3 = GetButtons(bbuttonsEx3);

                // vJoy only accepts 32767 axis Max value while DI allows 65534
                // need to halve the reported DI values
                X = data2.X / 2; // stick x axis
                Y = data2.Y / 2; // stick y axis
                Z = data2.Z / 2; // not used by Saitek X36

                XR = data2.RotationX / 2; // upper dial
                YR = data2.RotationY / 2; // not used by Saitek X36
                ZR = data2.RotationZ / 2; // rudder axis

                SL0 = data2.Sliders[0] / 2; // throttle axis
                SL1 = data2.Sliders[1] / 2; // lower dial

                iReport.bDevice = (byte)vJoyID;
                iReport.AxisX = X;
                iReport.AxisY = Y;
                //iReport.AxisZ = SL1;
                iReport.AxisZRot = ZR;
                iReport.AxisXRot = XR;
                iReport.AxisYRot = YR;
                iReport.Slider = SL0;
                iReport.Dial = SL1;

                // Set buttons one by one
                // iReport.Buttons = (uint)(0x1 <<  (int)(count / 20));
                // SharpDX reports buttons in a single bool array of size 128
                // vJoy Efficient mode uses 4 uint registers to represent buttons
                // bit positions represent each button in four ranges 1-32, 33-64, 65-96, 97-128
                iReport.Buttons = buttons;
                iReport.ButtonsEx1 = buttonsEx1;
                iReport.ButtonsEx2 = buttonsEx2;
                iReport.ButtonsEx3 = buttonsEx3;
                // vJoy and DI both allow the same format for continuous POV
                // can use a direct assignment
                iReport.bHats = (uint)data2.PointOfViewControllers[0];

                #region POV
                //if (ContPovNumber > 0)
                //{
                //    // Make Continuous POV Hat spin
                //    iReport.bHats = (count * 70);
                //    iReport.bHatsEx1 = (count * 70) + 3000;
                //    iReport.bHatsEx2 = (count * 70) + 5000;
                //    iReport.bHatsEx3 = 15000 - (count * 70);
                //    if ((count * 70) > 36000)
                //    {
                //        iReport.bHats = 0xFFFFFFFF; // Neutral state
                //        iReport.bHatsEx1 = 0xFFFFFFFF; // Neutral state
                //        iReport.bHatsEx2 = 0xFFFFFFFF; // Neutral state
                //        iReport.bHatsEx3 = 0xFFFFFFFF; // Neutral state
                //    };
                //}
                //else
                //{
                //    // Make 5-position POV Hat spin

                //    pov[0] = (byte)(((count / 20) + 0) % 4);
                //    pov[1] = (byte)(((count / 20) + 1) % 4);
                //    pov[2] = (byte)(((count / 20) + 2) % 4);
                //    pov[3] = (byte)(((count / 20) + 3) % 4);

                //    iReport.bHats = (uint)(pov[3] << 12) | (uint)(pov[2] << 8) | (uint)(pov[1] << 4) | (uint)pov[0];
                //    if ((count) > 550)
                //        iReport.bHats = 0xFFFFFFFF; // Neutral state
                //};
                #endregion POV
                /*** Feed the driver with the position packet - if fails then wait for input then try to re-acquire device ***/
                if (!vJoystick.UpdateVJD(vJoyID, ref iReport))
                {
                    Console.WriteLine("Feeding vJoy device number {0} failed - try to enable device then press enter\n", vJoyID);
                    Console.ReadKey(true);
                    vJoystick.AcquireVJD(vJoyID);
                }

                System.Threading.Thread.Sleep(5);
            }; // While

#endif // EFFICIENT

        } // Main
    } // class Program
} // namespace Feeder221FB_DI
