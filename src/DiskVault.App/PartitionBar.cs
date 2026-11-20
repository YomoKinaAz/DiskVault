using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskVault.Platform;
using DiskVault.Storage;

namespace DiskVault.App;

/// <summary>
/// 復元先ディスクのパーティション配置を横一列のバーで表示し、マウスで変更する。
/// ・右端のつまみをドラッグ → サイズ変更（最小サイズ〜入る大きさまで）
/// ・中ほどをドラッグ → 開始位置の移動（前後の空きが増減。後ろのパーティションは空きが無くなると押される）
/// ・ダブルクリック → 「残り全体」の切り替え
/// 各パーティションの幅 = 最小幅 + サイズ × 倍率（小さい EFI なども見えるようにする）。空きは倍率だけで描く。
/// </summary>
public sealed class PartitionBar : FrameworkElement
{
    const double MinW = 44, BarTop = 4, BarH = 58, Grip = 7;
    const long Align = LayoutPlanner.Align;
    List<PartitionPlan> _plan = [];
    long _first, _usableEnd;
    Func<PartitionPlan, string> _name = p => p.Source.Kind;
    int _drag = -1;
    bool _move;
    double _dragX;
    (long Len, bool Fill, long Gap)[] _snap = [];

    /// <summary>ドラッグ・ダブルクリックで配置を変えたとき。</summary>
    public event Action? LayoutEdited;
    /// <summary>今の配置が復元先に収まるか（LayoutPlanner.Build が通るか）。</summary>
    public Func<bool> Validate { get; set; } = () => true;
    public bool Dragging => _drag >= 0;

    public PartitionBar()
    {
        Height = BarTop + BarH + 20;
        Focusable = false;
    }

    public void Set(List<PartitionPlan> plan, long targetSize, Func<PartitionPlan, string> name)
    {
        _plan = plan.OrderBy(x => x.Source.Offset).ToList();
        _first = _plan.Count > 0 ? _plan[0].Source.Offset : Align;
        _usableEnd = (targetSize - LayoutPlanner.TailReserve) / Align * Align;
        _name = name;
        InvalidateVisual();
    }

    static long Up(long v) => (v + Align - 1) / Align * Align;

    /// <summary>1 バイトあたりのピクセル数。</summary>
    double Scale => _usableEnd > _first ? Math.Max(0, ActualWidth - _plan.Count * MinW) / (_usableEnd - _first) : 0;

    /// <summary>各パーティションの左端と幅（ピクセル）。</summary>
    List<(double X, double W)> Geometry()
    {
        var k = Scale;
        var g = new List<(double, double)>();
        double x = 0;
        foreach (var p in _plan)
        {
            x += Up(p.GapBefore) * k;
            double w = MinW + p.NewLength * k;
            g.Add((x, w));
            x += w;
        }
        return g;
    }

    /// <summary>新しい配置での各パーティションの開始位置（バイト）。</summary>
    List<long> Offsets()
    {
        var r = new List<long>();
        long off = _first;
        foreach (var p in _plan)
        {
            off += Up(p.GapBefore);
            r.Add(off);
            off = Up(off + p.NewLength);
        }
        r.Add(off);
        return r;
    }

    static Brush Fill(PartitionPlan p)
    {
        var t = p.Source.TypeGuid?.ToLowerInvariant();
        return t switch
        {
            PartitionRecord.EspType => B("#7A8699"),
            PartitionRecord.MsrType => B("#A9B1BD"),
            PartitionRecord.RecoveryType => B("#3D8C7F"),
            _ when p.Source.MbrType == 0x27 => B("#3D8C7F"),
            _ when p.Resizable => B("#2F6FBF"),
            _ => B("#8A94A3"),
        };
    }

