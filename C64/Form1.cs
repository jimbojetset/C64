using _6502CPU;
using System;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace C64
{
    public partial class Form1 : Form
    {

        private byte _width = 40;//32;//40;
        private byte _height = 25;//16;//25;
        private _6502_CPU cpu;
        private ushort _screenStartAddress = 0x3000;//0x8000;//0x400; // 0xC000
        private bool displayRunning = true;

        public Form1()
        {
            InitializeComponent();

            cpu = new _6502_CPU();
            //cpu.memory.Load(@"ROMS\BASIC.ROM", 0xA000, 8192, true);
            //cpu.memory.Load(@"ROMS\KERNAL.ROM", 0xE000, 8192, true);
            //cpu.memory.Load(@"ROMS\CHAR.ROM", 0xD000, 4096, false);

            //cpu.memory.Load(@"ROMS\kernel_rom.ROM", 0xF000, 4096, true);
            //cpu.memory.Load(@"ROMS\basic_rom.ROM", 0xC000, 4096, true);


            cpu.memory.Load(@"ROMS\Electron\basic.rom", 0x8000, 16384, true);
            cpu.memory.Load(@"ROMS\Electron\os", 0xC000, 16384, true);

            var processorThread = new Thread(() => cpu.Run())
            {
                IsBackground = true
            };
            processorThread.Start();

            var runThread = new Thread(() => Run())
            {
                IsBackground = true
            };
            runThread.Start();
            var run2Thread = new Thread(() => Run2())
            {
                IsBackground = true
            };
            run2Thread.Start();
        }

        private void Run()
        {
            Thread.Sleep(10);
            displayRunning = true;
            while (displayRunning)
            {
                var current = GetCurrentState();
                StringBuilder sb = new StringBuilder();
                for (byte y = 0; y < _height; y += 1)
                {
                    for (byte x = 0; x < _width; x += 1)
                    {
                        if (x >= _width || y >= _height) continue;
                        sb.Append(current[x, y]);
                    }
                    sb.Append("\r\n");
                }
                textBox1.Invoke((MethodInvoker)delegate { textBox1.Text = sb.ToString(); });
                if (cpu.memory.ReadByte(0xD012) != 0x0)
                    cpu.memory.WriteByte(0xD012, (byte)0x0);
                //cpu.CIA_IRQ = true;
                Thread.Sleep(16);
            }
        }

        private void Run2()
        {

            Thread.Sleep(10);
            displayRunning = true;
            while (displayRunning)
            {
                byte[] screen = new byte[8000];
                Buffer.BlockCopy(cpu.memory.memory, _screenStartAddress, screen,0, 8000);
                Bitmap bitmap = ConvertToBitmap(screen);
                pictureBox1.BackgroundImage = bitmap;
                var current = GetCurrentState2();
                StringBuilder sb = new StringBuilder();
                int t = 0;
                for (byte y = 0; y < _height; y += 1)
                {
                    for (byte x = 0; x < _width; x += 1)
                    {
                        if (x >= _width || y >= _height) continue;
                        sb.Append(current[t].ToString("x2") + " ");
                        t++;
                    }
                    sb.Append("\r\n");
                }
                label1.Invoke((MethodInvoker)delegate { label1.Text = sb.ToString(); });
                if (cpu.memory.ReadByte(0xD012) != 0x0)
                    cpu.memory.WriteByte(0xD012, (byte)0x0);
                //cpu.CIA_IRQ = true;
                Thread.Sleep(16);
            }
        }

        private char[,] GetCurrentState()
        {
            var result = new char[_width, _height];
            var currentAdr = _screenStartAddress;
            for (byte y = 0; y < _height; y++)
                for (byte x = 0; x < _width; x++)
                    result[x, y] = C64CharConverter.ConvertToAscii(cpu.memory.ReadByte(currentAdr++));
            return result;
        }

        private byte[] GetCurrentState2()
        {
            byte[] result = new byte[10000];
            var currentAdr = _screenStartAddress;
            int t = 0;
            for (byte y = 0; y < _height; y++)
                for (byte x = 0; x < _width; x++)
                    result[t++] = cpu.memory.ReadByte(currentAdr++);
            return result;
        }

        [DllImport("user32.dll")]
        static extern bool HideCaret(IntPtr hWnd);
        private void textBox1_TextChanged(object sender, EventArgs e)
        {
            HideCaret(textBox1.Handle);
        }

        static Bitmap ConvertToBitmap(byte[] data)
        {
            if (data.Length != 8000)
                throw new ArgumentException("Data must contain exactly 1000 bytes.");

            const int charsPerRow = 40;
            const int charRows = 25;
            const int bytesPerChar = 8;
            const int charWidth = 8;   // 8 pixels wide
            const int charHeight = 7;  // 7 pixels tall

            // Original image dimensions
            const int originalWidth = charsPerRow * charWidth; // 40 * 8 = 320 pixels
            const int originalHeight = charRows * charHeight;  // 25 * 7 = 175 pixels

            // Target image dimensions
            const int targetWidth = originalWidth; // 320 pixels
            const int targetHeight = 175;          // As per your requirement

            // Create a bitmap with the original dimensions
            Bitmap originalBitmap = new Bitmap(originalWidth, originalHeight);

            // Lock the bitmap's bits for faster access
            BitmapData bmpData = originalBitmap.LockBits(
                new Rectangle(0, 0, originalBitmap.Width, originalBitmap.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format24bppRgb);

            unsafe
            {
                byte* ptr = (byte*)bmpData.Scan0;

                int bytesPerPixel = Image.GetPixelFormatSize(bmpData.PixelFormat) / 8;
                int stride = bmpData.Stride;

                for (int charY = 0; charY < charRows; charY++)
                {
                    for (int charX = 0; charX < charsPerRow; charX++)
                    {
                        // Calculate the offset in the byte array
                        int offset = ((charY * charsPerRow + charX) * bytesPerChar);

                        // For each byte (row) in the character
                        for (int byteIndex = 0; byteIndex < bytesPerChar; byteIndex++)
                        {
                            byte currentByte = data[offset + byteIndex];

                            // For each bit in the byte
                            for (int bitIndex = 0; bitIndex < 8; bitIndex++)
                            {
                                // Check if the bit is set
                                bool isSet = (currentByte & (1 << (7 - bitIndex))) != 0;

                                // Calculate the pixel position
                                int pixelX = charX * charWidth + bitIndex;
                                int pixelY = charY * charHeight + byteIndex;

                                if (pixelX >= originalWidth || pixelY >= originalHeight)
                                    continue;

                                // Set the pixel color
                                int index = pixelY * stride + pixelX * bytesPerPixel;

                                // Set pixel to white or black
                                ptr[index + 0] = isSet ? (byte)255 : (byte)0; // Blue component
                                ptr[index + 1] = isSet ? (byte)255 : (byte)0; // Green component
                                ptr[index + 2] = isSet ? (byte)255 : (byte)0; // Red component
                            }
                        }
                    }
                }
            }

            // Unlock the bits
            originalBitmap.UnlockBits(bmpData);

            // Now, resize the original bitmap to the target dimensions
            Bitmap targetBitmap = new Bitmap(targetWidth, targetHeight);

            using (Graphics g = Graphics.FromImage(targetBitmap))
            {
                // Use nearest neighbor interpolation to preserve pixel clarity
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(originalBitmap, 0, 0, targetWidth, targetHeight);
            }

            return targetBitmap;
        }
    }
}
