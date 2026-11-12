using AllInOnePdf.Publishing.Model;

namespace AllInOnePdf.UI.Services;

/// <summary>Turns a drop reported by the canvas or structure tree script into one <see cref="CreatorState"/> operation.</summary>
public static class CreatorDrops
{
    /// <summary>Applies a drop.</summary>
    /// <param name="state">The editing session.</param>
    /// <param name="kind">
    /// <c>module</c>, <c>item</c> (a free-flow item in the tree), <c>row</c>, <c>section</c> or <c>new</c> (an Insert
    /// tile; <paramref name="id"/> is the module type).
    /// </param>
    /// <param name="id">The dragged element's id, or the module type for <c>new</c>.</param>
    /// <param name="targetId">The column (modules), a free-flow section (modules and items), a section (rows) or empty (sections).</param>
    /// <param name="index">
    /// Drop position in the target list before the element is taken out; for a free-flow section, the position in its
    /// front-to-back order.
    /// </param>
    /// <returns>True when the publication changed.</returns>
    public static bool Apply(CreatorState state, string kind, string id, string targetId, int index)
    {
        ArgumentNullException.ThrowIfNull(state);
        switch (kind)
        {
            case "new":
                if (CreatorModuleCatalog.Find(id) is not { } type)
                {
                    return false;
                }

                switch (state.Locate(targetId))
                {
                    case { Kind: CreatorSelectionKind.Column }:
                        state.AddModule(type.Create(), targetId, index);
                        return true;
                    case { Kind: CreatorSelectionKind.Section, Section: { Layout: SectionLayout.FreeFlow } canvas } when CreatorFreeFlow.Allowed(type.Type):
                    {
                        var (w, h) = CreatorFreeFlow.DefaultSize(type.Type);
                        var step = 12 * (canvas.Items.Count % 10);
                        // A new item always goes in front, wherever it was dropped in the list.
                        return state.AddItem(canvas.Id, type.Create(), 24 + step, 24 + step, w, h) is not null;
                    }

                    default:
                        return false;
                }

            case "module" or "item" or "row":
                return state.Move(id, targetId, index);

            case "section":
                return state.Move(id, null, index);

            default:
                return false;
        }
    }

    /// <summary>
    /// Applies a drop at a point of a free-flow canvas: an Insert tile (<c>new</c>, <paramref name="id"/> is the type)
    /// creates a module of its default size with its top-left at the point; a module or item moves there.
    /// </summary>
    /// <param name="state">The editing session.</param>
    /// <param name="kind"><c>new</c>, <c>module</c> or <c>item</c>.</param>
    /// <param name="id">The module type for <c>new</c>, else the module id.</param>
    /// <param name="sectionId">The free-flow section.</param>
    /// <param name="xPt">Drop point from the canvas's left, in points.</param>
    /// <param name="yPt">Drop point from the canvas's top, in points.</param>
    /// <returns>True when the publication changed.</returns>
    public static bool Place(CreatorState state, string kind, string id, string sectionId, double xPt, double yPt)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (kind == "new")
        {
            if (CreatorModuleCatalog.Find(id) is not { } type || !CreatorFreeFlow.Allowed(type.Type))
            {
                return false;
            }

            var (w, h) = CreatorFreeFlow.DefaultSize(type.Type);
            return state.AddItem(sectionId, type.Create(), xPt, yPt, w, h) is not null;
        }

        return kind is "module" or "item" && state.PlaceOnCanvas(id, sectionId, xPt, yPt);
    }
}
