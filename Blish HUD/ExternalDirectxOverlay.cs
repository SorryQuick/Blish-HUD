using Blish_HUD.Controls.Extern;
using Blish_HUD.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SharpDX.Direct3D9;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MouseEventArgs = Blish_HUD.Input.MouseEventArgs;

namespace Blish_HUD {
    internal static class ExternalDirectxOverlay {
        private static Thread _udpListenerThread;
        private static volatile bool _listening;
        private const int LISTENPORT = 49152;


        //-------------------------------------------- RENDERING ---------------------------------------------//
        public static MemoryMappedFile HeaderMMF = null;
        public static MemoryMappedViewAccessor HeaderAccesor = null;
        public static MemoryMappedFile BodyMMF = null;
        public static MemoryMappedViewAccessor BodyAccessor = null;
        public static int Width = 0 ;
        public static int Height = 0;
        const string HEADERMAPNAME = "BlishHUD_Header";
        const string BODYMAPNAME = "BlishHUD_Body";
        const int HEADERSIZE = 8; //2 * 4 bytes for width, height
        public static Color[] PreviousFrame = null;
        public static bool ForceOverlayHidden = false;

        //Just used as a buffer to receive data from GetBackBufferData. It's here to keep globals in this one file.
        public static Color[] PixelData;

        //Events
        private static EventWaitHandle _frameReadyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, "BlishHUD_FrameReady");
        private static EventWaitHandle _frameConsumedEvent = new EventWaitHandle(true, EventResetMode.ManualReset, "BlishHUD_FrameConsumed");

        /*
            Header : [ width (u32) | height (u32)]
            Body: [ Full Frame ]
         */

        private static readonly object _writeLock = new object();
        public static void ProcessFrame(Color[] frame) {
            Task.Run(() => {
                lock (_writeLock) { 
                    WriteToSharedMemory(frame);
                }
            });
        }

        public static void WriteToSharedMemory(Color[] currentFrame) {
            if (Width < 50 || Height < 50) {
                return;
            }

            if (PreviousFrame == null || PreviousFrame.Length != currentFrame.Length) {
                PreviousFrame = new Color[currentFrame.Length];
                PreviousFrame = currentFrame;
            }


            //Wait for the rust side to consume the last frame sent.
            _frameConsumedEvent.WaitOne();


            // Write width, height
            HeaderAccesor.Write(0, Width);
            HeaderAccesor.Write(4, Height);

            byte[] bytes = ColorArrayToRgbaBytes(currentFrame); 

            // Write frame data
            BodyAccessor.WriteArray(0, bytes, 0, bytes.Length);

            // Notify the rust DLL that a new frame is ready
            _frameConsumedEvent.Reset();
            _frameReadyEvent.Set();

            // Update previous frame
            PreviousFrame = currentFrame;

        }


        //TODO: Evaluate if it's worth comparing frames and only sending the new frame if it's different.
        //TODO: If so, use SMID (Vector<T>.EqualsAll).
        /*private static bool FramesAreTheSame(Color[] a, Color[] b) {
            if (!Vector.IsHardwareAccelerated) {
                return a.SequenceEqual(b);
            }
            return true;
        }*/


        private static byte[] ColorArrayToRgbaBytes(Color[] frame) {
            var bytes = new byte[frame.Length * 4];
            for (int i = 0; i < frame.Length; i++) {
                int offset = i * 4;
                bytes[offset + 0] = frame[i].R;
                bytes[offset + 1] = frame[i].G;
                bytes[offset + 2] = frame[i].B;
                bytes[offset + 3] = frame[i].A;
            }
            return bytes;
        }

        //When the game is resized. This will also be called on the first frame, so we can also run our initialization code here.
        public static void Resize(int w, int h) {
            if (HeaderMMF == null) {
                PreviousFrame = null;
                initializeMMF();
            }
            Width = w;
            Height = h;
        }

        private static void initializeMMF() {
            //TODO: Dynamic size (why do I need 30mb...I don't...)
            //Initialization goes here, move this elsewhere later
            int totalSize = (3840 * 2160 * 4) + 4; // Max size at 3840x2160 with 4 bytes per pixel
            HeaderMMF = MemoryMappedFile.CreateOrOpen(HEADERMAPNAME, HEADERSIZE, MemoryMappedFileAccess.ReadWrite);
            BodyMMF = MemoryMappedFile.CreateOrOpen(BODYMAPNAME, totalSize, MemoryMappedFileAccess.ReadWrite);
            HeaderAccesor = HeaderMMF.CreateViewAccessor(0, HEADERSIZE, MemoryMappedFileAccess.ReadWrite);
            BodyAccessor = BodyMMF.CreateViewAccessor(0, totalSize, MemoryMappedFileAccess.ReadWrite);
            _frameConsumedEvent.Set();
            setupNewWndProc();
        }


        //---------------------------------------------- INPUT -----------------------------------------------//

