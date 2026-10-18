using ImmichWin.Data;
using Microsoft.UI.Xaml;

namespace ImmichWin.App;

public sealed class ChangeRow(ChangeRecord c)
{
    public long Id { get; } = c.Id;
    public bool IsPendingRow { get; } = c.Type == "pending" && !c.Undone;
    public string Summary { get; } = c.Type switch
    {
        "tag" => $"{c.At.LocalDateTime:g}  tagged {c.Extra}  ({c.AssetId[..Math.Min(8, c.AssetId.Length)]}){(c.Undone ? "  — undone" : "")}",
        "album" => $"{c.At.LocalDateTime:g}  added to album {c.Extra}  ({c.AssetId[..Math.Min(8, c.AssetId.Length)]}){(c.Undone ? "  — undone" : "")}",
        "asset_created" => $"{c.At.LocalDateTime:g}  developed JPEG uploaded  ({c.AssetId[..Math.Min(8, c.AssetId.Length)]}){(c.Undone ? "  — undone" : "")}",
        "pending" => $"{c.At.LocalDateTime:g}  awaiting review  ({c.AssetId[..Math.Min(8, c.AssetId.Length)]}){(c.Undone ? "  — resolved" : "")}",
        _ => c.Type,
    };
    public string ActionText => IsPendingRow ? "Approve" : "Undo";
    public Visibility CanAct => c.Undone ? Visibility.Collapsed : Visibility.Visible;
    public Visibility IsPending => IsPendingRow ? Visibility.Visible : Visibility.Collapsed;
}
