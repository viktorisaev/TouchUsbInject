
//#define DEBUG_OUTPUT    // log touch event

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Windows.ApplicationModel.Background;
using Windows.Devices.Enumeration;
using Windows.Devices.Usb;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Input.Preview.Injection;

namespace TouchUsbInject
{
    public sealed class StartupTask : IBackgroundTask
    {
        private UInt32 xMin = 140;
        private UInt32 xMax = 3900;
        private UInt32 yMin = 280;
        private UInt32 yMax = 3650;

        // screen resolution in mouse units to inject
        private UInt32 xScreen = 65535;
        private UInt32 yScreen = 65535;

        // device VID and PID
        private const UInt32 UsbTouchDeviceVid = 0x0EEF;
        private const UInt32 UsbTouchDevicePid = 0x0001;
        private const UInt32 InterruptInPipeIndex = 0;

        private UsbDevice m_Device;

        private InputInjector m_InputInjector;
        private long m_IsInjectorInitialized = 0;
        private bool m_LastMouseLeftDown = false;


        BackgroundTaskDeferral m_Deferral;

        public void Run(IBackgroundTaskInstance taskInstance)
        {
            m_Deferral = taskInstance.GetDeferral();

            // get calibration (from Registry)
            ReadCalibration();

            // connect
            AttachToDevice();

            // never quit
            // deferral.Complete();
        }



        private async void ReadCalibration()
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            var localSettings2 = Windows.Storage.ApplicationData.Current.LocalFolder;
            Debug.WriteLine("localfolder: {0}", localSettings2.Path);

            const string calibrationFileName = "calibration.txt";

            // open or create a file
            try
            {
                StorageFile file = await localSettings2.GetFileAsync(calibrationFileName);
                // read calibration data
                var inputStream = await file.OpenSequentialReadAsync();

                string fileContents;
                using (var streamReader = new StreamReader(inputStream.AsStreamForRead()))
                {
                    fileContents = await streamReader.ReadToEndAsync();
                }

                string[] numbers = fileContents.Split(',');

                if (numbers.Length == 4)
                {
                    try
                    {
                        xMin = Convert.ToUInt32(numbers[0]);
                        xMax = Convert.ToUInt32(numbers[1]);
                        yMin = Convert.ToUInt32(numbers[2]);
                        yMax = Convert.ToUInt32(numbers[3]);
                    }
                    catch
                    {
                        Debug.WriteLine("ERROR: failed number parsing, file \"{0}\" should contain 4 comma separated numbers like \"90,3900,130,3600\"", calibrationFileName);
                    }
                }
                else
                {
                    Debug.WriteLine("ERROR: file \"{0}\" should contain 4 comma separated numbers like \"90,3900,130,3600\"", calibrationFileName);
                }

            }
            catch (FileNotFoundException)
            {
                // create calibration data
                StorageFile file = await localSettings2.CreateFileAsync(calibrationFileName);

                var outputStream = await file.OpenStreamForWriteAsync();

                string calibrationString = string.Format("{0},{1},{2},{3}", xMin, xMax, yMin, yMax);
                byte[] calibrationStringBytes = Encoding.ASCII.GetBytes(calibrationString);

                using (var streamWriter = new StreamWriter(outputStream))
                {
                    await outputStream.WriteAsync(calibrationStringBytes, 0, calibrationStringBytes.Length);
                }
            }


            Debug.WriteLine("Calibration settings: x=({0}..,{1}) , y=({2}..{3})", xMin, xMax, yMin, yMax);

        }




        private async void AttachToDevice()
        {
            string usbTouchDeviceSelector = UsbDevice.GetDeviceSelector(UsbTouchDeviceVid, UsbTouchDevicePid);
            DeviceInformationCollection deviceInfoCollection = await DeviceInformation.FindAllAsync(usbTouchDeviceSelector);
            if (deviceInfoCollection.Count == 0)
            {
                Debug.WriteLine("No USB device found with VID={0:X}, PID={1:X}", UsbTouchDeviceVid, UsbTouchDevicePid);
                return;
            }

            Debug.WriteLine("Device found on VID={0:X}, PID={1:X}", UsbTouchDeviceVid, UsbTouchDevicePid);
            DeviceInformation deviceInfo = deviceInfoCollection[0];

            m_Device = await UsbDevice.FromIdAsync(deviceInfo.Id);

            DeviceAccessStatus deviceAccessStatus = DeviceAccessInformation.CreateFromId(deviceInfo.Id).CurrentStatus;
            Debug.WriteLine("Device status: {0}", deviceAccessStatus.ToString());

            if (m_Device != null)
            {
                Debug.WriteLine("Device opened");

                UInt32 interruptPipeIndex = InterruptInPipeIndex;
                var interruptEventHandler = new TypedEventHandler<UsbInterruptInPipe, UsbInterruptInEventArgs>(this.OnUSBInterruptEvent);

                var interruptInPipes = m_Device.DefaultInterface.InterruptInPipes;

                if (interruptPipeIndex < interruptInPipes.Count)
                {
                    UsbInterruptInPipe interruptInPipe = interruptInPipes[(int)interruptPipeIndex];

                    interruptInPipe.DataReceived += interruptEventHandler;

                    Debug.WriteLine("Device interrupt connected");

                    try
                    {
                        m_InputInjector = InputInjector.TryCreate();
                        m_InputInjector.InitializeTouchInjection(InjectedInputVisualizationMode.Indirect);
                        Debug.WriteLine("InputInjector initialized");
                        Interlocked.Exchange(ref m_IsInjectorInitialized, 1);
                    }
                    catch
                    {
                        Debug.WriteLine("ERROR: InputInjector initialization failed!");
                    }

                }
            }
        }




