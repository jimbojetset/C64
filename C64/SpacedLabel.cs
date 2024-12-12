using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace C64
{
    public class SpacedLabel : Control
    {
        private string[] parts;

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);

            float lineHeight = g.MeasureString("X", Font).Height;

            lineHeight += Spacing;

            using (Brush brush = new SolidBrush(ForeColor))
            {
                for (int i = 0; i < parts.Length; i++)
                {
                    g.DrawString(parts[i], Font, brush, 0, i * lineHeight);
                }
            }
        }

        public override string Text
        {
            get
            {
                return base.Text;
            }
            set
            {
                base.Text = value;
                parts = (value ?? "").Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        /// <summary>
        /// Controls the change in spacing between lines.
        /// </summary>
        public float Spacing { get; set; }
    }
}
