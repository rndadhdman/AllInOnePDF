using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Elements;

/// <summary>How a row item gets its width.</summary>
internal enum RowItemKind
{
    /// <summary>A share of the width left after constant and auto items, by weight.</summary>
    Relative,

    /// <summary>Exactly the given width.</summary>
    Constant,

    /// <summary>The content's natural width (measured with infinite space).</summary>
    Auto,
}

/// <summary>One cell of a <see cref="RowElement"/>.</summary>
internal sealed class RowItem(RowItemKind kind, float value)
{
    public RowItemKind Kind { get; } = kind;

    /// <summary>Weight for relative items, width for constant items, unused for auto items.</summary>
    public float Value { get; } = value;

    public SlotElement Content { get; } = new();
}

/// <summary>
/// A horizontal layout (QuestPDF's Row). Every item is drawn with the row's height (the tallest item); when an item
/// continues on the next page the row does too, and items that finished are not drawn again.
/// </summary>
internal sealed class RowElement : Element
{
    private float[] _widths = [];
    private SpacePlan[] _plans = [];
    private bool[] _done = [];

    public List<RowItem> Items { get; } = [];

    public float Spacing { get; set; }

    public override IEnumerable<Element> GetChildren() => Items.Select(static i => i.Content);

    public override void Reset()
    {
        base.Reset();
        Array.Clear(_done);
    }

    public override SpacePlan Measure(Size availableSpace)
    {
        EnsureArrays();
        if (Items.Count == 0 || Array.TrueForAll(_done, static d => d))
        {
            return SpacePlan.Empty();
        }

        var totalWidth = Layout(availableSpace);
        if (totalWidth > availableSpace.Width + LayoutMath.Epsilon)
        {
            return SpacePlan.Wrap();
        }

        float height = 0;
        var partial = false;
        var allEmpty = true;
        for (var i = 0; i < Items.Count; i++)
        {
            if (_done[i])
            {
                continue;
            }

            var plan = _plans[i];
            if (plan.IsWrap)
            {
                return SpacePlan.Wrap();
            }

            if (plan.HasContent)
            {
                allEmpty = false;
                height = Math.Max(height, plan.Height);
                partial |= plan.IsPartial;
            }
        }

        if (allEmpty)
        {
            return SpacePlan.Empty();
        }

        return partial ? SpacePlan.PartialRender(totalWidth, height) : SpacePlan.FullRender(totalWidth, height);
    }

    public override void Draw(Size availableSpace)
    {
        EnsureArrays();
        if (Items.Count == 0)
        {
            return;
        }

        Layout(availableSpace);
        float height = 0;
        for (var i = 0; i < Items.Count; i++)
        {
            if (!_done[i] && _plans[i].HasContent)
            {
                height = Math.Max(height, _plans[i].Height);
            }
        }

        float x = 0;
        for (var i = 0; i < Items.Count; i++)
        {
            if (!_done[i] && !_plans[i].IsWrap)
            {
                var offset = new Position(x, 0);
                Canvas.Translate(offset);
                Items[i].Content.Draw(new Size(_widths[i], height));
                Canvas.Translate(offset.Reverse());
                _done[i] = !_plans[i].IsPartial;
            }

            x += _widths[i] + Spacing;
        }
    }

    private void EnsureArrays()
    {
        if (_widths.Length != Items.Count)
        {
            _widths = new float[Items.Count];
            _plans = new SpacePlan[Items.Count];
            _done = new bool[Items.Count];
        }
    }

    /// <summary>Computes every item's width and measures the unfinished items; returns the row's total width.</summary>
    private float Layout(Size availableSpace)
    {
        float fixedWidth = Spacing * (Items.Count - 1);
        float weights = 0;
        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            switch (item.Kind)
            {
                case RowItemKind.Constant:
                    _widths[i] = Math.Max(0, item.Value);
                    fixedWidth += _widths[i];
                    break;
                case RowItemKind.Auto:
                    var natural = item.Content.Measure(Size.Max);
                    _widths[i] = natural.HasContent ? natural.Width : 0;
                    fixedWidth += _widths[i];
                    break;
                default:
                    weights += Math.Max(0, item.Value);
                    break;
            }
        }

        var remaining = Math.Max(0, availableSpace.Width - fixedWidth);
        float total = fixedWidth;
        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            if (item.Kind == RowItemKind.Relative)
            {
                _widths[i] = weights > 0 ? remaining * Math.Max(0, item.Value) / weights : 0;
                total += _widths[i];
            }

            _plans[i] = _done[i] ? SpacePlan.Empty() : item.Content.Measure(new Size(_widths[i], availableSpace.Height));
        }

        return total;
    }
}
