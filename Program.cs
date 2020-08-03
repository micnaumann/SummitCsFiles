using System;
using System.Threading;
using System.Collections.Generic;
using Medtronic.NeuroStim.Olympus.DataTypes.DeviceManagement;
using Medtronic.NeuroStim.Olympus.DataTypes.Sensing;
using Medtronic.NeuroStim.Olympus.DataTypes.Therapy;
using Medtronic.SummitAPI.Classes;
using Medtronic.TelemetryM;
using System.IO;
using System.Linq;
using System.Timers;
using System.Windows.Forms;
using System.Text;


namespace SummitAPI
{
    class Program
    {
        // The summit manager is used to create the summit interface and link it with a CTM.
        private static SummitManager theSummitManager;

        // The summit system is the main interface in which we communicate with the INS (through the CTM). 
        private static SummitSystem theSummit;

        private static System.Timers.Timer aTimer;

        private static void OnTimedEvent(object source, ElapsedEventArgs e)
        {
            //Do the stuff you want to be done every hour;
            //String timeStamp = GetTimestamp(DateTime.Now);
            // sampling @ 1000Hz and frame rate @ 50ms ~= 50 samples per packet (sometimes get 48/49/51/52...).
            PowerStatus status = SystemInformation.PowerStatus;
            //String str = status.BatteryLifeRemaining.ToString();

            //Console.WriteLine("TD: " + sample); // lfp, as well as power 
            try { sw.WriteLine(GetTimestamp(DateTime.Now) + ", BatteryLevel, " + status.BatteryLifeRemaining.ToString()); }
            catch (Exception er) { Console.WriteLine("Exception: "); }

        }
        static void Main(string[] args)
        {

            // Create a Summit manager; "SummitTurnerPd" is the project ID. 
            theSummitManager = new SummitManager("SummitTurnerPd", verboseTraceLogging: true);

            // Initialize a summit interface, this will 
            Console.WriteLine("Starting initialization process...");
            if (InitializateSummitConnection())
            {
                // The interface (the SummitSystem) was successfully created, now we can do whatever we want.

                // Example of how to query the group settings from the INS.
                PrintActiveGroupSettings();

                // Basic configuration of time domain channels 0 & 1 to sample at 1000hz from electrodes 0/2 and 4/6.
                // FFT and Power are also configured semi-randomly to stream (modify however you'd like).
                Console.WriteLine("Configuring sensing");
                if (ConfigureSensing())
                {
                    // Start sensing LFP, FFT, and power.
                    Console.WriteLine("Enabling sensing/streaming on/from the INS");
                    APIReturnInfo senseStateEnable = theSummit.WriteSensingState(SenseStates.LfpSense | SenseStates.Fft | SenseStates.Power, 0x00);
                    Console.WriteLine("Write Sensing State Result: " + senseStateEnable.Descriptor);

                    // Start streaming LFP, FFT, and power (plus timestamp data).
                    APIReturnInfo streamEnable = theSummit.WriteSensingEnableStreams(true, false, true, false, false, true, true, false);
                    //APIReturnInfo streamEnable = theSummit.WriteSensingEnableStreams(true, true, true, false, false, false, true, false); // sample
                    Console.WriteLine("Enable stream Result: " + streamEnable.Descriptor);

                    Console.WriteLine("Attaching data handlers - press enter at any time to stop");
                    Thread.Sleep(2000);

                    // Attach events for the incoming packets (obviously this can be done anytime after the summit system is initialized).
                    theSummit.DataReceivedTDHandler += TheSummit_DataReceivedTDHandler;
                    //theSummit.DataReceivedFFTHandler += TheSummit_DataReceivedFFTHandler;
                    theSummit.DataReceivedPowerHandler += TheSummit_DataReceivedPowerHandler;
                    theSummit.DataReceivedAccelHandler += TheSummit_DataReceivedAccelHandler;

                    aTimer = new System.Timers.Timer(20 * 60 * 1000); //one hour in milliseconds
                    //aTimer = new System.Timers.Timer(1 * 1000); //one hour in milliseconds
                    aTimer.Elapsed += new ElapsedEventHandler(OnTimedEvent);
                    aTimer.Start();


                    // At this point the time domain, FFT, and power will be streaming from the INS and invoking the event handlers.
                    Console.ReadLine();

                    // Stop streaming and sensing. 
                    theSummit.WriteSensingDisableStreams(true);
                    theSummit.WriteSensingState(SenseStates.None, 0x00);

                    // Allow packets to finish printing to screen.
                    Thread.Sleep(10);
                }
                else
                {
                    Console.WriteLine("Failed to configure sensing...");
                }
            }
            else
            {
                // Failed to connect...
                Console.WriteLine("Failed to connect, disposing Summit and stopping...");
            }

            // Teardown 
            Console.WriteLine("Teardown started..");
            if (theSummit != null)
            {
                // Dispose the summit system
                theSummitManager.DisposeSummit(theSummit);
            }

            // Dispose the manager.
            theSummitManager.Dispose();

            // Wait for the user to press enter to exit.
            Console.WriteLine("Teardown complete - press enter to exit.");
            Console.ReadLine();
        }




