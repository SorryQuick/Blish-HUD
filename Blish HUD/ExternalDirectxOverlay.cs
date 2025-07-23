using Blish_HUD.Controls.Extern;
using Blish_HUD.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
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
        const int HEADERSIZE = 16; //4 * 4 bytes for width, height, frame_ready, frame_consumed
        public static Color[] PreviousFrame = null;
        public static bool ForceOverlayHidden = false;
        public static Color[] PixelData;

        //Frame queue related
        const int MAXQUEUESIZE = 2;
        private static ConcurrentQueue<Color[]> _frameQueue = new ConcurrentQueue<Color[]>();
        private static AutoResetEvent _frameAvailable = new AutoResetEvent(false);
        private static volatile bool _workerRunning = false;
        private static Thread _workerThread;

        /*
            Header : [ width (u32) | height (u32) | frame_ready (u32) | frame_consumed (u32) ]
            Body: { 
                [ rect count (4 bytes) ]
                [ rects (N * 16 bytes) ]
                [ pixel data for each rect (N * w * h * 4 bytes) ]
            }
         */



        public static void EnqueueFrame(Color[] pixelData) {
            if (!_workerRunning) {
                StartWorker();
            }
            var copy = new Color[pixelData.Length];
            Array.Copy(pixelData, copy, pixelData.Length);

            //Drop oldest frames if queue is too large
            while (_frameQueue.Count >= MAXQUEUESIZE) {
                _frameQueue.TryDequeue(out _); //Discard
            }

            _frameQueue.Enqueue(copy);
            _frameAvailable.Set();
        }

        private static void FrameProcessingLoop() {
            while (_workerRunning) {
                if (_frameQueue.TryDequeue(out var frame)) {
                    WriteToSharedMemory(frame);
                } else {
                    _frameAvailable.WaitOne(5000);
                }
            }
        }

        public static void StartWorker() {
            if (_workerRunning) return;
            _workerRunning = true;
            _workerThread = new Thread(FrameProcessingLoop) { IsBackground = true };
            _workerThread.Start();
        }

        public static void StopWorker() {
            _workerRunning = false;
            _frameAvailable.Set();
            _workerThread?.Join();
        }


        //TODO: Thread this but keep sending one at a time
        public static void WriteToSharedMemory(Color[] currentFrame) {
            if (Width < 50 || Height < 50) {
                return;
            }

            bool sendWholeFrame = false;

            if (PreviousFrame == null || PreviousFrame.Length != currentFrame.Length) {
                PreviousFrame = new Color[currentFrame.Length];
                Array.Copy(currentFrame, PreviousFrame, currentFrame.Length);
                sendWholeFrame = true;
            }

            //TODO: Do this with events
            //Wait for frame_consumed to be 1
            SpinWait.SpinUntil(() => HeaderAccesor.ReadInt32(12) == 1, 1000);


            int dirtyCount = 0;
            List<(Rectangle rect, Color[] data)> dirtyRects = new List<(Rectangle, Color[] data)>();

            if (!sendWholeFrame) {
                mergeRectangles(ref dirtyRects, currentFrame, ref dirtyCount);
            } else {
                dirtyRects.Add((new Rectangle(0, 0, Width, Height), currentFrame));
                dirtyCount = 1;
            }

            if (dirtyCount == 0) {
                Array.Copy(currentFrame, PreviousFrame, currentFrame.Length);
                return;
            }

            // Calculate needed buffer size for dirty rects data
            int frameSize = 4; // dirty rect count
            foreach (var (rect, pixels) in dirtyRects) {
                frameSize += 4 * 4;           // X, Y, Width, Height (4 ints)
                frameSize += pixels.Length * 4; // RGBA pixels
            }

            byte[] buffer = new byte[frameSize];
            int offset = 0;

            // Write dirty rect count
            BitConverter.GetBytes(dirtyRects.Count).CopyTo(buffer, offset);
            offset += 4;

            // Write dirty rects and pixel data
            foreach (var (rect, pixels) in dirtyRects) { 
                unsafe {
                    fixed (byte* pBuffer = buffer) {
                        int* ptr = (int*)(pBuffer + offset);
                        *ptr = rect.X;
                        ptr++;
                        *ptr = rect.Y;
                        ptr++;
                        *ptr = rect.Width;
                        ptr++;
                        *ptr = rect.Height;
                        offset += 16;
                    }
                }
                foreach (var pixel in pixels) {
                    buffer[offset++] = pixel.R;
                    buffer[offset++] = pixel.G;
                    buffer[offset++] = pixel.B;
                    buffer[offset++] = pixel.A;
                }
            }


            // Write width, height
            HeaderAccesor.Write(0, Width);
            HeaderAccesor.Write(4, Height);

            // Write frame data
            BodyAccessor.WriteArray(0, buffer, 0, buffer.Length);

            HeaderAccesor.Write(12, 0); // frame_consumed flag offset 12
            HeaderAccesor.Write(8, 1);  // frame_ready flag offset 8
            
            // Update previous frame now that data is written and flagged ready
            Array.Copy(currentFrame, PreviousFrame, currentFrame.Length);

        }

        //TODO: For now, this literally just creates one rectangle that covers all the changes.
        //Eventually, this would create more and only send the regions that actually changed.
        private static void mergeRectangles(ref List<(Rectangle, Color[] data)> rects, Color[] currentFrame, ref int dirtyCount) {
            int minX = Width, minY = Height, maxX = 0, maxY = 0;
            bool found = false;

            for (int y = 0; y < Height; y++) {
                for (int x = 0; x < Width; x++) {
                    int i = y * Width + x;
                    if (currentFrame[i].PackedValue != PreviousFrame[i].PackedValue) {
                        found = true;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            if (!found) return;

            int rectWidth = maxX - minX + 1;
            int rectHeight = maxY - minY + 1;

            var pixels = new Color[rectWidth * rectHeight];

            for (int y = 0; y < rectHeight; y++) {
                int srcIndex = (minY + y) * Width + minX;
                Array.Copy(currentFrame, srcIndex, pixels, y * rectWidth, rectWidth);
            }

            rects.Add((new Rectangle(minX, minY, rectWidth, rectHeight), pixels));
            dirtyCount = 1;
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
            HeaderAccesor.Write(12, 1);
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