    static SolidColorBrush B(string c)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c));
        b.Freeze();
        return b;
    }

    static readonly Brush UsedBrush = B("#33000000"), FreeBrush = B("#EEF1F5"), Text = Brushes.White, Dark = B("#2B3440"), Red = B("#CF222E"), Muted = B("#5A6472");
    static readonly Pen Edge = new(B("#FFFFFF"), 1.5), FreeEdge = new(B("#B8C0CC"), 1) { DashStyle = DashStyles.Dash }, GripPen = new(B("#E6FFFFFF"), 1.2),
        FocusPen = new(B("#F2B300"), 2);
    static readonly Typeface Face = new(new FontFamily("Yu Gothic UI, Meiryo UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Typeface Bold = new(new FontFamily("Yu Gothic UI, Meiryo UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    FormattedText Txt(string s, Brush b, double size = 12, bool bold = false, double maxW = 0)
    {
        var t = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? Bold : Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (maxW > 0)
        {
            t.MaxTextWidth = Math.Max(1, maxW);
            t.MaxLineCount = 1;
            t.Trimming = TextTrimming.CharacterEllipsis;
        }
        return t;
    }

    static string Gb(long v) => v >= 1L << 30 ? $"{v / (double)(1L << 30):0.##} GB" : $"{v / (double)(1 << 20):0} MB";

    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth;
        dc.DrawRectangle(FreeBrush, FreeEdge, new Rect(0.5, BarTop + 0.5, Math.Max(0, W - 1), BarH - 1));
        if (_plan.Count == 0 || Scale <= 0) return;
        var g = Geometry();
        var offs = Offsets();
        long end = offs[^1];
        double prevEnd = 0;
        for (int i = 0; i < _plan.Count; i++)
        {
            var p = _plan[i];
            var (x, w) = g[i];
            // 前の空き（広ければ大きさを書く）
            long gap = Up(p.GapBefore);
            if (gap > 0 && x - prevEnd > 46)
                dc.DrawText(Txt(T("空き") + "\n" + Gb(gap), Dark, 11), new Point(prevEnd + 5, BarTop + 6));
            prevEnd = x + w;
            if (x >= W) break;
            var r = new Rect(x, BarTop, Math.Max(1, Math.Min(w, W - x)), BarH);
            dc.PushClip(new RectangleGeometry(r));
            dc.DrawRectangle(Fill(p), null, r);
            // 使用中（縮められない部分）を下側に濃く表示
            if (p.Resizable && p.NewLength > 0)
                dc.DrawRectangle(UsedBrush, null, new Rect(x, BarTop + BarH - 10, w * Math.Min(1, p.MinLength / (double)p.NewLength), 10));
            dc.DrawText(Txt($"{p.Source.Number}  {_name(p)}", Text, 12, true, w - 10), new Point(x + 6, BarTop + 6));
            dc.DrawText(Txt(Gb(p.NewLength) + (p.Fill ? T("（残り全体）") : ""), Text, 12, false, w - 10), new Point(x + 6, BarTop + 24));
            dc.Pop();
            if (i == _drag) dc.DrawRectangle(null, FocusPen, new Rect(x + 1, BarTop + 1, Math.Max(1, w - 2), BarH - 2));
            dc.DrawLine(Edge, new Point(x, BarTop), new Point(x, BarTop + BarH));
            dc.DrawLine(Edge, new Point(x + w, BarTop), new Point(x + w, BarTop + BarH));
            if (p.Resizable)
            {
                double gx = x + w - 4;
                for (int k = 0; k < 3; k++)
                    dc.DrawLine(GripPen, new Point(gx - k * 3, BarTop + BarH / 2 - 9), new Point(gx - k * 3, BarTop + BarH / 2 + 9));
            }
        }
        long free = _usableEnd - end;
        if (free < 0)
        {
            dc.DrawRectangle(null, new Pen(Red, 2), new Rect(1, BarTop + 1, W - 2, BarH - 2));
            dc.DrawText(Txt(T("{0} 不足しています", Gb(-free)), Red), new Point(0, BarTop + BarH + 2));
            return;
        }
        if (free >= Align * 16 && W - prevEnd > 50)
            dc.DrawText(Txt(T("未割り当て") + "\n" + Gb(free), Dark, 11), new Point(prevEnd + 6, BarTop + 6));
        string info;
        if (_drag >= 0 && _move)
            info = T("パーティション {0}: 開始位置 {1}（前の空き {2}）", _plan[_drag].Source.Number, Gb(offs[_drag]), Gb(Up(_plan[_drag].GapBefore)));
        else if (_drag >= 0)
            info = T("パーティション {0}: {1}（最小 {2}）", _plan[_drag].Source.Number, Gb(_plan[_drag].NewLength), Gb(_plan[_drag].MinLength));
        else
            info = T("右端のつまみ＝サイズ変更　中ほどをドラッグ＝位置の移動　ダブルクリック＝「残り全体」に");
        dc.DrawText(Txt(info, Muted, 11.5), new Point(0, BarTop + BarH + 2));
    }

    /// <summary>x 座標にあるつまみ（サイズ変更できるパーティションの右端）。</summary>
    int GripAt(double x)
    {
        var g = Geometry();
        for (int i = 0; i < _plan.Count; i++)
        {
            double d = x - (g[i].X + g[i].W); // つまみは境界の左側に描いているので左に広めに取る
            if (_plan[i].Resizable && d >= -Grip * 2 && d <= Grip) return i;
        }
        return -1;
    }

    int SegmentAt(double x)
    {
        var g = Geometry();
        for (int i = 0; i < _plan.Count; i++)
            if (x >= g[i].X && x < g[i].X + g[i].W) return i;
        return -1;
    }

    void Snapshot() => _snap = _plan.Select(p => (p.NewLength, p.Fill, p.GapBefore)).ToArray();

    void Restore()
    {
        for (int i = 0; i < _plan.Count && i < _snap.Length; i++)
            (_plan[i].NewLength, _plan[i].Fill, _plan[i].GapBefore) = _snap[i];
    }

    /// <summary>ドラッグ開始時の状態から、パーティション j を delta バイト動かした配置にする。</summary>
    void ApplyMove(int j, long delta)
    {
        Restore();
        var p = _plan[j];
        delta = Math.Max(delta, -p.GapBefore);
        p.GapBefore += delta;
        if (j + 1 < _plan.Count)
        {
            var n = _plan[j + 1];
            // 右へ動かすときは次の前の空きから使い、足りなければ次以降を押す。左へ動かすと次の前に空きができる。
            n.GapBefore = delta >= 0 ? Math.Max(0, n.GapBefore - delta) : n.GapBefore - delta;
        }
    }

    void ApplyResize(int j, long len)
    {
        Restore();
        _plan[j].Fill = false;
        _plan[j].NewLength = len;
    }

    /// <summary>want が収まらなければ、0（開始時）との間で収まる最大の値を探して適用する。</summary>
    void ApplyBest(Action<long> apply, long start, long want)
    {
        apply(want);
        if (Validate()) return;
        long ok = start, ng = want;
        while (Math.Abs(ng - ok) > Align)
        {
            long mid = (ok + ng) / 2 / Align * Align;
            if (mid == ok || mid == ng) break;
            apply(mid);
            if (Validate()) ok = mid; else ng = mid;
        }
        apply(ok);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (pos.Y < BarTop || pos.Y > BarTop + BarH) return;
        if (e.ClickCount == 2)
        {
            int s = SegmentAt(pos.X);
            if (s >= 0 && _plan[s].Resizable)
            {
                Snapshot();
                bool on = !_plan[s].Fill;
                foreach (var p in _plan) p.Fill = false;
                _plan[s].Fill = on;
                if (!Validate()) Restore();
                LayoutEdited?.Invoke();
                InvalidateVisual();
            }
            e.Handled = true;
            return;
        }
        int gi = GripAt(pos.X);
        _move = gi < 0;
        if (_move) gi = SegmentAt(pos.X);
        if (gi < 0) return;
        _drag = gi;
        _dragX = pos.X;
        Snapshot();
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (_drag < 0)
        {
            bool inBar = pos.Y >= BarTop && pos.Y <= BarTop + BarH;
            Cursor = !inBar ? null : GripAt(pos.X) >= 0 ? Cursors.SizeWE : SegmentAt(pos.X) >= 0 ? Cursors.SizeAll : null;
            return;
        }
        var k = Scale;
        if (k <= 0) return;
        long delta = (long)((pos.X - _dragX) / k) / Align * Align;
        int j = _drag;
        if (_move) ApplyBest(d => ApplyMove(j, d), 0, delta);
        else
        {
            var p = _plan[j];
            long start = _snap[j].Len;
            long want = Math.Max(p.MinLength, start + delta);
            ApplyBest(len => ApplyResize(j, len), Math.Max(p.MinLength, Math.Min(start, want)), want);
        }
        LayoutEdited?.Invoke();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag < 0) return;
        _drag = -1;
        ReleaseMouseCapture();
        LayoutEdited?.Invoke();
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _drag = -1;
        InvalidateVisual();
    }
}