        /// <summary>
        /// This is virtually the same initialization protocol the "Summit Connect" training project uses, although 
        /// I added more comments where I thought it would be helpful.
        /// 
        /// Protocol used:
        ///     1. Query from the summit manager all the bonded CTMs. In this case, I pick the first one which may 
        ///         be sufficient for now although when we ship something home we need to make sure it's correctly  
        ///         choosing the participant's CTM each time.
        ///     2. Upon successfull connection to the CTM (which also creates the summit system referece), I attempt
        ///         to discover an INS (which needs to be very close to the CTM) and then connect to the first INS. 
        ///     3. If (1) and (2) succeed, the summit system has been initialized and can be used to interface with 
        ///         the INS.
        /// </summary>
        /// <returns>Boolean indicating if connection to INS was successful or not</returns>
        private static bool InitializateSummitConnection()
        {
            // Bond with any CTMs plugged in over USB.
            // This method basically implements the "pairing" of a CTM. Once the CTM is paired once, it will 
            // show up in the "GetKnownTelemetry" list. 
            Console.WriteLine("Checking USB for unbonded CTMs. Please make sure they are powered on.");
            theSummitManager.GetUsbTelemetry();

            // Retrieve a list of known and bonded telemetry.
            List<InstrumentInfo> knownTelemetry = theSummitManager.GetKnownTelemetry();

            // This will catch if a CTM has yet to be 'paired' on this commputer (i.e. CTM connected via USB while 'theSummitManager.GetUsbTelemetry()' is executed).
            // Again, once a CTM is paired once, it will show up in the known telemetry and we can continue. 
            if (knownTelemetry.Count == 0)
            {
                Console.WriteLine("No bonded CTMs found, plug a CTM in via USB and restart the program...");
                return false;
            }

            // Write out the known instruments.
            Console.WriteLine("Bonded Instruments Found:");
            foreach (InstrumentInfo inst in knownTelemetry)
            {
                Console.WriteLine("\t" + inst.SerialNumber);
            }

            // Connect to the first CTM available, then try others if it fails.
            SummitSystem tempSummit = null;
            for (int i = 0; i < knownTelemetry.Count; i++)
            {
                // Perform the connection
                ManagerConnectStatus connectReturn = theSummitManager.CreateSummit(out tempSummit, knownTelemetry[i]);

                // Write out the result
                Console.WriteLine("Connecting to CTM result: " + connectReturn.ToString());

                // Break if successful
                if (connectReturn == ManagerConnectStatus.Success)
                {
                    break;
                }
            }

            // Make sure telemetry was connected to, if not fail.
            if (tempSummit == null)
            {
                Console.WriteLine("Failed to connect to CTM...");
                return false;
            }
            else
            {
                // At this point we successfully connected to a CTM.
                // Now we need to 'discover' then connect to the INS in basically the same manner we did with the CTM.
                Console.WriteLine("CTM Connection Successful!");

                // Discover INS with the connected CTM, loop until a device has been discovered.
                // The CTM needs to be directly on top of the INS for it to 'discover' it. Once discovered, the range is ~6ft by default, but
                // can be adjusted by changing the mode/ratio of the CTM.
                Console.WriteLine("Attempting to find INS, ensure the CTM is within inches of the INS.");
                List<DiscoveredDevice> discoveredDevices;
                do
                {
                    tempSummit.OlympusDiscovery(out discoveredDevices);
                } while (discoveredDevices.Count == 0);

                // Check if null was returned. This is just possible error we will need to account for. 
                if (discoveredDevices == null)
                {
                    Console.WriteLine("Discovery returned null - handle this error better...");
                    return false;
                }

                // Display the discovered INS. 
                Console.WriteLine("INS found during discovery:");
                foreach (DiscoveredDevice ins in discoveredDevices)
                {
                    Console.WriteLine("\t" + ins);
                }

                // Connect to the first INS (in our case, the only INS).  
                Console.WriteLine("Starting a session with the discovered INS.");

                // Connection loop will attempt to connect again if an initialization error is encountered.
                //      'Critical errors' indicate that a component compatiblity problem has been encountered. 
                //          Reattempting connection with the same setup will not result in success.
                //      'Initialization errors' indicate that there was a problem with the connection attempt
                //          but not one that indicated a compatibility problem. Reattempting connection may succeed.
                ConnectReturn theWarnings;
                APIReturnInfo connectReturn;
                int i = -1;
                do
                {
                    connectReturn = tempSummit.StartInsSession(discoveredDevices[0], out theWarnings, true); // always disable annotations.
                    i++;
                } while (theWarnings.HasFlag(ConnectReturn.InitializationError));

                // Write out the number of times a StartInsSession was attempted with initialization errors
                Console.WriteLine("Initialization error count: " + i.ToString());

                // Finaly, check the rejection code from the last attempt. 
                // If successful, then the INS is ready to use via the summit system reference. 
                // Otherwise, failed and we'll need to decide what to do (try again, send error to sever, etc.). 
                if (connectReturn.RejectCode == 0)
                {
                    // Write out the warnings if they exist
                    Console.WriteLine("Summit Initialization: INS connected, warnings: " + theWarnings.ToString());
                    theSummit = tempSummit;
                    return true;
                }
                else
                {
                    Console.WriteLine("Summit Initialization: INS failed to connect");
                    theSummitManager.DisposeSummit(tempSummit);
                    return false;
                }
            }
        }

