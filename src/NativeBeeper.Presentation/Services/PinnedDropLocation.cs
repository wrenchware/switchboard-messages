namespace NativeBeeper.Presentation.Services;

public readonly record struct PinnedDropRow(string ChatId, double Top, double Bottom);
public readonly record struct PinnedDropLocation(string TargetId, bool After, double LineY)
{
    // Coordinates describe realized pinned rows in the visible list, including partially
    // clipped rows. Unpinned rows never participate, and the pointer must stay in the list.
    public static PinnedDropLocation? Find(IReadOnlyList<PinnedDropRow> rows, string draggedId,
        double pointerY, double viewportHeight)
    {
        if (rows.Count == 0 || !double.IsFinite(pointerY) || pointerY < 0 || pointerY > viewportHeight ||
            pointerY < rows[0].Top - 8 || pointerY > rows[^1].Bottom + 8) return null;
        var row = rows.MinBy(row => Math.Abs(pointerY - (row.Top + row.Bottom) / 2));
        if (row.ChatId == draggedId) return null;
        var after = pointerY >= (row.Top + row.Bottom) / 2;
        return new(row.ChatId, after, Math.Clamp(after ? row.Bottom : row.Top, 0, viewportHeight));
    }
}
