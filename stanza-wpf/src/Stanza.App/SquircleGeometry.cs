using System.Windows;
using System.Windows.Media;

namespace Stanza.App;

/// <summary>
/// 连续曲率圆角（squircle）几何：用超椭圆弧（指数 n≈4.6）替代普通圆弧。
/// 普通圆弧在切点处曲率突变（直线曲率 0 → 圆弧曲率 1/r），超椭圆的曲率从 0 平滑渐增，
/// 视觉上更圆润自然，即 Apple 连续圆角的常用逼近。
/// </summary>
public static class SquircleGeometry
{
    private const double Exponent = 4.6;   // 超椭圆指数 n
    private const int Samples = 16;        // 每个角的采样点数（抗锯齿下足够平滑）

    /// <summary>生成宽 width、高 height、圆角半径 radius 的 squircle 闭合几何。</summary>
    public static StreamGeometry Build(double width, double height, double radius)
    {
        var r = Math.Min(radius, Math.Min(width, height) / 2);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(r, 0), true, true);

            ctx.LineTo(new Point(width - r, 0), true, false);          // 上边
            AppendCorner(ctx, width - r, r, r, -90, 0);                // 右上角
            ctx.LineTo(new Point(width, height - r), true, false);     // 右边
            AppendCorner(ctx, width - r, height - r, r, 0, 90);        // 右下角
            ctx.LineTo(new Point(r, height), true, false);             // 下边
            AppendCorner(ctx, r, height - r, r, 90, 180);              // 左下角
            ctx.LineTo(new Point(0, r), true, false);                  // 左边
            AppendCorner(ctx, r, r, r, 180, 270);                      // 左上角
        }
        geo.Freeze();
        return geo;
    }

    /// <summary>向路径追加一段以 (cx, cy) 为中心、角度 fromDeg→toDeg 的超椭圆弧。</summary>
    private static void AppendCorner(StreamGeometryContext ctx,
        double cx, double cy, double r, double fromDeg, double toDeg)
    {
        var points = new Point[Samples + 1];
        for (var i = 0; i <= Samples; i++)
        {
            var theta = (fromDeg + (toDeg - fromDeg) * i / Samples) * Math.PI / 180;
            var cos = Math.Cos(theta);
            var sin = Math.Sin(theta);
            // 超椭圆参数方程：(|cos|^(2/n), |sin|^(2/n)) 保号
            points[i] = new Point(
                cx + r * Math.Sign(cos) * Math.Pow(Math.Abs(cos), 2.0 / Exponent),
                cy + r * Math.Sign(sin) * Math.Pow(Math.Abs(sin), 2.0 / Exponent));
        }
        ctx.PolyLineTo(points, true, false);
    }
}