        /// <summary>
        /// Shows how to query each group's settings (the active group here). Prints out the active 
        /// group's current frequency, and all it's associated program's amplitudes.
        /// </summary>
        private static void PrintActiveGroupSettings()
        {
            // Determine which group is the active group 
            Console.WriteLine("Querying group settings of the active group.");
            APIReturnInfo therapyReadResult = theSummit.ReadGeneralInfo(out GeneralInterrogateData generalDataReadBuffer);
            if (therapyReadResult.RejectCode == 0)
            {
                // Convert the active group enumeration value into a groupnumber
                GroupNumber theActiveGroup = (GroupNumber)((int)(generalDataReadBuffer.TherapyStatusData.ActiveGroup) * 16);

                // Perform the read of the active group parameters
                therapyReadResult = theSummit.ReadStimGroup(theActiveGroup, out TherapyGroup activeGroupReadBuffer);
                if (therapyReadResult.RejectCode == 0)
                {
                    Console.WriteLine("\nActive group settings:");
                    Console.WriteLine("\tActive Group: " + theActiveGroup.ToString());
                    Console.WriteLine("\tFrequency: " + activeGroupReadBuffer.RateInHz);
                    for (int i = 0; i < activeGroupReadBuffer.Programs.Count; i++)
                    {
                        Console.WriteLine($"\tProgram {i}'s amplitude: {activeGroupReadBuffer.Programs[i].AmplitudeInMilliamps} mA");
                    }
                }
                else
                {
                    Console.WriteLine("Failed to read the stim settings of the active group.");
                }
            }
            else
            {
                Console.WriteLine("Failed to do a general interrogation..");
            }
        }

