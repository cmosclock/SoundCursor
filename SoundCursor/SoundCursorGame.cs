using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using NAudio.Wave;
using Vanara.PInvoke;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Color = Microsoft.Xna.Framework.Color;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace SoundCursor
{
    public class SoundCursorGame : Game
    {
        private GraphicsDeviceManager _graphics;
        private SpriteBatch _spriteBatch;
        private Texture2D? _texture;
        private WasapiLoopbackCapture _capture;
        private float[] _averageSamples = Array.Empty<float>();
        private float[]? _lastRenderedStripHeights;
        NotifyIcon _notifyIcon = new ();

        public SoundCursorGame()
        {
            _graphics = new GraphicsDeviceManager(this);
            Content.RootDirectory = "Content";
            IsMouseVisible = true;
            Window.IsBorderless = true;
            // anti aliasing
            _graphics.PreferMultiSampling = true;

            // fps
            IsFixedTimeStep = true;

            var mainGameForm = (Form)Control.FromHandle(Window.Handle);
            mainGameForm.FormBorderStyle = FormBorderStyle.None;
            mainGameForm.TopMost = true;
            mainGameForm.WindowState = FormWindowState.Maximized;
            mainGameForm.ShowInTaskbar = false;
            MakeFullScreenOverlay(Window.Handle);
            _graphics.HardwareModeSwitch = false;
            _graphics.IsFullScreen = true;
            _graphics.PreferredBackBufferWidth = Screen.PrimaryScreen.Bounds.Width;
            _graphics.PreferredBackBufferHeight = Screen.PrimaryScreen.Bounds.Height;
            _graphics.ApplyChanges();
            mainGameForm.Hide();
        }

        public void MakeFullScreenOverlay(IntPtr hWnd, bool clickable = false)
        {
            var flag = 0
                       | User32.GetWindowLong(hWnd, User32.WindowLongFlags.GWL_EXSTYLE)
                       // hide in alt tab
                       | (int)User32.WindowStylesEx.WS_EX_TOOLWINDOW 
                       | 0;
            if (!clickable)
            {
                // make entire window click through
                flag |= (int)User32.WindowStylesEx.WS_EX_TRANSPARENT | (int)User32.WindowStylesEx.WS_EX_LAYERED;
            }

            User32.SetWindowLong(hWnd, User32.WindowLongFlags.GWL_EXSTYLE, flag);
            User32.SetWindowPos(hWnd, HWND.HWND_TOPMOST, 0, 0, 0, 0, 0
                                                                     | User32.SetWindowPosFlags.SWP_NOSIZE
                                                                     | User32.SetWindowPosFlags.SWP_NOMOVE
                                                                     | 0);
            DwmApi.MARGINS margins = new DwmApi.MARGINS(-1);
            DwmApi.DwmExtendFrameIntoClientArea(Window.Handle, margins);
        }

        protected override void Initialize()
        {
            _notifyIcon.Icon = SystemIcons.Asterisk;
            _notifyIcon.Text = nameof(SoundCursor);
            _notifyIcon.Visible = true;
            _notifyIcon.ShowBalloonTip(5000, $"{nameof(SoundCursor)}", $"{nameof(SoundCursor)} is now running",  ToolTipIcon.Info);
            _notifyIcon.ContextMenuStrip = new ContextMenuStrip();
            var quitBtn = new ToolStripMenuItem("Exit");
            _notifyIcon.ContextMenuStrip.Items.Add(quitBtn);
            quitBtn.Click += (sender, args) =>
            {
                _notifyIcon.Visible = false;
                _capture.StopRecording();
                Exit();
            };

            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += (object? sender, WaveInEventArgs e) =>
            {
                // Credit: https://chowdera.com/2021/05/20210510001935196R.html
                //  Set that we have just saved the sample to the variable  AllSamples  in ,  The type is  float[]

                var channelCount = _capture.WaveFormat.Channels;   // WasapiLoopbackCapture  Of  WaveFormat  Specifies the waveform format of the current sound ,  Including the number of channels
                var samples = Enumerable
                    .Range(0, e.BytesRecorded / 4)
                    .Select(i => BitConverter.ToSingle(e.Buffer, i * 4))
                    .ToArray();
                var channelSamples = Enumerable
                    .Range(0, channelCount)
                    .Select(channel => Enumerable
                        .Range(0, samples.Length / channelCount)
                        .Select(i => samples[channel + i * channelCount])
                        .ToArray())
                    .ToArray();
                _averageSamples = Enumerable
                    .Range(0, samples.Length / channelCount)
                    .Select(index => Enumerable
                        .Range(0, channelCount)
                        .Select(channel => channelSamples[channel][index])
                        .Average())
                    .ToArray();
            };
            _capture.StartRecording();
            base.Initialize();
        }

        protected override void LoadContent()
        {
            _spriteBatch = new SpriteBatch(GraphicsDevice);
            _texture = new Texture2D(_spriteBatch.GraphicsDevice, 1, 1, false, SurfaceFormat.Color);
            _texture.SetData(new []
            {
                Color.White
            });
        }

        protected override void Update(GameTime gameTime)
        {
            if (GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed || Keyboard.GetState().IsKeyDown(Keys.Escape))
                Exit();
            base.Update(gameTime);
        }
        
        protected override void Draw(GameTime gameTime)
        {
            GraphicsDevice.Clear(Color.Transparent);

            var strips = 64;
            var stripColor = Color.IndianRed;
            // starts from top left, cloclwise
            var degStripsVisible = 35.0;
            var stripsVisible = strips * degStripsVisible / 360.0;

            var stripHeights = _averageSamples.Any()
                ? _averageSamples
                    .Chunk((int)Math.Floor(_averageSamples.Length * 1.0f / strips))
                    .Select(g => g.Average())
                    .Take(strips)
                    .ToArray()
                : Enumerable.Repeat(0.0f, strips).ToArray();
            if (_lastRenderedStripHeights != null)
            {
                foreach (var (height, i) in stripHeights.Select((h, i) => (h, i)))
                {
                    stripHeights[i] = (height + _lastRenderedStripHeights[i]) / 2;
                }
            }
            var stripHeightsMax = stripHeights.Any() ? stripHeights.Max() : 0.15;
            var stripHeightsMin = stripHeights.Any() ? stripHeights.Min() : 0.0;

            var mouse = Mouse.GetState();
            // rotate the whole thing for easier range settings
            // var point1 = new Vector2(Window.ClientBounds.Width / 2.0f, Window.ClientBounds.Height / -2.0f);
            // var point2 = new Vector2(mouse.X, -mouse.Y);
            // var beginRadianOffset = Math.Atan2(point2.X - point1.X, point2.Y - point1.Y)
            //                         // fix up
            //                         + 180.0 * Math.PI / 180
            //                         // adjust based on degStripsVisible
            //                         + (((180.0 - degStripsVisible) / 2) * Math.PI / 180);
            var beginRadianOffset = (270.0 + (90.0 - degStripsVisible) / 2) * Math.PI / 180;
            var innerRadius = 45 + (5 * stripHeightsMin/stripHeightsMax);
            var outerRadius = 70 + (5 * stripHeightsMin/stripHeightsMax);
            var centerX = mouse.X;
            var centerY = mouse.Y;

            var radianOffset = (float)(2.0f * Math.PI / strips);
            var radian = 0.0;
            var beginRadian = (int)(beginRadianOffset / radianOffset) * radianOffset;
            _spriteBatch.Begin();
            for (var i = 0; i < strips; ++i)
            {
                var stripHeight = stripHeights[i];
                var root = new Vector2((float) (innerRadius * Math.Cos(radian)) + centerX, (float) (innerRadius * Math.Sin(radian)) + centerY);
                var angle = radian - Math.PI / 2;
                var displayWidth = (float)(2 * Math.PI * innerRadius) / (strips * 2);
                var displayHeightPercent = Math.Max(stripHeight / stripHeightsMax, 0.1);
                var displayHeight = (float)((outerRadius - innerRadius) * displayHeightPercent);
                var size = new Vector2(displayWidth, displayHeight);
                var curStripColor = stripColor * (float)(displayHeightPercent + 0.5);
                var endRadian = beginRadian + radianOffset * stripsVisible;
                if (radian > beginRadian && radian < endRadian || radian < endRadian - 2 * Math.PI)
                {
                    _spriteBatch.Draw(_texture, root, new Rectangle?(), curStripColor, (float)angle, Vector2.Zero, size, SpriteEffects.None, 0);
                }
                
                radian += radianOffset;
            }
            _spriteBatch.End();
            _lastRenderedStripHeights = stripHeights.ToArray();

            base.Draw(gameTime);
        }
    }
}
