namespace CrushGheChoi.Core.Effects;

/// <summary>Một trái tim đang bay. Tọa độ tính bằng DIP trong cửa sổ crush.</summary>
public sealed class HeartParticle
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Vx { get; set; }

    public double Vy { get; set; }

    public double Age { get; set; }

    public double Life { get; set; }

    public double Size { get; set; }

    public double Rotation { get; set; }

    public double Spin { get; set; }

    public double WobbleAmplitude { get; set; }

    public double WobbleFrequency { get; set; }

    public double WobblePhase { get; set; }

    public int ColorIndex { get; set; }

    public bool Pop { get; set; }

    public double DrawX => X + Math.Sin(Age * WobbleFrequency + WobblePhase) * WobbleAmplitude;

    /// <summary>Phóng lớn nhẹ lúc mới hiện, rồi nhỏ dần khi bay lên.</summary>
    public double Scale
    {
        get
        {
            var u = Math.Clamp(Age / Life, 0, 1);
            var pop = 1.0;
            if (Pop)
            {
                pop = Age < 0.22 ? 0.3 + 0.82 * EaseOutBack(Age / 0.22)
                    : Age < 0.4 ? 1.12 - 0.12 * ((Age - 0.22) / 0.18)
                    : 1.0;
            }

            return pop * (1 - 0.35 * Math.Clamp((u - 0.2) / 0.8, 0, 1));
        }
    }

    public double Opacity
    {
        get
        {
            var u = Math.Clamp(Age / Life, 0, 1);
            var fadeIn = Math.Clamp(Age / (Pop ? 0.08 : 0.3), 0, 1);
            var fadeOut = u < 0.55 ? 1 : 1 - (u - 0.55) / 0.45;
            return fadeIn * Math.Clamp(fadeOut, 0, 1);
        }
    }

    private static double EaseOutBack(double x)
    {
        const double c1 = 1.70158;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2);
    }
}

/// <summary>Sinh, cập nhật và thu hồi tim. Giới hạn số tim đang bay để không tốn tài nguyên.</summary>
public sealed class HeartSimulation(Random? rng = null)
{
    public const int MaxActive = 24;
    public const int MaxPerBurst = 8;

    private readonly Random _rng = rng ?? Random.Shared;
    private readonly List<HeartParticle> _active = [];
    private readonly List<PendingHeart> _pending = [];

    public IReadOnlyList<HeartParticle> Active => _active;

    public bool IsIdle => _active.Count == 0 && _pending.Count == 0;

    public bool ReducedMotion { get; set; }

    public int PaletteSize { get; set; } = 4;

    /// <summary>
    /// Phát một chùm tim từ (x, y). Trái đầu hiện ngay, các trái sau cách nhau 0,15–0,25 giây.
    /// <paramref name="drift"/> là -1 (bay lệch sang trái) hoặc +1 (sang phải).
    /// </summary>
    public void Burst(double x, double y, int count, double drift)
    {
        count = Math.Clamp(count, 1, MaxPerBurst);
        if (ReducedMotion)
        {
            count = Math.Min(count, 2);
        }

        var delay = 0.0;
        for (var i = 0; i < count; i++)
        {
            _pending.Add(new PendingHeart(delay, x, y, Math.Sign(drift) == 0 ? 1 : Math.Sign(drift), i == 0));
            delay += 0.15 + _rng.NextDouble() * 0.10;
        }
    }

    public void Update(double dt)
    {
        if (dt <= 0)
        {
            return;
        }

        // Khung hình bị trễ (ví dụ máy bận) thì không cho tim nhảy cóc.
        dt = Math.Min(dt, 0.1);

        for (var i = 0; i < _pending.Count; i++)
        {
            var p = _pending[i];
            p.Delay -= dt;
            if (p.Delay > 0)
            {
                continue;
            }

            _pending.RemoveAt(i--);
            if (_active.Count < MaxActive)
            {
                _active.Add(Spawn(p));
            }
        }

        var damping = Math.Pow(0.75, dt);
        foreach (var h in _active)
        {
            h.Age += dt;
            h.X += h.Vx * dt;
            h.Y += h.Vy * dt;
            h.Vy *= damping;
            h.Vx *= damping;
            h.Rotation += h.Spin * dt;
        }

        _active.RemoveAll(h => h.Age >= h.Life);
    }

    public void Clear()
    {
        _active.Clear();
        _pending.Clear();
    }

    private HeartParticle Spawn(PendingHeart p)
    {
        var m = ReducedMotion ? 0.6 : 1.0;
        return new HeartParticle
        {
            X = p.X + Range(-4, 4),
            Y = p.Y + Range(-4, 4),
            Vx = (p.Drift * Range(12, 48) + Range(-12, 12)) * m,
            Vy = -Range(80, 120) * m,
            Life = Range(2.2, 2.8),
            Size = p.First ? 34 : Range(22, 30),
            Rotation = Range(-12, 12),
            Spin = Range(-10, 10) * m,
            WobbleAmplitude = ReducedMotion ? 0 : Range(5, 11),
            WobbleFrequency = Range(3.0, 4.5),
            WobblePhase = Range(0, Math.Tau),
            ColorIndex = _rng.Next(Math.Max(1, PaletteSize)),
            Pop = !ReducedMotion,
        };
    }

    private double Range(double min, double max) => min + _rng.NextDouble() * (max - min);

    private sealed class PendingHeart(double delay, double x, double y, int drift, bool first)
    {
        public double Delay { get; set; } = delay;

        public double X { get; } = x;

        public double Y { get; } = y;

        public int Drift { get; } = drift;

        public bool First { get; } = first;
    }
}