        /// <summary>
        /// Configures the sesing for the INS. 
        /// </summary>
        /// <returns></returns>
        private static bool ConfigureSensing()
        {
            // Ensure streaming and sensing is off before attempting to configuring.
            theSummit.WriteSensingDisableStreams(true);
            theSummit.WriteSensingState(SenseStates.None, 0x00);

            // ********************************** Set up time domain **********************************
            List<TimeDomainChannel> TimeDomainChannels = new List<TimeDomainChannel>(4);
            //TdSampleRates the_sample_rate = TdSampleRates.Sample1000Hz;
            TdSampleRates the_sample_rate = TdSampleRates.Sample0250Hz;

            // Channel Specific configuration - 0
            TimeDomainChannels.Add(new TimeDomainChannel(
                the_sample_rate,
                TdMuxInputs.Mux0,
                TdMuxInputs.Mux2,
                TdEvokedResponseEnable.Standard,
                TdLpfStage1.Lpf100Hz,
                TdLpfStage2.Lpf100Hz,
                TdHpfs.Hpf0_85Hz));

            // Channel Specific configuration - 1
            TimeDomainChannels.Add(new TimeDomainChannel(
                the_sample_rate,
                TdMuxInputs.Mux4,
                TdMuxInputs.Mux6,
                TdEvokedResponseEnable.Standard,
                TdLpfStage1.Lpf100Hz,
                TdLpfStage2.Lpf100Hz,
                TdHpfs.Hpf0_85Hz));

            // Channel Specific configuration - 2
            TimeDomainChannels.Add(new TimeDomainChannel(
                TdSampleRates.Disabled,
                TdMuxInputs.Mux0,
                TdMuxInputs.Mux2,
                TdEvokedResponseEnable.Standard,
                TdLpfStage1.Lpf100Hz,
                TdLpfStage2.Lpf100Hz,
                TdHpfs.Hpf0_85Hz));

            // Channel Specific configuration - 3
            TimeDomainChannels.Add(new TimeDomainChannel(
                TdSampleRates.Disabled,
                TdMuxInputs.Mux1,
                TdMuxInputs.Mux3,
                TdEvokedResponseEnable.Standard,
                TdLpfStage1.Lpf100Hz,
                TdLpfStage2.Lpf100Hz,
                TdHpfs.Hpf0_85Hz));

            // ********************************** Set up the FFT **********************************
            FftConfiguration fftChannel = new FftConfiguration(FftSizes.Size1024, 100, FftWindowAutoLoads.Hann100);

            // ********************************** Set up the Power channels **********************************
            List<PowerChannel> powerChannels = new List<PowerChannel>
            {
                new PowerChannel(5, 10, 20, 30),
                new PowerChannel(5, 10, 20, 30),
                new PowerChannel(5, 10, 20, 30),
                new PowerChannel(5, 10, 20, 30)
            };

            BandEnables theBandEnables =
                BandEnables.Ch0Band0Enabled | BandEnables.Ch0Band1Enabled |
                BandEnables.Ch1Band1Enabled | BandEnables.Ch1Band1Enabled;

            // ********************************** Set miscellaneous settings **********************************
            MiscellaneousSensing miscsettings = new MiscellaneousSensing
            {
                StreamingRate = StreamingFrameRate.Frame100ms,
                //StreamingRate = StreamingFrameRate.Frame50ms,
                LrTriggers = LoopRecordingTriggers.None,
                LrPostBufferTime = 53,
                Bridging = BridgingConfig.None
            };

            // ********************************** Write Configurations **********************************
            bool returnBuffer = true;
            returnBuffer &= theSummit.WriteSensingTimeDomainChannels(TimeDomainChannels).RejectCode == 0;
            returnBuffer &= theSummit.WriteSensingFftSettings(fftChannel).RejectCode == 0;
            returnBuffer &= theSummit.WriteSensingPowerChannels(theBandEnables, powerChannels).RejectCode == 0;
            returnBuffer &= theSummit.WriteSensingMiscSettings(miscsettings).RejectCode == 0;
            returnBuffer &= theSummit.WriteSensingAccelSettings(AccelSampleRate.Sample32).RejectCode == 0;
            //returnBuffer &= theSummit.WriteSensingAccelSettings(AccelSampleRate.Disabled).RejectCode == 0;
            Console.WriteLine("Writing sense configuration successful: " + returnBuffer.ToString());

            return returnBuffer;
        }


        //public static StreamWriter sw = new StreamWriter("C:\\Users\\Michael Naumann\\Documents\\SummitTextFile.txt");
        public static StreamWriter sw = new StreamWriter("C:\\Users\\gaoqi\\Documents\\SummitTextFile_7_30_133pm.txt");

