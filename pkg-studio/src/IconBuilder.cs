using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace PkgStudio
{
    /// <summary>
    /// 生成解锁器 PKG 的 icon0.png：游戏自己的封面作底图，中间叠一个奖杯图标。
    ///
    /// 输出固定 512x512、不带 alpha 的 PNG —— PS4 的 icon0.png 就是这个规格，
    /// 模板里那份也是 512x512 全不透明。gp4 里已有 sce_sys/icon0.png 条目，
    /// 所以只要覆盖 CONTENTS 下的文件，不用改工程文件。
    /// </summary>
    internal static class IconBuilder
    {
        private const int SizePx = 512;

        /// <summary>
        /// gameIcon = 游戏的 icon0.png（本地 PKG 或 FTP 取来的），可以为空；
        /// trophy = 透明背景的奖杯图标（template/icon1.png），为空则不改图标；
        /// templateIcon = 模板解出来的 icon0.png，作为没有游戏封面时的兜底底图。
        /// 返回 PNG 字节；没有任何底图/奖杯素材时返回 null，让调用方保留模板原样。
        /// </summary>
        public static byte[] Compose(byte[] gameIcon, byte[] trophy, string templateIcon)
        {
            if (trophy == null || trophy.Length == 0)
                return null;

            Bitmap baseImg = LoadBitmap(gameIcon);
            bool fromGame = baseImg != null;

            if (baseImg == null && File.Exists(templateIcon))
            {
                try { baseImg = new Bitmap(templateIcon); }
                catch { baseImg = null; }
            }
            if (baseImg == null)
                return null;

            try
            {
                using (baseImg)
                using (Image cup = LoadBitmap(trophy))
                using (Bitmap canvas = new Bitmap(SizePx, SizePx, PixelFormat.Format24bppRgb))
                using (Graphics g = Graphics.FromImage(canvas))
                {
                    if (cup == null)
                        return null;

                    g.Clear(Color.Black);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;

                    // 1. 底图等比放大并居中裁剪，铺满 512x512
                    float k = Math.Max((float)SizePx / baseImg.Width, (float)SizePx / baseImg.Height);
                    float dw = baseImg.Width * k, dh = baseImg.Height * k;
                    g.DrawImage(baseImg,
                        new RectangleF((SizePx - dw) / 2f, (SizePx - dh) / 2f, dw, dh));

                    // 2. 奖杯压在中间偏上，高度约占 64%（参考示意图的比例）
                    float cupH = SizePx * 0.64f;
                    float cupK = cupH / cup.Height;
                    float cupW = cup.Width * cupK;
                    float cupX = (SizePx - cupW) / 2f;
                    float cupY = SizePx * 0.03f;

                    // 纯叠加：参考示意图里奖杯直接压在封面上，不加底色圆盘
                    g.DrawImage(cup, new RectangleF(cupX, cupY, cupW, cupH));

                    using (MemoryStream ms = new MemoryStream())
                    {
                        canvas.Save(ms, ImageFormat.Png);
                        return ms.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("图标合成失败（保留模板图标）：" + ex.Message);
                return null;
            }
            finally
            {
                // 仅用于日志：让调用方知道底图来自哪里
                Log.Write("图标底图: " + (fromGame ? "游戏 icon0.png" : "模板 icon0.png"));
            }
        }

        /// <summary>从字节数组加载成独立 Bitmap（脱离 MemoryStream 的生命周期）。</summary>
        private static Bitmap LoadBitmap(byte[] data)
        {
            if (data == null || data.Length == 0)
                return null;
            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                using (Image tmp = Image.FromStream(ms))
                    return new Bitmap(tmp);
            }
            catch
            {
                return null;
            }
        }
    }
}