        ///////////////////////////////////////////////////////////////////
        // Interrupt

        byte[] g_AccumBuf = new byte[5];    // buffer to accumulate bytes
        int g_AccumBufPos = 0;
        byte[] m_ReadBuf = new byte[100];

        // re-usable inject infos
        List<InjectedInputMouseInfo> m_InjectDataMove = new List<InjectedInputMouseInfo>
        {
            new InjectedInputMouseInfo()
        };
        List<InjectedInputMouseInfo> m_InjectDataDown = new List<InjectedInputMouseInfo>
        {
            new InjectedInputMouseInfo()
        };
        List<InjectedInputMouseInfo> m_InjectDataUp = new List<InjectedInputMouseInfo>
        {
            new InjectedInputMouseInfo()
        };


        private void OnUSBInterruptEvent(UsbInterruptInPipe sender, UsbInterruptInEventArgs eventArgs)
        {
            IBuffer buffer = eventArgs.InterruptData;

#if DEBUG_OUTPUT
            Debug.WriteLine("Interrupt={0}", buffer.Length);
#endif

            if (buffer.Length > 0)
            {
                DataReader reader = DataReader.FromBuffer(buffer);

                while (reader.UnconsumedBufferLength > 0)   // read all input data for interrupt
                {
                    byte r1 = reader.ReadByte();
                    g_AccumBuf[g_AccumBufPos] = r1;
                    g_AccumBufPos += 1;

                    // wait for a full touch event (5 bytes):
                    // - touch (1 byte)
                    // - y (2 bytes)
                    // - x (2 bytes)
                    if (g_AccumBufPos == 5)
                    {
                        g_AccumBufPos = 0;  // reset accum buffer current position

                        // check valid touch event
                        if (g_AccumBuf[0] != 0x80 && g_AccumBuf[0] != 0x81)
                        {
                            Debug.WriteLine("Interrupt data seq broken!");
                            return; // something went wrong, skip the event
                        }

                        // form x and y mouse positions from touch positions
                        UInt32 x = (UInt32)g_AccumBuf[3] * 256 + g_AccumBuf[4];
                        UInt32 y = (UInt32)g_AccumBuf[1] * 256 + g_AccumBuf[2];

        #if DEBUG_OUTPUT
                        StringBuilder b = new StringBuilder();
                        b.Clear();
                        b.AppendFormat("{0:X}: ({1}, {2})", g_AccumBuf[0], x, y);
                        Debug.WriteLine(b.ToString());
        #endif

                        bool isLeftDown = g_AccumBuf[0] == 0x81;

                        x = Clamp(x, xMin, xMax);
                        y = Clamp(y, yMin, yMax);

                        UInt32 width = (xMax - xMin);
                        UInt32 height = (yMax - yMin);

                        UInt32 mouseX = (x - xMin) * xScreen / width;
                        UInt32 mouseY = (height - (y - yMin)) * yScreen / height;

                        if (Interlocked.Read(ref m_IsInjectorInitialized) != 0)
                        {
                            try
                            {
                                // inject mouse move

                                InjectedInputMouseInfo mouseInfoMove = m_InjectDataMove[0];
                                mouseInfoMove.DeltaX = (int)mouseX;
                                mouseInfoMove.DeltaY = (int)mouseY;
                                mouseInfoMove.MouseOptions = InjectedInputMouseOptions.Absolute;
                                m_InputInjector.InjectMouseInput(m_InjectDataMove);

                                if (isLeftDown != m_LastMouseLeftDown)
                                {
                                    if (isLeftDown)
                                    {
                                        // inject left mouse button down
                                        InjectedInputMouseInfo mouseInfoDown = m_InjectDataDown[0];
                                        mouseInfoDown.MouseOptions = InjectedInputMouseOptions.LeftDown;
                                        m_InputInjector.InjectMouseInput(m_InjectDataDown);
                                    }
                                    else
                                    {
                                        // inject left mouse button up
                                        InjectedInputMouseInfo mouseInfoUp = m_InjectDataUp[0];
                                        mouseInfoUp.MouseOptions = InjectedInputMouseOptions.LeftUp;
                                        m_InputInjector.InjectMouseInput(m_InjectDataUp);
                                    }

                                    m_LastMouseLeftDown = isLeftDown;
                                }
                            }
                            catch (ArgumentException)
                            {
                                // Handle exception.
                            }
                        }
                        else
                        {
                            Debug.WriteLine("InputInjector not ready");
                        }
                    }
                }   // read all input data for interrupt
            }
        }   // OnUSBInterruptEvent



        // Clamp value
        public static UInt32 Clamp(UInt32 v, UInt32 min, UInt32 max)
        {
            return (v < min) ? min : (v > max) ? max : v;
        }


    }   // class StartupTask

}   // namespace TouchUsbInject