        #region Event Handlers
        /// <summary>
        /// Time domain (LFP) event hander.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void TheSummit_DataReceivedTDHandler(object sender, Medtronic.SummitAPI.Events.SensingEventTD e)
        {
            // Attempt to pull the data out of the received data (it won't be there if not enabled)
            if (e.ChannelSamples.TryGetValue(0, out List<double> tdData)) // pulling Ch0 data
            {
                //String timeStamp = GetTimestamp(DateTime.Now);
                // sampling @ 1000Hz and frame rate @ 50ms ~= 50 samples per packet (sometimes get 48/49/51/52...).
                foreach (var sample in tdData)
                {
                    //Console.WriteLine("TD: " + sample); // lfp, as well as power 
                    try { sw.WriteLine(GetTimestamp(DateTime.Now) + ", TD, " + sample); }
                    catch (Exception er) { Console.WriteLine("Exception: "); }

                }

                // include time stamp
                // instead save to a file. , instead of writing to console
                // run for 24 hours. 9from clean boot, chekc other background programs)
                // track enery consumptioin of the app, log and plot 
                // create online repo, with code structure and data
                // can add folder for interesting medical iot papers, (for talk on tuesday, and before meeting)
            }
        }

        public static String GetTimestamp(DateTime value)
        {
            return value.ToString("yyyyMMddHHmmssffff");
        }

        /// <summary>
        /// FFT data event handler.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void TheSummit_DataReceivedFFTHandler(object sender, Medtronic.SummitAPI.Events.SensingEventFFT e)
        {
            // e.FftOutput: holds FFT output bins .. based on configuration.
        }

        /// <summary>
        /// Power data event handler.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void TheSummit_DataReceivedPowerHandler(object sender, Medtronic.SummitAPI.Events.SensingEventPower e)
        {
            //Console.WriteLine("POWER: " + e.Bands[0]);
            //if (e.ChannelSamples.TryGetValue(0, out List<double> tdData)) // pulling Ch0 data // from TD copied

            {
                String timeStamp = GetTimestamp(DateTime.Now);
                // sampling @ 1000Hz and frame rate @ 50ms ~= 50 samples per packet (sometimes get 48/49/51/52...).
                foreach (var sample in e.Bands)
                {
                    //Console.WriteLine("TD: " + sample); // lfp, as well as power 
                    try { sw.WriteLine(timeStamp + ", Power, " + sample); }
                    catch (Exception er) { Console.WriteLine("Exception: "); }

                }

                // include time stamp
                // instead save to a file. , instead of writing to console
                // run for 24 hours. 9from clean boot, chekc other background programs)
                // track enery consumptioin of the app, log and plot 
                // create online repo, with code structure and data
                // can add folder for interesting medical iot papers, (for talk on tuesday, and before meeting)
            }

        }

        private static void TheSummit_DataReceivedAccelHandler(object sender, Medtronic.SummitAPI.Events.SensingEventAccel e)
        {
            //Console.WriteLine("Accel: " + e.XSamples.Count
            //if (e.ChannelSamples.TryGetValue(0, out List<double> tdData)) // pulling Ch0 data // from TD copied

            {
                String timeStamp = GetTimestamp(DateTime.Now);
                // sampling @ 1000Hz and frame rate @ 50ms ~= 50 samples per packet (sometimes get 48/49/51/52...).
                foreach (var sample in e.XSamples)
                {
                    //Console.WriteLine("TD: " + sample); // lfp, as well as power 
                    try { sw.WriteLine(timeStamp + ", AccellX, " + sample); }
                    catch (Exception er) { Console.WriteLine("Exception: "); }

                }
                foreach (var sample in e.YSamples)
                {
                    //Console.WriteLine("TD: " + sample); // lfp, as well as power 
                    try { sw.WriteLine(timeStamp + ", AccellY, " + sample); }
                    catch (Exception er) { Console.WriteLine("Exception: "); }

                }
                foreach (var sample in e.ZSamples)
                {
                    //Console.WriteLine("TD: " + sample); // lfp, as well as power 
                    try { sw.WriteLine(timeStamp + ", AccellZ, " + sample); }
                    catch (Exception er) { Console.WriteLine("Exception: "); }

                }

                // include time stamp
                // instead save to a file. , instead of writing to console
                // run for 24 hours. 9from clean boot, chekc other background programs)
                // track enery consumptioin of the app, log and plot 
                // create online repo, with code structure and data
                // can add folder for interesting medical iot papers, (for talk on tuesday, and before meeting)
            }

        }
        #endregion

        
    }
}