        public static void StartUdpServer() {
            _listening = true;
            _udpListenerThread = new Thread(UdpListenLoop) {
                IsBackground = true
            };
            _udpListenerThread.Start();
        }

        //TODO: use this
        public static void StopUdpServer() {
            _listening = false;
            _udpListenerThread?.Join();
        }

        //Message format: [<EventTypeID>(4bytes), x(4bytes), y(4bytes)]
        //Send mouse events over UDP. Faster than you'd expect.
        private static void UdpListenLoop() {
            using (var udpClient = new UdpClient(LISTENPORT)) {
                var remoteEP = new IPEndPoint(IPAddress.Any, 0);
                while (_listening) {
                    try {
                        byte[] data = udpClient.Receive(ref remoteEP);
                        if (data.Length >= 9) {
                            byte eventTypeId = data[0];
                            int x = BitConverter.ToInt32(data, 1);
                            int y = BitConverter.ToInt32(data, 5);

                            //Left Button Pressed
                            if (eventTypeId == 0) {
                                GameService.Input.Mouse.HandleInput(new MouseEventArgs(
                                    MouseEventType.LeftMouseButtonPressed,
                                    x,
                                    y,
                                    0,
                                    0,
                                    Environment.TickCount,
                                    0
                                ));
                            //Left Button Released
                            } else if (eventTypeId == 1) {
                                GameService.Input.Mouse.HandleInput(new MouseEventArgs(
                                    MouseEventType.LeftMouseButtonReleased,
                                    x,
                                    y,
                                    0,
                                    0,
                                    Environment.TickCount,
                                    0
                                ));
                            //Mouse Moved
                            } else if (eventTypeId == 2) {
                                MouseState oldState = GameService.Input.Mouse.StaticMouseState;
                                GameService.Input.Mouse.StaticMouseState = new MouseState(
                                    x,
                                    y,
                                    oldState.ScrollWheelValue,
                                    oldState.LeftButton,
                                    oldState.RightButton,
                                    oldState.MiddleButton,
                                    oldState.XButton1,
                                    oldState.XButton2
                                );
                                GameService.Input.Mouse.HandleInput(new MouseEventArgs(
                                    MouseEventType.MouseMoved,
                                    x,
                                    y,
                                    0,
                                    0,
                                    Environment.TickCount,
                                    0
                                ));
                            //Right Button Pressed  
                            } else if (eventTypeId == 3) {
                                GameService.Input.Mouse.HandleInput(new MouseEventArgs(
                                    MouseEventType.RightMouseButtonPressed,
                                    x,
                                    y,
                                    0,
                                    0,
                                    Environment.TickCount,
                                    0
                                ));
                            //Right Button Released
                            } else if (eventTypeId == 4) {
                                GameService.Input.Mouse.HandleInput(new MouseEventArgs(
                                    MouseEventType.RightMouseButtonReleased,
                                    x,
                                    y,
                                    0,
                                    0,
                                    Environment.TickCount,
                                    0
                                ));
                            }
                        }
                    } catch (SocketException) {
                        //TODO
                    }
                }
            }
        }

        //--------------------------------------------- WINDOW MANAGEMENT ------------------------------------------//

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, WndProcDelegate dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPOS {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        const int GWL_WNDPROC = -4;
        const int WM_SHOWWINDOW = 0x0018;
        const int WM_WINDOWPOSCHANGING = 0x0046;
        const uint SWP_HIDEWINDOW = 0x0080;
        const uint SWP_SHOWWINDOW = 0x0040;

        delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        static WndProcDelegate newWndProc;
        static IntPtr originalWndProc;

        public static void setupNewWndProc() {
            IntPtr hwnd = BlishHud.Instance.FormHandle;
            originalWndProc = GetWindowLongPtr(hwnd, GWL_WNDPROC);
            newWndProc = HookWndProc;
            SetWindowLongPtr(hwnd, GWL_WNDPROC, newWndProc);
            BlishHud.Instance.Form.Visible = false;
            BlishHud.Instance.Form.Hide();
        }
        static IntPtr CallOriginalWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) {
            return CallWindowProc(originalWndProc, hWnd, msg, wParam, lParam);
        }


        //Prevents the window from ever showing up, even if Blish tries to show it.
        static IntPtr HookWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) {
            if (msg == WM_SHOWWINDOW) {
                return IntPtr.Zero;
            } else if (msg == WM_WINDOWPOSCHANGING) {
                unsafe {
                    WINDOWPOS* pos = (WINDOWPOS*)lParam;
                    if ((pos->flags & SWP_SHOWWINDOW) != 0) {
                        pos->flags &= ~SWP_SHOWWINDOW;
                        pos->flags |= SWP_HIDEWINDOW;
                    }
                }
            }

            return CallOriginalWndProc(hWnd, msg, wParam, lParam);
        }
    }
}
