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
        private ushort _screenStartAddress = 0x400;//0x8000; // 0x0400
        private bool displayRunning = true;
        public bool V_Sync = false;

        public Form1()
        {
            InitializeComponent();

            cpu = new _6502_CPU();
            cpu.memory.Load(@"ROMS\BASIC.ROM", 0xA000, 0x2000, true);
            cpu.memory.Load(@"ROMS\KERNAL.ROM", 0xE000, 0x2000, true);
            cpu.memory.Load(@"ROMS\CHAR.ROM", 0xD000, 0x1000, false);

            textBox1.ReadOnly = true;
            textBox1.GotFocus += textBox1_GotFocus!;

            var processorThread = new Thread(() => cpu.Run())
            {
                IsBackground = true
            };
            processorThread.Start();
            while (textBox1.Handle == 0) { }
            var runThread = new Thread(() => Run())
            {
                IsBackground = true
            };
            runThread.Start();

        }

        private void Run()
        {
            displayRunning = true;
            while (displayRunning)
            {
                Stopwatch sw = Stopwatch.StartNew();
                var currentAdr = _screenStartAddress;
                StringBuilder sb = new StringBuilder();
                for (byte y = 0; y < _height; y += 1)
                {
                    sw.Restart();
                    for (byte x = 0; x < _width; x += 1)
                    {
                        sb.Append(C64CharConverter.ConvertToAscii(cpu.memory.ReadByte(currentAdr++)));
                    }
                    sb.Append("\r\n");
                    cpu.memory.WriteByte(0xD012, (byte)y);
                    while (sw.ElapsedMilliseconds == 0) { }
                }
                textBox1.Invoke((MethodInvoker)delegate { textBox1.Text = sb.ToString(); });
            }
        }

        private void textBox1_GotFocus(object sender, EventArgs e)
        {
            ((System.Windows.Forms.TextBox)sender).Parent!.Focus();
        }

    }
}
