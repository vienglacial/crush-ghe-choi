namespace CrushGheChoi.Core.Greetings;

/// <summary>
/// Bốc chỉ số kiểu "túi xáo trộn": phát hết lượt mới lặp, và không lặp ngay phần tử vừa bốc.
/// Dùng cho câu chào và cho ảnh của bộ hình.
/// </summary>
public sealed class ShuffleBag(Random? rng = null)
{
    private readonly Random _rng = rng ?? Random.Shared;
    private readonly Queue<int> _bag = new();
    private int _count = -1;
    private int _last = -1;

    /// <summary>Chỉ số kế tiếp trong [0, count); -1 nếu danh sách rỗng.</summary>
    public int Next(int count)
    {
        if (count <= 0)
        {
            return -1;
        }

        if (count != _count)
        {
            _bag.Clear();
            _count = count;
            if (_last >= count)
            {
                _last = -1;
            }
        }

        if (_bag.Count == 0)
        {
            Refill();
        }

        _last = _bag.Dequeue();
        return _last;
    }

    /// <summary>Gọi khi nội dung danh sách thay đổi.</summary>
    public void Reset()
    {
        _bag.Clear();
        _count = -1;
        _last = -1;
    }

    private void Refill()
    {
        var order = Enumerable.Range(0, _count).ToArray();
        _rng.Shuffle(order);
        if (order.Length > 1 && order[0] == _last)
        {
            (order[0], order[^1]) = (order[^1], order[0]);
        }

        foreach (var i in order)
        {
            _bag.Enqueue(i);
        }
    }
}

/// <summary>Bốc câu chào: dùng hết các câu mới lặp lại, không nói lại ngay câu vừa nói.</summary>
public sealed class GreetingPicker(Random? rng = null)
{
    private readonly ShuffleBag _bag = new(rng);

    public string? Pick(IReadOnlyList<string> greetings) =>
        _bag.Next(greetings.Count) is var i and >= 0 ? greetings[i] : null;

    public void Reset() => _bag.Reset();
}
