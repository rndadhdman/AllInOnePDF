using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Elements;

/// <summary>
/// A vertical stack (QuestPDF's Column). Items are laid out top to bottom with <see cref="Spacing"/> between visible
/// items; the column paginates item by item and remembers the first item that is not finished yet.
/// </summary>
internal sealed class ColumnElement : Element
{
    private readonly List<Command> _commands = [];
    private int _index;

    public List<Element> Items { get; } = [];

    public float Spacing { get; set; }

    public override IEnumerable<Element> GetChildren() => Items;

    public override void Reset()
    {
        base.Reset();
        _index = 0;
    }

    public override SpacePlan Measure(Size availableSpace)
    {
        if (_index >= Items.Count)
        {
            return SpacePlan.Empty();
        }

        Plan(availableSpace);
        if (_commands.Count == 0)
        {
            return SpacePlan.Wrap();
        }

        float width = 0;
        float height = 0;
        var allEmpty = true;
        var finished = 0;
        foreach (var command in _commands)
        {
            if (!command.Plan.IsEmpty)
            {
                allEmpty = false;
                width = Math.Max(width, command.Plan.Width);
                height = Math.Max(height, command.Offset + command.Plan.Height);
            }

            if (!command.Plan.IsPartial)
            {
                finished++;
            }
        }

        var complete = _index + finished == Items.Count;
        if (complete && allEmpty)
        {
            return SpacePlan.Empty();
        }

        return complete ? SpacePlan.FullRender(width, height) : SpacePlan.PartialRender(width, height);
    }

    public override void Draw(Size availableSpace)
    {
        if (_index >= Items.Count)
        {
            return;
        }

        Plan(availableSpace);
        var next = _index;
        foreach (var command in _commands)
        {
            var offset = new Position(0, command.Offset);
            Canvas.Translate(offset);
            Items[command.Index].Draw(new Size(availableSpace.Width, command.Plan.Height));
            Canvas.Translate(offset.Reverse());
            if (!command.Plan.IsPartial)
            {
                next = command.Index + 1;
            }
        }

        _index = next;
    }

    /// <summary>Fills <see cref="_commands"/> with the items that go on this page and their offsets.</summary>
    private void Plan(Size availableSpace)
    {
        _commands.Clear();
        float top = 0;
        for (var i = _index; i < Items.Count; i++)
        {
            var height = availableSpace.Height - top;
            if (height < -LayoutMath.Epsilon)
            {
                break;
            }

            var plan = Items[i].Measure(new Size(availableSpace.Width, Math.Max(0, height)));
            if (plan.IsWrap)
            {
                break;
            }

            _commands.Add(new Command(i, plan, top));
            if (plan.IsPartial)
            {
                break;
            }

            if (!plan.IsEmpty)
            {
                top += plan.Height + Spacing;
            }
        }
    }

    private readonly record struct Command(int Index, SpacePlan Plan, float Offset);
}
