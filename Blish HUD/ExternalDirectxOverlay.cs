using Blish_HUD.Input;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Color = Microsoft.Xna.Framework.Color;
using MouseEventArgs = Blish_HUD.Input.MouseEventArgs;
using Texture2D = SharpDX.Direct3D11.Texture2D;

namespace Blish_HUD {
    internal static class ExternalDirectxOverlay {
        private static Thread _udpListenerThread;
        private static volatile bool _listening;
        private const int LISTENPORT = 49152;


        //-------------------------------------------- RENDERING ---------------------------------------------//
        public static MemoryMappedFile HeaderMMF = null;
        public static MemoryMappedViewAccessor HeaderAccesor = null;
        public static int Width = 0 ;
        public static int Height = 0;
        const string HEADERMAPNAME = "BlishHUD_Header";
        const int HEADERSIZE = 28;
        private static uint _textureIdx = 0;


        private static readonly object _writeLock = new object();

        //Double buffer for synchronicity
        private static Texture2D[] _textures2D;
        private static SharpDX.Direct3D11.Device _device;
        private static SwapChain _swapChain;
        public static IntPtr[] SharedTextureHandles;

        //Globals
        public static bool AutoUpdatesEnabled = false;

        //Mutex the rust side can use to check if Blish is still running.
        private static Mutex _isAliveMtx = new Mutex(true, "Global\\blish_isalive_mutex");

        //Because for some reason I can't make it work peroperly with GameService.Overlay.InterfaceHidden
        public static volatile bool InterfaceHidden = false;


        /*
            Header : [ width (u32) | height (u32) | index (u32) | sharedtextureptr1 (u64) | sharedtextureptr2 (u64)]
            Body: [ Full Frame ]
         */

        private static readonly object _logLock = new object();
        private static string _logPath;
        //simple logging function to write debug messages to a file
        public static void Log(string level, string message, Exception ex = null) {
            if (_logPath == null) {
                var logsDir = Path.Combine(AppContext.BaseDirectory, "..", "logs");
                Directory.CreateDirectory(logsDir);

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
                _logPath = Path.Combine(logsDir, $"BlishHUD-{timestamp}.log");
            }

            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var logEntry = $"[{now}] [BlishHUD] [{level.ToUpper()}] {message}";

            if (ex != null) {
                logEntry += Environment.NewLine + ex + Environment.NewLine;
            }

            lock (_logLock) {
                File.AppendAllText(_logPath, logEntry + Environment.NewLine);
            }
            Console.WriteLine(logEntry);
        }

        public static void InitSharedTexture(GraphicsDevice device) {
            initializeMMF();

            ResizeTextures(device);
        }

        public static void ResizeTextures(GraphicsDevice device) {
            device.SetRenderTarget(null);

            Width = device.PresentationParameters.BackBufferWidth;
            Height = device.PresentationParameters.BackBufferHeight;

            var oldTextures = _textures2D;

            var newRenderTarget = new RenderTarget2D(
                device,
                Width,
                Height,
                false,
                SurfaceFormat.Color,
                DepthFormat.None,
                0,
                RenderTargetUsage.DiscardContents
            );

            var desc = new Texture2DDescription {
                Width = Width,
                Height = Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.Shared
            };
            _device = (SharpDX.Direct3D11.Device)typeof(GraphicsDevice).GetField("_d3dDevice", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(device);

            _swapChain = (SwapChain)typeof(GraphicsDevice).GetField("_swapChain", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(device);

            

            var newTextures = new Texture2D[] { new Texture2D(_device, desc), new Texture2D(_device, desc) };
            var newHandles = new IntPtr[newTextures.Length];

            for (int i = 0; i < newTextures.Length; i++) {
                using var dxgiResource = newTextures[i].QueryInterface<SharpDX.DXGI.Resource>();
                newHandles[i] = dxgiResource.SharedHandle;
            }

            HeaderAccesor.Write(0, Width);
            HeaderAccesor.Write(4, Height);
            HeaderAccesor.Write(8, _textureIdx);
            HeaderAccesor.Write(12, newHandles[0].ToInt64());
            HeaderAccesor.Write(20, newHandles[1].ToInt64());

            //Swap in new textures
            _textures2D = newTextures;
            SharedTextureHandles = newHandles;

            // Dispose old stuff
            if (oldTextures != null) {
                foreach (var t in oldTextures) t?.Dispose();
            }
        }

        public static void CopyToSharedTexture() {
            var texture = _swapChain.GetBackBuffer<Texture2D>(0);

            //Because the source texture is multisampled.
            //Basically a copy.
            _device.ImmediateContext.ResolveSubresource(
                texture,
                0,
                _textures2D[_textureIdx],
                0,
                texture.Description.Format
            );

            _device.ImmediateContext.Flush();
            FlipBufferIdx();
        }

        private static void FlipBufferIdx() {
            _textureIdx ^= 1;
            HeaderAccesor.Write(8, _textureIdx);
        }
        
        private static void initializeMMF() {
            HeaderMMF = MemoryMappedFile.CreateOrOpen(HEADERMAPNAME, HEADERSIZE, MemoryMappedFileAccess.ReadWrite);
            HeaderAccesor = HeaderMMF.CreateViewAccessor(0, HEADERSIZE, MemoryMappedFileAccess.ReadWrite);

            GameService.Overlay.HideAllInterface.Value.Activated += (sender, e) => {
                if (!InterfaceHidden) {
                    ClearTextures();
                    InterfaceHidden = true;
                } else {
                    InterfaceHidden = false;
                }
            };
            Log("Debug", "BlishHUD started successfully.");
        }

        //Clears the textures when the overlay is hidden.
        private static void ClearTextures() {
            if (_textures2D == null) return;

            foreach (var tex in _textures2D) {
                if (tex == null) continue;

                using (var rtv = new RenderTargetView(_device, tex)) {
                    var clearColor = new Color4(0, 0, 0, 0);
                    _device.ImmediateContext.ClearRenderTargetView(rtv, clearColor);
                }
            }
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

                            //Mouse moved
                            if (eventTypeId == 2) {
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
                            }
                        }
                    } catch (SocketException) {
                        Log("Error", "SocketException occurred while receiving UDP data.");
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

        //This patches audio to work on linux. Basically it stubs the methods that wine does not implement.
        //Using Harmony would be 1000% better, but it did not seem feasible due to .Net versions.

        [DllImport("kernel32")]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        public static void PatchUnregisterNotifications() {
            try {
                var method = typeof(NAudio.CoreAudioApi.AudioSessionManager)
                                .GetMethod("UnregisterNotifications", BindingFlags.Instance | BindingFlags.NonPublic);
                if (method == null) return;

                //Force compile
                RuntimeHelpers.PrepareMethod(method.MethodHandle);

                IntPtr ptr = method.MethodHandle.GetFunctionPointer();

                //0xC3 = ret
                //Only on x86_64 and probably x86.
                byte[] patch = { 0xC3 };

                //Make memory writable
                VirtualProtect(ptr, (UIntPtr)patch.Length, 0x40, out uint oldProtect);

                //Patch
                Marshal.Copy(patch, 0, ptr, patch.Length);

                //Restore protect
                VirtualProtect(ptr, (UIntPtr)patch.Length, oldProtect, out _);

            } catch (Exception ex) {
                Log("Debug", "Failed to patch audio", ex);
            }
        }
    }
}