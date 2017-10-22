
//#define DEBUG_OUTPUT    // log touch event

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Http;
using Windows.ApplicationModel.Background;
using Windows.Storage.Streams;
using Windows.Devices.Usb;
using Windows.Devices.Enumeration;
using Windows.UI.Input.Preview.Injection;
using System.Diagnostics;
using System.Threading;
using Windows.Foundation;


namespace TouchUsbInject
{
    public sealed class StartupTask : IBackgroundTask
    {
        private UInt32 xMin = 90;
        private UInt32 xMax = 3900;
        private UInt32 yMin = 130;
        private UInt32 yMax = 3600;

        private UInt32 xScreen = 65535;
        private UInt32 yScreen = 65535;

        private const UInt32 UsbTouchDeviceVid = 0x0EEF;
        private const UInt32 UsbTouchDevicePid = 0x0001;
        private const UInt32 InterruptInPipeIndex = 0;

        private UsbDevice m_Device;

        private InputInjector m_InputInjector;
        private long m_IsInjectorInitialized = 0;
        private bool m_LastMouseLeftDown = false;



        public void Run(IBackgroundTaskInstance taskInstance)
        {
            BackgroundTaskDeferral deferral = taskInstance.GetDeferral();

            AttachToDevice();

            // never quit
            // deferral.Complete();
        }



        private async void AttachToDevice()
        {
            string usbTouchDeviceSelector = UsbDevice.GetDeviceSelector(UsbTouchDeviceVid, UsbTouchDevicePid);
            DeviceInformationCollection deviceInfoCollection = await DeviceInformation.FindAllAsync(usbTouchDeviceSelector);
            if (deviceInfoCollection.Count == 0)
            {
                Debug.WriteLine("No USB device found with VID={0}, PID={1}\n", UsbTouchDeviceVid, UsbTouchDevicePid);
                return;
            }

            Debug.WriteLine("Device found on VID={0:X}, PID={1:X}\n", UsbTouchDeviceVid, UsbTouchDevicePid);
            DeviceInformation deviceInfo = deviceInfoCollection[0];

            DeviceAccessStatus deviceAccessStatus = DeviceAccessInformation.CreateFromId(deviceInfo.Id).CurrentStatus;

            Debug.WriteLine("Device status 1: {0}\n", deviceAccessStatus.ToString());
            m_Device = await UsbDevice.FromIdAsync(deviceInfo.Id);
            Debug.WriteLine("Device status 2: {0}\n", deviceAccessStatus.ToString());

            if (m_Device != null)
            {
                Debug.WriteLine("Device opened\n");

                UInt32 interruptPipeIndex = InterruptInPipeIndex;
                var interruptEventHandler = new TypedEventHandler<UsbInterruptInPipe, UsbInterruptInEventArgs>(this.OnUSBInterruptEvent);

                var interruptInPipes = m_Device.DefaultInterface.InterruptInPipes;

                if (interruptPipeIndex < interruptInPipes.Count)
                {
                    var interruptInPipe = interruptInPipes[(int)interruptPipeIndex];

                    interruptInPipe.DataReceived += interruptEventHandler;

                    Debug.WriteLine("Device interrupt connected\n");

                    try
                    {
                        m_InputInjector = InputInjector.TryCreate();
                        m_InputInjector.InitializeTouchInjection(InjectedInputVisualizationMode.Indirect);
                        Debug.WriteLine("InputInjector initialized\n");
                        Interlocked.Exchange(ref m_IsInjectorInitialized, 1);
                    }
                    catch
                    {
                        Debug.WriteLine("ERROR: InputInjector initialization failed!\n");
                    }

                }
            }
        }

        byte[] g_AccumBuf = new byte[5];    // buffer to accumulate bytes
        int g_AccumBufPos = 0;

        private void OnUSBInterruptEvent(UsbInterruptInPipe sender, UsbInterruptInEventArgs eventArgs)
        {
            IBuffer buffer = eventArgs.InterruptData;

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

                        Clamp(x, xMin, xMax);
                        Clamp(y, yMin, yMax);

                        UInt32 width = (xMax - xMin);
                        UInt32 height = (yMax - yMin);

                        UInt32 mouseX = (x - xMin) * xScreen / width;
                        UInt32 mouseY = (height - (y - yMin)) * yScreen / height;

                        if (Interlocked.Read(ref m_IsInjectorInitialized) != 0)
                        {
                            try
                            {
                                // inject mouse move
                                m_InputInjector.InjectMouseInput(new List<InjectedInputMouseInfo>
                                {
                                    new InjectedInputMouseInfo
                                    {
                                        DeltaX = (int)mouseX,
                                        DeltaY = (int)mouseY,
                                        MouseOptions = InjectedInputMouseOptions.Absolute
                                    }
                                });

                                if (isLeftDown != m_LastMouseLeftDown)
                                {
                                    if (isLeftDown)
                                    {
                                        // inject left mouse button down
                                        m_InputInjector.InjectMouseInput(new List<InjectedInputMouseInfo>
                                        {
                                            new InjectedInputMouseInfo
                                            {
                                                MouseOptions = InjectedInputMouseOptions.LeftDown
                                            }
                                        });
                                    }
                                    else
                                    {
                                        // inject left mouse button up
                                        m_InputInjector.InjectMouseInput(new List<InjectedInputMouseInfo>
                                        {
                                            new InjectedInputMouseInfo
                                            {
                                                MouseOptions = InjectedInputMouseOptions.LeftUp
                                            }
                                        });
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


        public static UInt32 Clamp(UInt32 v, UInt32 min, UInt32 max)
        {
            return (v < min) ? min : (v > max) ? max : v;
        }

    }   // class StartupTask

}   // namespace TouchUsbInject
