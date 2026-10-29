using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace StreamLite.UI
{
    /// <summary>
    /// StreamLite's small icons (sidebar, title bar, device cards, hotkeys). Windows draws them from its own icon font
    /// (Segoe MDL2 Assets); Linux has no such font, so there they're drawn as simple line icons of the same meaning.
    /// </summary>
    internal static class Icons
    {
        /// <summary>The icon for an MDL2 code point, centred in r, the size the font would have drawn it.</summary>
        public static void Draw(Graphics g, string glyph, float fontSize, Rectangle r, Color c)
        {
#if LINUX
            if (string.IsNullOrEmpty(glyph)) return;
            float s = Math.Min(Math.Min(r.Width, r.Height), fontSize * 1.45f * g.DpiX / 72f);
            var box = new RectangleF(r.X + (r.Width - s) / 2f, r.Y + (r.Height - s) / 2f, s, s);
            var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(c, Math.Max(1.2f, s / 12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var b = new SolidBrush(c))
                Paint(g, glyph[0], box, p, b);
            g.SmoothingMode = old;
#else
            using (var f = new Font("Segoe MDL2 Assets", fontSize))
                TextRenderer.DrawText(g, glyph, f, r, c, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
#endif
        }

#if LINUX
        static PointF P(RectangleF b, float x, float y) { return new PointF(b.X + b.Width * x, b.Y + b.Height * y); }
        static RectangleF R(RectangleF b, float x, float y, float w, float h) { return new RectangleF(b.X + b.Width * x, b.Y + b.Height * y, b.Width * w, b.Height * h); }

        static void Paint(Graphics g, char c, RectangleF b, Pen p, Brush br)
        {
            switch ((int)c)
            {
                case 0xE711: case 0xE8BB:   // close / cancel
                    g.DrawLine(p, P(b, .2f, .2f), P(b, .8f, .8f)); g.DrawLine(p, P(b, .8f, .2f), P(b, .2f, .8f)); break;
                case 0xE921:                // minimize
                    g.DrawLine(p, P(b, .2f, .55f), P(b, .8f, .55f)); break;
                case 0xE740:                // full screen: corners outward
                    foreach (var k in new[] { new[] { .15f, .35f, .15f, .15f, .35f, .15f }, new[] { .65f, .15f, .85f, .15f, .85f, .35f }, new[] { .85f, .65f, .85f, .85f, .65f, .85f }, new[] { .35f, .85f, .15f, .85f, .15f, .65f } })
                        g.DrawLines(p, new[] { P(b, k[0], k[1]), P(b, k[2], k[3]), P(b, k[4], k[5]) });
                    break;
                case 0xE73F:                // back to a window: corners inward
                    foreach (var k in new[] { new[] { .15f, .4f, .4f, .4f, .4f, .15f }, new[] { .6f, .15f, .6f, .4f, .85f, .4f }, new[] { .85f, .6f, .6f, .6f, .6f, .85f }, new[] { .4f, .85f, .4f, .6f, .15f, .6f } })
                        g.DrawLines(p, new[] { P(b, k[0], k[1]), P(b, k[2], k[3]), P(b, k[4], k[5]) });
                    break;
                case 0xE713:                // settings: a gear
                {
                    var ctr = P(b, .5f, .5f); float ro = b.Width * .42f, ri = b.Width * .3f;
                    var pts = new PointF[32];
                    for (int i = 0; i < 32; i++) { double a = i * Math.PI / 16 + Math.PI / 32; float rr = (i / 2) % 2 == 0 ? ro : ri; pts[i] = new PointF(ctr.X + (float)(rr * Math.Cos(a)), ctr.Y + (float)(rr * Math.Sin(a))); }
                    g.DrawPolygon(p, pts);
                    g.DrawEllipse(p, R(b, .38f, .38f, .24f, .24f));
                    break;
                }
                case 0xE720:                // microphone
                    using (var path = Theme.Rounded(R(b, .36f, .08f, .28f, .5f), b.Width * .14f)) g.DrawPath(p, path);
                    g.DrawArc(p, R(b, .22f, .26f, .56f, .5f), 0, 180);
                    g.DrawLine(p, P(b, .5f, .76f), P(b, .5f, .9f)); g.DrawLine(p, P(b, .34f, .9f), P(b, .66f, .9f)); break;
                case 0xE7F6:                // headphones
                    g.DrawArc(p, R(b, .14f, .12f, .72f, .8f), 180, 180);
                    using (var path = Theme.Rounded(R(b, .1f, .5f, .2f, .34f), b.Width * .06f)) g.DrawPath(p, path);
                    using (var path = Theme.Rounded(R(b, .7f, .5f, .2f, .34f), b.Width * .06f)) g.DrawPath(p, path);
                    break;
                case 0xE714:                // video camera
                    using (var path = Theme.Rounded(R(b, .08f, .28f, .58f, .44f), b.Width * .07f)) g.DrawPath(p, path);
                    g.DrawPolygon(p, new[] { P(b, .68f, .44f), P(b, .92f, .3f), P(b, .92f, .7f), P(b, .68f, .56f) }); break;
                case 0xE722:                // camera
                    using (var path = Theme.Rounded(R(b, .08f, .28f, .84f, .56f), b.Width * .08f)) g.DrawPath(p, path);
                    g.DrawLine(p, P(b, .34f, .28f), P(b, .4f, .18f)); g.DrawLine(p, P(b, .4f, .18f), P(b, .6f, .18f)); g.DrawLine(p, P(b, .6f, .18f), P(b, .66f, .28f));
                    g.DrawEllipse(p, R(b, .36f, .38f, .28f, .3f)); break;
                case 0xE765:                // keyboard
                    using (var path = Theme.Rounded(R(b, .06f, .26f, .88f, .48f), b.Width * .06f)) g.DrawPath(p, path);
                    for (int i = 0; i < 4; i++) { g.FillRectangle(br, R(b, .18f + i * .19f, .36f, .06f, .06f)); g.FillRectangle(br, R(b, .18f + i * .19f, .48f, .06f, .06f)); }
                    g.DrawLine(p, P(b, .3f, .63f), P(b, .7f, .63f)); break;
                case 0xE95E:                // health: a pulse
                    g.DrawLines(p, new[] { P(b, .06f, .55f), P(b, .3f, .55f), P(b, .4f, .3f), P(b, .52f, .78f), P(b, .62f, .45f), P(b, .7f, .55f), P(b, .94f, .55f) }); break;
                case 0xE73E:                // check mark
                    g.DrawLines(p, new[] { P(b, .18f, .52f), P(b, .4f, .74f), P(b, .84f, .28f) }); break;
                case 0xE734:                // star
                {
                    var pts = new PointF[10];
                    for (int i = 0; i < 10; i++) { double a = -Math.PI / 2 + i * Math.PI / 5; float rr = i % 2 == 0 ? .45f : .19f; pts[i] = P(b, .5f + (float)(rr * Math.Cos(a)), .53f + (float)(rr * Math.Sin(a))); }
                    g.DrawPolygon(p, pts); break;
                }
                case 0xE8D6:                // sound: a speaker and waves
                    g.DrawPolygon(p, new[] { P(b, .1f, .4f), P(b, .26f, .4f), P(b, .46f, .22f), P(b, .46f, .78f), P(b, .26f, .6f), P(b, .1f, .6f) });
                    g.DrawArc(p, R(b, .44f, .32f, .2f, .36f), -60, 120); g.DrawArc(p, R(b, .44f, .18f, .38f, .64f), -60, 120); break;
                case 0xEBE8:                // bug
                    g.DrawEllipse(p, R(b, .3f, .3f, .4f, .56f)); g.DrawArc(p, R(b, .36f, .12f, .28f, .3f), 180, 180);
                    g.DrawLine(p, P(b, .3f, .5f), P(b, .12f, .44f)); g.DrawLine(p, P(b, .7f, .5f), P(b, .88f, .44f));
                    g.DrawLine(p, P(b, .3f, .7f), P(b, .12f, .78f)); g.DrawLine(p, P(b, .7f, .7f), P(b, .88f, .78f)); g.DrawLine(p, P(b, .5f, .42f), P(b, .5f, .86f)); break;
                default:
                    g.DrawEllipse(p, R(b, .3f, .3f, .4f, .4f)); break;
            }
        }
#endif
    }
}
