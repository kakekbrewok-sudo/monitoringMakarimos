using System;
using System.Drawing;
using System.Drawing.Drawing2D;

static class DotClock
{
    // Original 5x7 dot glyphs; no external fonts or image resources.
    static readonly string[] Digits = {
        "01110/11011/11011/11011/11011/11011/01110",
        "00110/01110/00110/00110/00110/00110/01111",
        "01110/11011/00011/00110/01100/11000/11111",
        "11110/00011/00011/01110/00011/00011/11110",
        "00011/00111/01111/11011/11111/00011/00011",
        "11111/11000/11000/11110/00011/00011/11110",
        "01110/11000/11000/11110/11011/11011/01110",
        "11111/00011/00110/00110/01100/01100/01100",
        "01110/11011/11011/01110/11011/11011/01110",
        "01110/11011/11011/01111/00011/00011/01110" };
    static readonly string[][] Rows = Array.ConvertAll(Digits, d => d.Split('/'));
    public static void Draw(Graphics g, string text, float x, float y, float pitch, Color ink, float scale)
    {
        var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(ink))
        {
            foreach (char c in text)
            {
                if (c >= '0' && c <= '9')
                {
                    string[] rows = Rows[c - '0'];
                    for (int row = 0; row < 7; row++) for (int col = 0; col < 5; col++) if (rows[row][col] == '1')
                        g.FillEllipse(brush, (x + col * pitch) * scale, (y + row * pitch) * scale, pitch * .73f * scale, pitch * .73f * scale);
                    x += 6 * pitch;
                }
                else if (c == ':')
                {
                    for (int row = 2; row <= 4; row += 2) g.FillEllipse(brush, x * scale, (y + row * pitch) * scale, pitch * .73f * scale, pitch * .73f * scale);
                    x += 2 * pitch;
                }
                else x += 3 * pitch;
            }
        }
        g.SmoothingMode = old;
    }
}
